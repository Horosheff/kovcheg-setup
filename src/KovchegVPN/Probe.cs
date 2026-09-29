using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KovchegVPN;

public readonly record struct TapIf(string Name, string Description, string Ip, int Index, string Gateway);
public readonly record struct LanIf(string Name, string Description, string Ip, int Index, string Gateway);

public static class Probe
{
    private static readonly object TapGate = new();
    private static TapIf? _tapCache;
    private static DateTime _tapCacheAt = DateTime.MinValue;
    private static readonly TimeSpan TapTtl = TimeSpan.FromSeconds(3);

    private static HttpClient Client(string? proxyUrl, int timeoutSec)
    {
        var h = new SocketsHttpHandler();
        if (proxyUrl == null)
        {
            h.UseProxy = false;
            ApplyLanBind(h);
        }
        else
        {
            h.Proxy = new WebProxy(proxyUrl);
            h.UseProxy = true;
        }
        return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
    }

    // Чужой OpenVPN TAP. Для охоты/xray его НЕ bind: иначе Cursor живёт
    // только пока TAP включён. Критерий 1.9.76 — Kovcheg без чужого VPN.
    public static bool IsTapNic(NetworkInterface n)
    {
        if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            return false;
        string desc = n.Description ?? "";
        string name = n.Name ?? "";
        PhysicalAddress mac = PhysicalAddress.None;
        try { mac = n.GetPhysicalAddress(); } catch { }
        return LooksTapText(desc) || LooksTapText(name) || LooksTapMac(mac);
    }

    public static TapIf? FindTap()
    {
        lock (TapGate)
        {
            if (DateTime.UtcNow - _tapCacheAt < TapTtl)
                return _tapCache;
            _tapCache = ScanTap();
            _tapCacheAt = DateTime.UtcNow;
            return _tapCache;
        }
    }

    public static void InvalidateTap()
    {
        lock (TapGate)
        {
            _tapCache = null;
            _tapCacheAt = DateTime.MinValue;
        }
    }

    private static TapIf? ScanTap()
    {
        // TAP-Windows: имя часто «Подключение по локальной сети», .NET Status=Unknown,
        // IP не 10.105.* (у нас 10.126.65.222/10). Не требовать Up и не искать по имени.
        TapIf? slash = ScanSlash1();
        TapIf? nic = ScanTapNics();
        TapIf? wmi = nic == null || string.IsNullOrEmpty(nic.Value.Ip)
            ? ScanTapWmi()
            : null;
        TapIf? hit = FirstWithIp(nic, wmi, slash) ?? nic ?? wmi ?? slash;
        if (hit == null)
        {
            LogNicDump("miss");
            return null;
        }
        if (string.IsNullOrEmpty(hit.Value.Gateway) && slash != null)
            hit = hit.Value with { Gateway = slash.Value.Gateway };
        if (string.IsNullOrEmpty(hit.Value.Ip) && slash != null)
            hit = hit.Value with { Ip = slash.Value.Ip };
        if (string.IsNullOrEmpty(hit.Value.Ip))
        {
            Cfg.Log("probe: TAP без IPv4 — не bind");
            LogNicDump("no-ip");
            return null;
        }
        return hit;
    }

    private static TapIf? FirstWithIp(params TapIf?[] xs)
    {
        foreach (var x in xs)
        {
            if (x != null && !string.IsNullOrEmpty(x.Value.Ip))
                return x;
        }
        return null;
    }

    private static bool LooksTapText(string s) =>
        s.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("TAP-Win32", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("TAP-Win64", StringComparison.OrdinalIgnoreCase);

    private static bool LooksTapMac(PhysicalAddress mac)
    {
        byte[] b = mac.GetAddressBytes();
        return b.Length >= 2 && b[0] == 0x00 && b[1] == 0xFF;
    }

    private static bool LooksTapMac(string mac)
    {
        string s = mac.Replace(":", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal);
        return s.StartsWith("00FF", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SkipVirtual(string blob) =>
        blob.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
        blob.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
        blob.Contains("singbox", StringComparison.OrdinalIgnoreCase) ||
        blob.Contains("WireGuard", StringComparison.OrdinalIgnoreCase);

    private static bool UsableIpv4(string s) =>
        s != Cfg.TunIp && !s.StartsWith("169.254.", StringComparison.Ordinal);

    private static TapIf? ScanTapNics()
    {
        TapIf? found = null;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    // TAP-Windows часто Unknown в .NET при живом Get-NetAdapter Up.
                    if (n.OperationalStatus is OperationalStatus.Down
                        or OperationalStatus.NotPresent)
                        continue;
                    string desc = n.Description ?? "";
                    string name = n.Name ?? "";
                    string blob = name + " " + desc;
                    if (SkipVirtual(blob)) continue;
                    PhysicalAddress mac = PhysicalAddress.None;
                    try { mac = n.GetPhysicalAddress(); } catch { }
                    bool tap = LooksTapText(desc) || LooksTapText(name) || LooksTapMac(mac);
                    if (!tap) continue;
                    string ip = "";
                    string gw = "";
                    int idx = 0;
                    try
                    {
                        var p = n.GetIPProperties();
                        foreach (var a in p.UnicastAddresses)
                        {
                            if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            string s = a.Address.ToString();
                            if (!UsableIpv4(s)) continue;
                            ip = s;
                            break;
                        }
                        foreach (var g in p.GatewayAddresses)
                        {
                            if (g.Address?.AddressFamily == AddressFamily.InterNetwork)
                            {
                                gw = g.Address.ToString();
                                break;
                            }
                        }
                        try { idx = p.GetIPv4Properties()?.Index ?? 0; } catch { }
                    }
                    catch (Exception ex)
                    {
                        Cfg.Log($"probe: TAP nic «{name}» props {ex.Message}");
                    }
                    found = new TapIf(name, desc, ip, idx, gw);
                    Cfg.Log($"probe: TAP nic «{name}» desc={desc} status={n.OperationalStatus} ip={ip} gw={gw} mac={mac}");
                    break;
                }
                catch (Exception ex)
                {
                    Cfg.Log($"probe: nic scan {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Cfg.Log("probe: ScanTapNics " + ex.Message);
        }
        return found;
    }

    private static TapIf? ScanTapWmi()
    {
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT Description, InterfaceIndex, MACAddress, IPAddress, DefaultIPGateway " +
                "FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            foreach (ManagementObject mo in q.Get())
            {
                string desc = mo["Description"] as string ?? "";
                string mac = mo["MACAddress"] as string ?? "";
                if (!LooksTapText(desc) && !LooksTapMac(mac)) continue;
                string ip = "";
                if (mo["IPAddress"] is string[] ips)
                {
                    foreach (string s in ips)
                    {
                        if (!IPAddress.TryParse(s, out var a) ||
                            a.AddressFamily != AddressFamily.InterNetwork ||
                            !UsableIpv4(s))
                            continue;
                        ip = s;
                        break;
                    }
                }
                if (ip.Length == 0) continue;
                int idx = 0;
                try { idx = Convert.ToInt32(mo["InterfaceIndex"]); } catch { }
                string gw = "";
                if (mo["DefaultIPGateway"] is string[] gws)
                {
                    foreach (string s in gws)
                    {
                        if (IPAddress.TryParse(s, out var a) &&
                            a.AddressFamily == AddressFamily.InterNetwork)
                        {
                            gw = s;
                            break;
                        }
                    }
                }
                Cfg.Log($"probe: TAP WMI desc={desc} ip={ip} gw={gw} mac={mac}");
                return new TapIf(desc, desc, ip, idx, gw);
            }
        }
        catch (Exception ex)
        {
            Cfg.Log("probe: TAP WMI " + ex.Message);
        }
        return null;
    }

    // 0.0.0.0/1 — полный туннель OpenVPN. Не Kovcheg TUN (10.0.85.2).
    // Русский route print: колонки те же (адрес/маска/шлюз/интерфейс).
    private static TapIf? ScanSlash1()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("route.exe", "print -4")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (p == null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            foreach (string line in outp.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                if (parts[0] != "0.0.0.0" || parts[1] != "128.0.0.0") continue;
                string gw = parts[2];
                string iface = parts[3];
                if (!IPAddress.TryParse(gw, out var gwA) ||
                    gwA.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (!IPAddress.TryParse(iface, out var ifA) ||
                    ifA.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (gw == Cfg.TunGw || iface == Cfg.TunIp) continue;
                Cfg.Log($"probe: TAP /1 gw={gw} iface={iface}");
                return new TapIf("OpenVPN /1", "TAP via 0.0.0.0/1", iface, 0, gw);
            }
        }
        catch (Exception ex)
        {
            Cfg.Log("probe: TAP /1 " + ex.Message);
        }
        return null;
    }

    private static void LogNicDump(string why)
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                string ips = "";
                try
                {
                    ips = string.Join(",", n.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString()));
                }
                catch { ips = "?"; }
                string mac = "";
                try { mac = n.GetPhysicalAddress().ToString(); } catch { }
                Cfg.Log(
                    $"probe: nic-dump/{why} «{n.Name}» desc={n.Description} status={n.OperationalStatus} " +
                    $"type={n.NetworkInterfaceType} mac={mac} ip={ips}");
            }
        }
        catch (Exception ex)
        {
            Cfg.Log("probe: nic-dump " + ex.Message);
        }
    }

    // Tailscale CGNAT 100.64.0.0/10 на локальном адаптере. NoState / нет IP → нет.
    public static bool HasLocalCgNat()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = a.Address.GetAddressBytes();
                    if (b.Length >= 2 && b[0] == 100 && (b[1] & 0xC0) == 64)
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    public static void PrepareOpenVpnPath()
    {
        InvalidateTap();
        var tap = FindTap();
        if (tap != null)
            Cfg.Log(
                $"probe: чужой TAP «{tap.Value.Name}» ip={tap.Value.Ip} gw={tap.Value.Gateway} — " +
                "не bind. Охота с LAN, иначе Cursor умрёт когда TAP выключат");
        else
            Cfg.Log("probe: чужого TAP нет — охота с LAN");
        if (TunAdapterUp())
        {
            Cfg.Log("probe: Kovcheg TUN уже up — /32 на VPS не трогаю");
            return;
        }
        PinVpsViaLan();
    }

    // /32 через Ethernet, не через TAP. Даже если чужой /1 жив, охота
    // проверяет LigaLink — тот путь, который останется после выключения OpenVPN.
    public static void PinVpsViaLan()
    {
        DropVpsHostPins();
        var lan = FindLan();
        if (lan == null ||
            string.IsNullOrEmpty(lan.Value.Gateway) ||
            !IPAddress.TryParse(lan.Value.Gateway, out var gw) ||
            gw.AddressFamily != AddressFamily.InterNetwork)
        {
            Cfg.Log("probe: LAN gw нет — охота с системного маршрута, TAP не bind");
            return;
        }
        Cfg.Log(
            $"probe: LAN «{lan.Value.Name}» ip={lan.Value.Ip} gw={lan.Value.Gateway} " +
            $"if={lan.Value.Index} — /32 на VPS, TAP не bind");
        foreach (string ip in new[] { Cfg.ServerIp, Cfg.ServerIpB })
            RouteAddHost(ip, lan.Value.Gateway, lan.Value.Index);
    }

    private static void RouteAddHost(string ip, string gw, int ifIdx)
    {
        try
        {
            string extra = ifIdx > 0 ? $" metric 1 if {ifIdx}" : " metric 1";
            var p = Process.Start(new ProcessStartInfo(
                "route.exe", $"add {ip} mask 255.255.255.255 {gw}{extra}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (p == null) return;
            string outp = (p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(4000);
            Cfg.Log($"probe: route add {ip} via {gw} exit={p.ExitCode}" +
                    (string.IsNullOrEmpty(outp) ? "" : " " + Trunc(outp, 120)));
        }
        catch (Exception ex)
        {
            Cfg.Log($"probe: route add {ip} {ex.Message}");
        }
    }
    public static void DropVpsHostPins()
    {
        foreach (string ip in new[] { Cfg.ServerIp, Cfg.ServerIpB })
            RouteDelete(ip);
    }

    private static void RouteDelete(string ip)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("route.exe", "delete " + ip)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (p == null) return;
            string outp = (p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(4000);
            Cfg.Log($"probe: route delete {ip} exit={p.ExitCode}" +
                    (string.IsNullOrEmpty(outp) ? "" : " " + Trunc(outp, 120)));
        }
        catch (Exception ex)
        {
            Cfg.Log($"probe: route delete {ip} {ex.Message}");
        }
    }

    // Старое имя: 1.9.74–75 биндили TAP. Теперь LAN, TAP никогда.
    public static void ApplyTapBind(SocketsHttpHandler h) => ApplyLanBind(h);

    public static void ApplyLanBind(SocketsHttpHandler h)
    {
        string lan = LanBindIp();
        if (string.IsNullOrEmpty(lan) || !IPAddress.TryParse(lan, out var local))
            return;
        h.ConnectCallback = async (ctx, ct) =>
        {
            var ep = ctx.DnsEndPoint;
            IPAddress addr;
            if (!IPAddress.TryParse(ep.Host, out addr!))
            {
                var addrs = await Dns.GetHostAddressesAsync(ep.Host, ct).ConfigureAwait(false);
                addr = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? addrs[0];
            }
            if (addr.AddressFamily != AddressFamily.InterNetwork)
            {
                var s6 = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await s6.ConnectAsync(ep, ct).ConfigureAwait(false);
                    return new NetworkStream(s6, ownsSocket: true);
                }
                catch
                {
                    s6.Dispose();
                    throw;
                }
            }
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(new IPEndPoint(addr, ep.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    public static string LanBindIp()
    {
        var lan = FindLan();
        return lan == null ? "" : lan.Value.Ip;
    }

    public static LanIf? FindLan()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel) continue;
                    if (IsTapNic(n)) continue;
                    string blob = (n.Name ?? "") + " " + (n.Description ?? "");
                    if (SkipVirtual(blob)) continue;
                    var p = n.GetIPProperties();
                    string ip = "";
                    foreach (var a in p.UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = a.Address.ToString();
                        if (!UsableIpv4(s)) continue;
                        ip = s;
                        break;
                    }
                    if (ip.Length == 0) continue;
                    string gw = "";
                    foreach (var g in p.GatewayAddresses)
                    {
                        if (g.Address?.AddressFamily == AddressFamily.InterNetwork)
                        {
                            gw = g.Address.ToString();
                            break;
                        }
                    }
                    if (gw.Length == 0 || LooksForeignGw(gw)) continue;
                    int idx = 0;
                    try { idx = p.GetIPv4Properties()?.Index ?? 0; } catch { }
                    return new LanIf(n.Name ?? "", n.Description ?? "", ip, idx, gw);
                }
                catch (Exception ex)
                {
                    Cfg.Log($"probe: LAN nic {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Cfg.Log("probe: FindLan " + ex.Message);
        }
        return ScanLanDefaultRoute();
    }

    private static bool LooksForeignGw(string gw) =>
        gw.StartsWith("10.0.85.", StringComparison.Ordinal) ||
        gw.StartsWith("100.", StringComparison.Ordinal) ||
        LooksOpenVpnPoolGw(gw);

    // Пулы TAP, которые уже видели: 10.80.64.1 / 10.105.64.1 / 10.126.64.1.
    private static bool LooksOpenVpnPoolGw(string gw)
    {
        var p = gw.Split('.');
        return p.Length == 4 && p[0] == "10" && p[2] == "64" && p[3] == "1";
    }

    private static LanIf? ScanLanDefaultRoute()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("route.exe", "print -4")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (p == null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            var tap = FindTap();
            foreach (string line in outp.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                if (parts[0] != "0.0.0.0" || parts[1] != "0.0.0.0") continue;
                string gw = parts[2];
                string iface = parts[3];
                if (!IPAddress.TryParse(gw, out var gwA) ||
                    gwA.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (LooksForeignGw(gw)) continue;
                if (tap != null && gw == tap.Value.Gateway) continue;
                if (!IPAddress.TryParse(iface, out var ifA) ||
                    ifA.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (!UsableIpv4(iface)) continue;
                Cfg.Log($"probe: LAN /0 gw={gw} iface={iface}");
                return new LanIf("LAN /0", "default route", iface, 0, gw);
            }
        }
        catch (Exception ex)
        {
            Cfg.Log("probe: LAN /0 " + ex.Message);
        }
        return null;
    }

    // Несколько эхо-сервисов: падение одного не должно выглядеть как смерть ядра.
    // ipify с дома LigaLink травит в 8.47/8.6 — не первый в списке.
    private static readonly string[] EchoUrls =
    {
        "https://icanhazip.com",
        "https://checkip.amazonaws.com",
        "https://api.ipify.org"
    };
    private static int _echoIdx;

    // maxTries: сколько эхо-сервисов перебрать. Через мёртвый туннель каждая
    // попытка — отдельный TLS к VPS, поэтому сторожевые пробы ходят с maxTries=1.
    public static async Task<string?> ExitIp(string? proxyUrl, int timeoutSec = 8, int maxTries = 3)
    {
        int start = Volatile.Read(ref _echoIdx);
        int tries = Math.Clamp(maxTries, 1, EchoUrls.Length);
        string via = proxyUrl ?? "direct";
        for (int k = 0; k < tries; k++)
        {
            string url = EchoUrls[(start + k) % EchoUrls.Length];
            var sw = Stopwatch.StartNew();
            try
            {
                using var c = Client(proxyUrl, timeoutSec);
                string s = (await c.GetStringAsync(url).ConfigureAwait(false)).Trim();
                if (IPAddress.TryParse(s, out var ip))
                {
                    Cfg.Log($"probe: ExitIp {url} via {via} -> {s} {sw.ElapsedMilliseconds}мс");
                    return Normalize(ip, s);
                }
                Cfg.Log($"probe: ExitIp {url} via {via} не IP «{Trunc(s, 80)}» {sw.ElapsedMilliseconds}мс");
            }
            catch (TaskCanceledException)
            {
                Cfg.Log($"probe: ExitIp {url} via {via} TIMEOUT {timeoutSec}с ({sw.ElapsedMilliseconds}мс)");
                Volatile.Write(ref _echoIdx, (start + k + 1) % EchoUrls.Length);
            }
            catch (Exception ex)
            {
                Cfg.Log($"probe: ExitIp {url} via {via} FAIL {ex.GetType().Name}: {ex.Message} ({sw.ElapsedMilliseconds}мс)");
                Volatile.Write(ref _echoIdx, (start + k + 1) % EchoUrls.Length);
            }
        }
        return null;
    }

    // Выход по IPv6 VPS — тот же сервер; сравнения по коду идут с Cfg.ServerIp.
    private static string Normalize(IPAddress ip, string raw)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 &&
            ip.ToString().StartsWith(Cfg.ServerIp6Prefix, StringComparison.OrdinalIgnoreCase))
        {
            Cfg.Log($"probe: выход {raw} = IPv6 VPS, считаю {Cfg.ServerIp}");
            return Cfg.ServerIp;
        }
        return raw;
    }

    public static async Task<bool> TcpOpen(string host, int port, int timeoutMs = 1500)
    {
        string lan = LanBindIp();
        try
        {
            TcpClient tc;
            if (!string.IsNullOrEmpty(lan) && IPAddress.TryParse(lan, out var local))
                tc = new TcpClient(new IPEndPoint(local, 0));
            else
                tc = new TcpClient();
            using (tc)
            {
                var connect = tc.ConnectAsync(host, port);
                var done = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
                bool ok = done == connect && !connect.IsFaulted && tc.Connected;
                if (!ok && !string.IsNullOrEmpty(lan))
                    Cfg.Log($"probe: TcpOpen {host}:{port} нет (bind LAN {lan})");
                return ok;
            }
        }
        catch (Exception ex)
        {
            Cfg.Log($"probe: TcpOpen {host}:{port} {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> PortOpen(int port, int timeoutMs = 400)
    {
        try
        {
            using var tc = new TcpClient();
            var connect = tc.ConnectAsync("127.0.0.1", port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return done == connect && !connect.IsFaulted && tc.Connected;
        }
        catch { return false; }
    }

    public static async Task<int> HttpCode(string url, string? proxyUrl, int timeoutSec = 15)
    {
        var d = await HttpDetail(url, proxyUrl, timeoutSec).ConfigureAwait(false);
        return d.Code;
    }

    // Код + причина: раньше -1 глотался, и в логе было «instagram -> -1» без exception.
    public static async Task<HttpSnap> HttpDetail(
        string url, string? proxyUrl, int timeoutSec = 8, Version? httpVer = null)
    {
        var sw = Stopwatch.StartNew();
        string via = proxyUrl ?? "TUN/direct";
        string ver = httpVer == null ? "auto" : httpVer.ToString();
        try
        {
            using var c = Client(proxyUrl, timeoutSec);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) KovchegDiag/1.9.64");
            if (httpVer != null)
            {
                req.Version = httpVer;
                req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }
            using var r = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            string loc = r.Headers.Location?.ToString() ?? "";
            long? len = r.Content.Headers.ContentLength;
            string detail =
                $"HTTP {(int)r.StatusCode} {r.ReasonPhrase} h={r.Version} want={ver} " +
                $"{sw.ElapsedMilliseconds}ms loc={Trunc(loc, 80)} len={len?.ToString() ?? "?"} via {via}";
            return new HttpSnap((int)r.StatusCode, (int)sw.ElapsedMilliseconds, detail);
        }
        catch (TaskCanceledException)
        {
            string detail = $"TIMEOUT {sw.ElapsedMilliseconds}ms want={ver} via {via}";
            return new HttpSnap(-1, (int)sw.ElapsedMilliseconds, detail);
        }
        catch (Exception ex)
        {
            string inner = ex.InnerException == null ? "" : " | " + ex.InnerException.Message;
            string detail =
                $"FAIL {sw.ElapsedMilliseconds}ms want={ver} via {via} {ex.GetType().Name}: {ex.Message}{inner}";
            return new HttpSnap(-1, (int)sw.ElapsedMilliseconds, detail);
        }
    }

    public static bool HttpLooksUp(HttpSnap s) => s.Code is >= 200 and < 400;

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "-" : (s.Length <= n ? s : s[..n] + "…");

    public static bool TunAdapterUp()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork &&
                        a.Address.ToString() == Cfg.TunIp)
                        return true;
            }
        }
        catch { }
        return false;
    }

    // Глобальный IPv6 (не link-local / ULA / Teredo). Без него ss6/xhttp6
    // умирают за 0,3 с — нечего гонять в охоте.
    public static bool HasGlobalIpv6()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                {
                    var ip = a.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetworkV6) continue;
                    if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6Teredo)
                        continue;
                    byte[] b = ip.GetAddressBytes();
                    if (b.Length >= 1 && (b[0] & 0xfe) == 0xfc) continue; // fc00::/7 ULA
                    if (ip.Equals(IPAddress.IPv6Loopback)) continue;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    // IPv4 LAN-интерфейса со шлюзом — чтобы не путать «тот же 192.168.0.1»
    // в другой сети с домашней, где прямые трубы уже мертвы.
    public static string LanIpv4()
    {
        string ip = LanBindIp();
        if (ip.Length > 0) return ip;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;
                if (IsTapNic(n)) continue;
                var props = n.GetIPProperties();
                bool hasGw = false;
                foreach (var g in props.GatewayAddresses)
                {
                    if (g.Address?.AddressFamily == AddressFamily.InterNetwork)
                    {
                        hasGw = true;
                        break;
                    }
                }
                if (!hasGw) continue;
                foreach (var a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string s = a.Address.ToString();
                    if (!UsableIpv4(s)) continue;
                    return s;
                }
            }
        }
        catch { }
        return "";
    }

    // Базовый RTT до VPS. ICMP, не TCP:443 — коннект на Reality-порт
    // сторож принимал за ClientHello, и ТСПУ копил «фейковые» хендшейки.
    public static int BaseRttMs()
    {
        try
        {
            using var p = new Ping();
            var r = p.Send(Cfg.ServerIp, 2000);
            if (r.Status == IPStatus.Success) return (int)r.RoundtripTime;
        }
        catch { }
        return -1;
    }
}

public readonly record struct HttpSnap(int Code, int Ms, string Detail);
