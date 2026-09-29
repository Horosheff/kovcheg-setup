using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KovchegVPN;

// KovchegVPN.exe --elevated tun-on|tun-off. Без WPF, без окна.
// Каждый шаг в kovcheg.log, итог в tun-result.txt. Watchdog гасит процесс всегда.
public static class ElevatedWorker
{
    // Худший путь tun-on: охота SS ~80с + sing-box. После OK сторож нельзя откатывать живой TUN.
    private const int WatchdogSec = 240;
    private static System.Threading.Timer? _watchdog;
    private static volatile bool _finished;

    public static int Run(string mode)
    {
        Directory.CreateDirectory(Cfg.Dir);
        _finished = false;

        using var watchdog = new System.Threading.Timer(_ =>
        {
            if (_finished) return;
            try
            {
                if (File.Exists(Cfg.ResultPath) &&
                    File.ReadAllText(Cfg.ResultPath).StartsWith("OK|", StringComparison.Ordinal))
                {
                    Cfg.Log("elevated: WATCHDOG — TUN уже готов, не откатываю");
                    Environment.Exit(0);
                    return;
                }
            }
            catch { }
            Cfg.Log("elevated: WATCHDOG timeout — откатываю и выхожу");
            // Без отката полу-поднятый TUN = чёрная дыра без интернета.
            try { if (mode == "tun-on") Rollback(); } catch { }
            try { File.WriteAllText(Cfg.ResultPath, $"FAIL|worker молчал {WatchdogSec}с — откат, аварийный выход"); } catch { }
            Environment.Exit(2);
        }, null, WatchdogSec * 1000, Timeout.Infinite);
        _watchdog = watchdog;

        try
        {
            using var mtx = new Mutex(false, "KovchegVPN.Elevated", out _);
            if (!mtx.WaitOne(0))
            {
                Cfg.Log("elevated: второй worker не стартую (mutex)");
                return Fail("уже работает другой worker — подожди 10с");
            }

            KillZombieWorkers();
            Cfg.ExtractAssets();
            Cfg.Log($"elevated {mode}: start (worker v{LogBus.Version})");

            int code = mode switch
            {
                "tun-on" => TunOn(),
                "tun-off" => TunOff(),
                _ => Fail($"неизвестный режим {mode}"),
            };
            Cfg.Log($"elevated {mode}: exit {code}");
            return code;
        }
        catch (Exception ex)
        {
            Cfg.Log("elevated crash: " + ex);
            return Fail(ex.Message);
        }
    }

    private static int TunOn()
    {
        var sw = Stopwatch.StartNew();

        // Быстрый путь: туннель уже жив — ничего не трогаем.
        string? quick = Probe.ExitIp(null, 3).Result;
        if (Cfg.IsOurs(quick) && Probe.TunAdapterUp())
        {
            Core.KillOtherXray();
            Cfg.Log($"tun-on: уже подключено, fast path {sw.ElapsedMilliseconds}мс");
            return Ok("Уже подключено.");
        }

        KillSingBox();
        Core.KillOtherXray();
        Cfg.Log($"tun-on: старый sing-box погашен ({sw.ElapsedMilliseconds}мс)");
        Cfg.Log("tun-on: маршруты ДО: " + RouteLines());

        CleanGhosts();
        PinLanGateway();
        Cfg.Log($"tun-on: чистка сделана ({sw.ElapsedMilliseconds}мс), адаптер up=" + Probe.TunAdapterUp() +
                $", gw={LanGateway()}");

        // Ядро для TUN: своё 10818 (xmux — меньше пинг) → 10808 → http-резерв.
        string obType = "";
        int obPort = 0;
        string? ip = Probe.ExitIp($"socks5://127.0.0.1:{Cfg.OwnSocksPort}", 3).Result;
        Cfg.Log($"tun-on: probe socks {Cfg.OwnSocksPort} -> {ip ?? "нет"}");
        if (Cfg.IsOurs(ip)) { obType = "socks"; obPort = Cfg.OwnSocksPort; }
        else
        {
            Cfg.Log("tun-on: поднимаю свой xray 10818/10819");
            string? brought = Core.EnsureAsync().GetAwaiter().GetResult();
            Cfg.Log($"tun-on: EnsureAsync -> {brought ?? "нет"} ({sw.ElapsedMilliseconds}мс)");
            if (brought != null)
            {
                ip = Probe.ExitIp($"socks5://127.0.0.1:{Cfg.OwnSocksPort}", 4).Result;
                if (Cfg.IsOurs(ip)) { obType = "socks"; obPort = Cfg.OwnSocksPort; }
            }
            Cfg.Log($"tun-on: свой xray -> {(obPort != 0 ? "жив" : "мёртв")} ({sw.ElapsedMilliseconds}мс)");
        }
        if (obPort == 0)
        {
            ip = Probe.ExitIp($"socks5://127.0.0.1:{Cfg.SocksPort}", 4).Result;
            Cfg.Log($"tun-on: probe socks {Cfg.SocksPort} -> {ip ?? "нет"}");
            if (Cfg.IsOurs(ip)) { obType = "socks"; obPort = Cfg.SocksPort; }
        }
        if (obPort == 0)
        {
            var probes = new[] { 12809, 10809, Cfg.OwnHttpPort }.Select(p => Task.Run(async () =>
                (p, ip2: await Probe.ExitIp($"http://127.0.0.1:{p}", 4)))).ToArray();
            foreach (var (p, ip2) in Task.WhenAll(probes).Result)
            {
                Cfg.Log($"tun-on: probe http {p} -> {ip2 ?? "нет"}");
                if (obPort == 0 && Cfg.IsOurs(ip2)) { obType = "http"; obPort = p; }
            }
        }
        if (obPort == 0)
            return Fail("ядро молчит: 10808/10818/12809/10809/10819 не выдают наш выход");

        string? sbExe = Cfg.SingBoxExe();
        Cfg.Log($"tun-on: sing-box = {sbExe ?? "НЕ НАЙДЕН"}");
        if (sbExe == null)
        {
            string bin = "(папки bin нет)";
            try
            {
                if (Directory.Exists(Cfg.BinDir))
                    bin = string.Join(",", Directory.GetFiles(Cfg.BinDir).Select(Path.GetFileName));
            }
            catch { }
            Cfg.Log("tun-on: v2rayN=" + (Cfg.V2rayNDir() ?? "нет") + ", bin=[" + bin + "]");
            return Fail("не найден sing-box (ни в v2rayN, ни в своих ядрах) — нажми ещё раз, докачаю");
        }
        DetectSingBoxVersion(sbExe);

        // 2 = новый формат без fake-ip (1.9.44: эхо через TUN жило).
        // 3 = fake-ip: LigaLink иногда молчит ~2 мин — ложный откат.
        int dnsMode = _sbVersion >= new Version(1, 13, 0) ? 2 : 1;
        string? socksProof = ip;
        try
        {
            WriteSbConfig(obType, obPort, dnsMode, stack: "system");
            Cfg.Log($"tun-on: конфиг -> {obType} 127.0.0.1:{obPort}, dns-mode {dnsMode}, stack system");
        }
        catch (Exception ex) { return Fail("конфиг sing-box: " + ex.Message); }

        EnsureEscapeRoutes("до TUN");

        string? exit = StartAndVerify(sw, obType, obPort, socksProof);
        if (exit == null && _hostRouteMissing)
        {
            EnsureEscapeRoutes("ещё раз после miss");
            if (TrustedTunReady(obType, obPort, socksProof))
                return Success(sw, obType, obPort);
            Cfg.Log("tun-on: host-route так и нет — дальше крутить DNS бесполезно");
            string hostFail = Markers() + " маршруты: " + RouteLines();
            Cfg.Log("tun-on: FAIL host-route. " + hostFail);
            Rollback();
            return Fail($"выход без прокси = нет вместо {Cfg.ServerIp} {hostFail} — детали в kovcheg.log");
        }
        if (exit == null && _sbConfigError)
        {
            Cfg.Log("tun-on: sing-box отклонил конфиг — меняю формат DNS и повторяю");
            KillSingBox();
            CleanGhosts();
            Thread.Sleep(500);
            // 1.13.19 убивает legacy DNS без env. Не падаем в dns-mode 0:
            // без hijack Windows снова спрашивает роутер, LigaLink травит в 8.47.
            if (_sbVersion >= new Version(1, 13, 0))
            {
                dnsMode = dnsMode == 2 ? 4 : 2; // 2=UDP через proxy, 4=DoT если UDP-конфиг отклонили
                WriteSbConfig(obType, obPort, dnsMode, stack: "system");
                Cfg.Log($"tun-on: sing-box {_sbVersion} — dns-mode {dnsMode}, stack system");
            }
            else
            {
                dnsMode = dnsMode >= 2 ? 1 : 2;
                WriteSbConfig(obType, obPort, dnsMode, stack: "system");
                Cfg.Log("tun-on: gvisor не принялся — stack system");
            }
            exit = StartAndVerify(sw, obType, obPort, socksProof);
        }
        if (Cfg.IsOurs(exit))
            return Success(sw, obType, obPort);

        if (TrustedTunReady(obType, obPort, socksProof))
            return Success(sw, obType, obPort);

        if (Probe.TunAdapterUp() && SocksIsOurs(obType, obPort))
        {
            Cfg.Log("tun-on: эхо молчит, адаптер и socks живы — TUN оставляю (LigaLink травит HTTPS-проверку)");
            return Success(sw, obType, obPort);
        }

        // fake-ip HTTPS часто молчит, а без него Windows спрашивает DNS роутера
        // (192.168.0.1) — LigaLink травит ipify в 8.47/8.6. Один раз без fake-ip
        // и icanhazip; не крутим 4 перезапуска по 40с.
        if (dnsMode == 3)
        {
            Cfg.Log("tun-on: fake-ip не дал эхо — DNS без fake-ip, icanhazip/amazonaws");
            KillSingBox();
            CleanGhosts();
            Thread.Sleep(400);
            dnsMode = 2;
            WriteSbConfig(obType, obPort, dnsMode);
            exit = StartAndVerify(sw, obType, obPort, socksProof);
            if (Cfg.IsOurs(exit))
                return Success(sw, obType, obPort);
            if (TrustedTunReady(obType, obPort, socksProof))
                return Success(sw, obType, obPort);
        }

        if (Probe.TunAdapterUp())
        {
            try
            {
                var ig = Probe.HttpDetail("https://www.instagram.com/", null, 8, HttpVersion.Version11)
                    .GetAwaiter().GetResult();
                Cfg.Log($"tun-on: instagram через TUN -> {ig.Detail}");
                string coreUrl = obType == "http"
                    ? $"http://127.0.0.1:{obPort}"
                    : $"socks5://127.0.0.1:{obPort}";
                if (Probe.HttpLooksUp(ig) && Cfg.IsOurs(Probe.ExitIp(coreUrl, 4, maxTries: 1).GetAwaiter().GetResult()))
                {
                    Cfg.Log("tun-on: эхо отравлен, Instagram 200 и socks жив — TUN считаю поднятым");
                    return Success(sw, obType, obPort);
                }
            }
            catch { }
        }

        // Только теперь откат. Маркеры ДО rollback.
        string markers = Markers();
        string routes = RouteLines();
        Cfg.Log("tun-on: FAIL окончательно. " + markers + " маршруты: " + routes);
        Rollback();
        return Fail($"выход без прокси = {exit ?? "нет"} вместо {Cfg.ServerIp} {markers} — детали в kovcheg.log");
    }

    private static int Success(Stopwatch sw, string obType, int obPort)
    {
        string udpNote = obType == "http" ? " UDP/голос может не идти: ядро без socks." : "";
        // Сначала OK на диск и снять сторож: logsend/instagram не должны
        // откатить уже живой TUN (1.9.42: ss-b встал, сторож убил через 200с).
        int code = Ok($"Весь ПК через {Cfg.ExitCountry} ({obType} :{obPort}).{udpNote}");
        Cfg.Log($"tun-on: готово за {sw.ElapsedMilliseconds}мс transport={Core.Transport}");
        try { Diag.Snapshot("tun-on"); }
        catch (Exception ex) { Cfg.Log("diag: " + ex.Message); }
        try { LogSender.Send(6); }
        catch (Exception ex) { Cfg.Log("logsend: " + ex.Message); }
        return code;
    }

    private static bool SocksIsOurs(string obType, int obPort)
    {
        string coreUrl = obType == "http"
            ? $"http://127.0.0.1:{obPort}"
            : $"socks5://127.0.0.1:{obPort}";
        return Cfg.IsOurs(Probe.ExitIp(coreUrl, 4, maxTries: 1).GetAwaiter().GetResult());
    }

    private static bool TrustedTunReady(string obType, int obPort, string? socksProof)
    {
        if (!Probe.TunAdapterUp()) return false;
        // После PinLanDns HttpClient резолвит имена локально и висит 15с.
        // socksProof снят ДО pin — его достаточно.
        if (!Cfg.IsOurs(socksProof) && !SocksIsOurs(obType, obPort)) return false;
        EnsureEscapeRoutes("trusted");
        bool hostA = HasHostRoute(Cfg.ServerIp);
        bool hostB = HasHostRoute(Cfg.ServerIpB);
        if (!hostA && !hostB)
        {
            Cfg.Log("tun-on: socks жив, но ни одного host-route — всё равно оставляю, xray на loopback");
        }
        Cfg.Log($"tun-on: эхо молчит, socks до TUN={socksProof ?? "?"}, host A={hostA} B={hostB} — TUN оставляю");
        return true;
    }

    private static bool _sbConfigError;
    private static bool _hostRouteMissing;
    private static readonly StringBuilder SbConsole = new();

    // Старт sing-box, ожидание адаптера, диагностика, ipify. null или IP.
    private static string? StartAndVerify(Stopwatch sw, string obType, int obPort, string? socksProof)
    {
        _sbConfigError = false;
        _hostRouteMissing = false;
        string? sb = Cfg.SingBoxExe();
        if (sb == null) { Cfg.Log("tun-on: sing-box НЕ НАЙДЕН"); return null; }

        Process? sbProc;
        try
        {
            lock (SbConsole) SbConsole.Clear();
            sbProc = Process.Start(new ProcessStartInfo(sb, $"run -c \"{Cfg.SbConfigPath}\"")
            {
                WorkingDirectory = Cfg.Dir,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (sbProc != null)
            {
                sbProc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (SbConsole) SbConsole.AppendLine(e.Data); };
                sbProc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (SbConsole) SbConsole.AppendLine(e.Data); };
                sbProc.BeginOutputReadLine();
                sbProc.BeginErrorReadLine();
            }
            Cfg.Log($"tun-on: sing-box pid {sbProc?.Id.ToString() ?? "?"} ({sw.ElapsedMilliseconds}мс)");
        }
        catch (Exception ex) { Cfg.Log("tun-on: sing-box не стартовал: " + ex.Message); return null; }

        bool up = false;
        for (int i = 0; i < 80 && !up; i++)
        {
            Thread.Sleep(250);
            up = Probe.TunAdapterUp();
            if (sbProc is { HasExited: true })
            {
                string tail = LogTail();
                Cfg.Log($"tun-on: sing-box умер, код {sbProc.ExitCode}. {tail}");
                string tl = tail.ToLowerInvariant();
                _sbConfigError = tl.Contains("fatal") &&
                    (tl.Contains("dns") || tl.Contains("config") || tl.Contains("invalid") ||
                     tl.Contains("unknown") || tl.Contains("deprecated") || tl.Contains("removed"));
                return null;
            }
        }
        if (!up) { Cfg.Log($"tun-on: адаптер {Cfg.TunIp} не встал за 20с"); return null; }
        Cfg.Log($"tun-on: адаптер up ({sw.ElapsedMilliseconds}мс)");

        // sing-box auto_route сносит /32 — без них xray/DNS уходят в TUN и петля.
        EnsureEscapeRoutes("сразу после адаптера");
        bool hostA = HasHostRoute(Cfg.ServerIp);
        bool hostB = HasHostRoute(Cfg.ServerIpB);
        if (!hostA && !hostB)
        {
            _hostRouteMissing = true;
            Cfg.Log("tun-on: без host-route /1 не ставлю — иначе петля");
            return null;
        }
        if (!hostA || !hostB)
            Cfg.Log($"tun-on: host-route частично A={hostA} B={hostB} — /1 ставлю, xray на loopback");

        // Явные /1 через tun СРАЗУ: cover-set sing-box на этой машине даёт чёрную дыру.
        Route("add 0.0.0.0 mask 128.0.0.0 10.0.85.2 metric 1");
        Route("add 128.0.0.0 mask 128.0.0.0 10.0.85.2 metric 1");
        Cfg.Log($"tun-on: /1 маршруты добавлены ({sw.ElapsedMilliseconds}мс)");
        EnsureEscapeRoutes("после /1");
        EnsurePoisonBlock();
        PinLanDns();
        FlushDns();

        Cfg.Log("tun-on: адрес адаптера: " + PsShow(
            "Get-NetIPAddress -InterfaceAlias 'singbox_tun' -AddressFamily IPv4 -ErrorAction SilentlyContinue | " +
            "Select-Object IPAddress,AddressState | Format-Table -HideTableHeaders"));
        Cfg.Log("tun-on: host-route виден: " + HasHostRoute(Cfg.ServerIp) + " gw=" + LanGateway());

        // Не резолвим имена и не долбим эхо: после pin системный DNS ещё
        // греется, HttpClient висит по 12–15с, UI «подключается две минуты».
        if (Probe.TunAdapterUp() && Cfg.IsOurs(socksProof))
        {
            Cfg.Log("tun-on: socks жив до pin, адаптер up — не жду эхо");
            return socksProof;
        }

        if (TrustedTunReady(obType, obPort, socksProof))
            return socksProof;

        string? exit = null;
        for (int i = 0; i < 2; i++)
        {
            EnsureEscapeRoutes($"проба {i + 1}");
            exit = Probe.ExitIp(null, 6, maxTries: 2).GetAwaiter().GetResult();
            Cfg.Log($"tun-on: echo TUN [{i + 1}] -> {exit ?? "нет"} ({sw.ElapsedMilliseconds}мс)");
            if (Cfg.IsOurs(exit)) break;
            if (TrustedTunReady(obType, obPort, socksProof))
                return socksProof;
        }
        return exit;
    }

    private static readonly List<string> EscapeIps = new();
    private static readonly List<string> GwCandidates = new();
    private static string? _lanGw;

    private static string LanGateway() => _lanGw ?? Cfg.Gateway;

    // Реальный LAN-шлюз. Дефолт 192.168.0.1 на части роутеров не существует —
    // тогда `route add VPS via 192.168.0.1` молча не встаёт, host=нет, TUN петля.
    private static void PinLanGateway()
    {
        GwCandidates.Clear();
        GwCandidates.AddRange(DetectLanGateways());
        string? live = GwCandidates.Count > 0 ? GwCandidates[0] : null;
        string? ini = Cfg.IniGet("Gateway");
        Cfg.Log($"tun-on: LAN gw detect={live ?? "нет"} ini={ini ?? "нет"} candidates=[{string.Join(",", GwCandidates)}]");
        if (live != null)
        {
            _lanGw = live;
            if (live != ini)
            {
                Cfg.IniSet("Gateway", live);
                Cfg.Log($"tun-on: запомнил Gateway={live}");
            }
        }
        else
            Cfg.Log("tun-on: шлюз не нашёл, беру " + Cfg.Gateway);
    }

    private static List<string> DetectLanGateways()
    {
        var ordered = new List<(int Metric, string Gw)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Consider(string gw, int metric, string src)
        {
            if (gw is "0.0.0.0" or "127.0.0.1" or "On-link" or "on-link") return;
            if (gw.StartsWith("10.0.85.", StringComparison.Ordinal)) return;
            if (gw.StartsWith("100.", StringComparison.Ordinal)) return;
            var octets = gw.Split('.');
            if (octets.Length == 4 && octets[0] == "10" && octets[2] == "64" && octets[3] == "1")
                return;
            if (!IPAddress.TryParse(gw, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return;
            if (!seen.Add(gw)) return;
            ordered.Add((metric, gw));
            Cfg.Log($"tun-on: LAN gw кандидат {gw} metric {metric} ({src})");
        }

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                if (Probe.IsTapNic(ni))
                {
                    Cfg.Log($"tun-on: LAN gw skip TAP «{ni.Name}»");
                    continue;
                }
                var props = ni.GetIPProperties();
                if (props.UnicastAddresses.Any(a => a.Address.ToString() == Cfg.TunIp))
                    continue;
                int idx = 0;
                try { idx = props.GetIPv4Properties().Index; } catch { }
                foreach (var g in props.GatewayAddresses)
                {
                    if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    Consider(g.Address.ToString(), 40, $"{ni.Name} if {idx}");
                }
            }
        }
        catch (Exception ex) { Cfg.Log("tun-on: detect gw " + ex.Message); }

        try
        {
            var p = Process.Start(new ProcessStartInfo("route.exe", "print -4")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p != null)
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                foreach (string l in outp.Split('\n'))
                {
                    string t = l.Trim();
                    var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;
                    if (parts[0] != "0.0.0.0" || parts[1] != "0.0.0.0") continue;
                    int metric = 50;
                    if (parts.Length >= 5 && int.TryParse(parts[4], out int m)) metric = m;
                    Consider(parts[2], metric, "route print");
                }
            }
        }
        catch (Exception ex) { Cfg.Log("tun-on: route print gw " + ex.Message); }

        return ordered.OrderBy(x => x.Metric).Select(x => x.Gw).ToList();
    }

    private static IEnumerable<string> CollectEscapeIps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Cfg.ServerIp, Cfg.ServerIpB, Cfg.ServerIp6
        };
        Cfg.Log("tun-on: escape /32 " + string.Join(",", set));
        return set;
    }

    private static IEnumerable<string> GatewayTries()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_lanGw) && seen.Add(_lanGw)) yield return _lanGw;
        if (seen.Add(Cfg.Gateway)) yield return Cfg.Gateway;
        foreach (string g in GwCandidates)
            if (seen.Add(g)) yield return g;
    }

    // /32 через LAN-шлюз. Наши /1 ловят всё, что без более узкого маршрута —
    // без них Reality/CF/DNS уходят в TUN и петля. auto_route их ещё и стирает.
    private static bool EnsureEscapeRoutes(string when)
    {
        if (EscapeIps.Count == 0)
            EscapeIps.AddRange(CollectEscapeIps());
        foreach (string gw in GatewayTries())
        {
            int ifIdx = LanIfIndex(gw);
            bool all = true;
            foreach (string ip in EscapeIps)
            {
                if (ip.Contains(':')) continue;
                if (!EnsureOneHostRoute(ip, gw, ifIdx, when))
                    all = false;
            }
            if (!HasHostRoute(Cfg.ServerIp) && !HasHostRoute(Cfg.ServerIpB)) continue;
            if (_lanGw != gw)
            {
                _lanGw = gw;
                Cfg.IniSet("Gateway", gw);
                Cfg.Log($"tun-on: рабочий Gateway={gw}");
            }
            return all;
        }
        Cfg.Log($"tun-on: host-route {Cfg.ServerIp}/{Cfg.ServerIpB} {when} так и нет");
        return false;
    }

    private static bool EnsureOneHostRoute(string ip, string gw, int ifIdx, string when)
    {
        if (HasHostRoute(ip, gw))
            return true;
        var extras = new List<string>();
        if (ifIdx > 0) extras.Add($" metric 1 if {ifIdx}");
        extras.Add(" metric 1");
        extras.Add("");
        foreach (string extra in extras)
        {
            Route($"delete {ip}");
            RouteLogged($"add {ip} mask 255.255.255.255 {gw}{extra}");
            Thread.Sleep(150);
            if (HasHostRoute(ip, gw))
            {
                Cfg.Log($"tun-on: host-route {ip} {when} на месте (gw={gw}{extra})");
                return true;
            }
        }
        Cfg.Log($"tun-on: host-route {ip} {when} НЕТ (gw={gw} if={ifIdx})");
        return false;
    }

    private static int LanIfIndex(string gw)
    {
        if (!IPAddress.TryParse(gw, out var gwIp) || gwIp.AddressFamily != AddressFamily.InterNetwork)
            return 0;
        byte[] gwb = gwIp.GetAddressBytes();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (Probe.IsTapNic(ni)) continue;
                var props = ni.GetIPProperties();
                if (props.UnicastAddresses.Any(a => a.Address.ToString() == Cfg.TunIp))
                    continue;
                int idx;
                try { idx = props.GetIPv4Properties().Index; } catch { continue; }
                if (props.GatewayAddresses.Any(g => g.Address.ToString() == gw))
                    return idx;
                foreach (var u in props.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] ub = u.Address.GetAddressBytes();
                    byte[] mask = u.IPv4Mask?.GetAddressBytes() ?? new byte[] { 255, 255, 255, 0 };
                    bool same = true;
                    for (int i = 0; i < 4; i++)
                        if ((ub[i] & mask[i]) != (gwb[i] & mask[i])) { same = false; break; }
                    if (same) return idx;
                }
            }
        }
        catch { }
        return 0;
    }

    private static bool HasHostRoute(string ip, string? via = null)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("route.exe", "print -4")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p == null) return false;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            foreach (string l in outp.Split('\n'))
            {
                var parts = l.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || parts[0] != ip) continue;
                if (via == null || (parts.Length >= 3 && parts[2] == via)) return true;
            }
        }
        catch { }
        return false;
    }

    private static void ClearEscapeRoutes()
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Cfg.ServerIp, "1.1.1.1", "1.0.0.1" };
        try
        {
            if (EscapeIps.Count == 0)
                EscapeIps.AddRange(CollectEscapeIps());
        }
        catch { }
        foreach (string ip in EscapeIps) ips.Add(ip);
        foreach (string ip in ips)
        {
            if (ip.Contains(':')) continue;
            Route($"delete {ip}");
        }
    }

    private static void CleanGhosts()
    {
        DeleteKovchegSlash1();
        Netsh($"interface ipv4 delete address name=\"singbox_tun\" addr={Cfg.TunIp}");
    }

    private static string PsShow(string cmd)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{cmd}\"")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p == null) return "?";
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            string s = string.Join(" ; ", outp.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            return s.Length > 400 ? s[..400] : s;
        }
        catch { return "?"; }
    }

    private static int TunOff()
    {
        KillSingBox();
        Cfg.Log("tun-off: sing-box погашен");
        DeleteKovchegSlash1();
        ClearEscapeRoutes();
        ClearPoisonBlock();
        Netsh($"interface ipv4 delete address name=\"singbox_tun\" addr={Cfg.TunIp}");
        RestoreLanDns();
        RestoreLanIpv6();
        RestoreTailscaleDns();
        Cfg.Log("tun-off: маршруты сняты, адрес с адаптера убран. " + RouteLines());

        string? direct = Probe.ExitIp(null, 10).Result;
        Cfg.Log($"tun-off: direct -> {direct ?? "нет"}");
        if (direct == null) return Ok("TUN снят. Прямой выход не ответил — проверь сеть.");
        if (Cfg.IsOurs(direct)) return Fail("маршрут застрял: прямой выход всё ещё наш VPS");
        return Ok("TUN снят, интернет жив.");
    }

    private static string Markers()
    {
        string r = RouteLines();
        bool slash1 = r.Contains("128.0.0.0");
        bool host = HasHostRoute(Cfg.ServerIp);
        bool tun = Probe.TunAdapterUp();
        return $"[адаптер={(tun ? "up" : "нет")} /1={(slash1 ? "да" : "нет")} host={(host ? "да" : "нет")} gw={LanGateway()}]";
    }

    private static string RouteLines()
    {
        var sb2 = new StringBuilder();
        try
        {
            var p = Process.Start(new ProcessStartInfo("route.exe", "print -4")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p == null) return "?";
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            foreach (string l in outp.Split('\n'))
            {
                string t = l.Trim();
                if (t.StartsWith("0.0.0.0") || t.Contains("128.0.0.0") ||
                    t.Contains(Cfg.ServerIp) || t.Contains("10.0.85") ||
                    t.Contains("1.1.1.1") || t.Contains("1.0.0.1") ||
                    t.Contains("8.47.") || t.Contains("8.6.") ||
                    EscapeIps.Any(ip => t.Contains(ip, StringComparison.Ordinal)))
                    sb2.Append(t).Append(" ; ");
            }
        }
        catch { }
        string s = sb2.ToString();
        return s.Length > 600 ? s[..600] : s;
    }

    // dnsMode: 0 = без DNS-hijack (как 1.5.6), 1 = legacy,
    // 2 = UDP DNS через proxy (1.13+), 4 = DoT через proxy (если UDP отклонили).
    private static void WriteSbConfig(string obType, int obPort, int dnsMode, string? stack = null)
    {
        using var s = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("KovchegVPN.Resources.singbox-tun.json")!;
        var node = JsonNode.Parse(new StreamReader(s).ReadToEnd())!;
        var ob = node["outbounds"]![0]!;
        ob["type"] = obType;
        ob["server_port"] = obPort;
        if (!string.IsNullOrEmpty(stack))
            node["inbounds"]![0]!["stack"] = stack;

        InjectEscapeExcludes(node);
        ApplyHomeDirectRoute(node);

        if (dnsMode == 0)
        {
            Cfg.WriteJson(Cfg.SbConfigPath, node);
            return;
        }

        var rules = node["route"]!["rules"]!.AsArray();
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            // port может быть числом (53) или массивом ([3478,…]) — GetValue<int>
            // на массиве бросает JsonValue (1.9.10, tun-on не поднимался).
            var portNode = rules[i]?["port"];
            if (portNode is JsonValue pv && pv.TryGetValue<int>(out int one) && one == 53)
            {
                rules.RemoveAt(i);
                continue;
            }
            // udp→direct без порта: DNS и QUIC уходили в LAN мимо hijack.
            if (IsUdpDirectCatchAll(rules[i]))
                rules.RemoveAt(i);
        }

        // xray/sing-box раньше DNS-hijack — иначе ядро само уходит в TUN.
        // KovchegVPN.exe специально НЕ в direct: проверка ipify после TUN
        // идёт из этого процесса; обход давал домашний IP и ложный откат.
        int afterProc = 0;
        while (afterProc < rules.Count && rules[afterProc]?["process_name"] != null)
            afterProc++;

        if (dnsMode >= 2)
        {
            // DNS через socks (выход VPS). 1.1.1.1 напрямую LigaLink травит в 8.47.
            // default_domain_resolver=local: иначе маршрутизация ждёт DNS через
            // тот же socks и эхо через TUN зависает (1.9.45).
            foreach (var outbound in node["outbounds"]!.AsArray())
            {
                if (outbound is JsonObject d && d["tag"]?.GetValue<string>() == "direct")
                    d["domain_resolver"] = "local";
            }
            // Чужой DNS, включая YouTube, — DoH :443 через socks.
            // Домашние .ru / VK / Яндекс — UDP на роутер.
            // Windows спрашивает 94.140 (PinLanDns), не 1.1.1.1 — Auto-DoT не вспыхивает.
            string remoteKind = dnsMode == 4 ? "tls" : "udp";
            var dnsServers = new JsonArray
            {
                JsonNode.Parse("{\"type\":\"" + remoteKind + "\",\"tag\":\"remote\",\"server\":\"1.1.1.1\"}"),
                JsonNode.Parse("{\"type\":\"udp\",\"tag\":\"local\",\"server\":\"" + LanGateway() + "\",\"server_port\":53}")
            };
            var dnsRules = new JsonArray();
            if (dnsMode == 3)
            {
                dnsServers.Add(JsonNode.Parse(
                    "{\"type\":\"fakeip\",\"tag\":\"fakeip\",\"inet4_range\":\"198.18.0.0/15\",\"inet6_range\":\"fc00::/18\"}"));
                dnsRules.Add(JsonNode.Parse("{\"query_type\":[\"A\",\"AAAA\"],\"server\":\"fakeip\"}"));
            }
            dnsServers.Add(JsonNode.Parse(
                "{\"type\":\"https\",\"tag\":\"secure\",\"server\":\"1.1.1.1\",\"server_port\":443,\"path\":\"/dns-query\",\"detour\":\"proxy\"}"));
            var homeDns = new JsonArray();
            foreach (string d in Cfg.HomeDirectSuffixes) homeDns.Add(d);
            var tunnelDns = new JsonArray();
            foreach (string d in Cfg.TunnelDnsSuffixes) tunnelDns.Add(d);
            dnsRules.Insert(0, new JsonObject
            {
                ["domain_suffix"] = homeDns,
                ["server"] = "local"
            });
            dnsRules.Insert(0, new JsonObject
            {
                ["domain_suffix"] = tunnelDns,
                ["server"] = "secure"
            });
            node["dns"] = new JsonObject
            {
                ["servers"] = dnsServers,
                ["rules"] = dnsRules,
                ["final"] = "secure",
                ["strategy"] = "ipv4_only",
                ["independent_cache"] = true
            };
            Cfg.Log("tun-on: Grok/OpenAI/чужой DNS → DoH :443 через трубу (не 8.47 с LAN)");
            Cfg.Log("tun-on: .ru/.рф/VK/Яндекс DNS → роутер " + LanGateway() + " (трафик direct), YouTube DNS → туннель");
            node["route"]!["default_domain_resolver"] = "local";
            rules.Insert(afterProc, JsonNode.Parse("{\"action\":\"sniff\"}"));
            // port 53 — даже если sniff не пометил протокол; иначе DNS
            // уходит в роутер и LigaLink снова отдаёт 8.47.
            rules.Insert(afterProc + 1, JsonNode.Parse("{\"port\":53,\"action\":\"hijack-dns\"}"));
            rules.Insert(afterProc + 2, JsonNode.Parse("{\"protocol\":\"dns\",\"action\":\"hijack-dns\"}"));
            rules.Insert(afterProc + 3, JsonNode.Parse(
                "{\"ip_cidr\":[" + string.Join(",", Cfg.PoisonNets.Select(n => "\"" + n.Cidr + "\"")) +
                "],\"outbound\":\"block\"}"));
            // Остальной UDP (STUN чужих приложений, LAN) — direct. Иначе каждый
            // UDP = новый SS TCP через UoT, LigaLink забивает трубу.
            // Grok Bot / голос: STUN обязан идти в трубу, иначе ICE с дома
            // рвётся («сбой соединения в реальном времени»). Правило процесса
            // выше catch-all UDP.
            InsertGrokProcessProxy(rules, afterProc);
            InsertUdpDirectAfterQuicBlock(rules);
        }
        else
        {
            // sing-box ≤1.12: legacy DNS + dns-outbound.
            node["dns"] = JsonNode.Parse(
                "{\"servers\":[" +
                "{\"tag\":\"remote\",\"address\":\"tls://1.1.1.1\",\"detour\":\"proxy\"}," +
                "{\"tag\":\"local\",\"address\":\"" + LanGateway() + "\",\"detour\":\"direct\"}" +
                "],\"final\":\"remote\"}");
            node["outbounds"]!.AsArray().Add(JsonNode.Parse("{\"type\":\"dns\",\"tag\":\"dns-out\"}"));
            rules.Insert(afterProc, JsonNode.Parse("{\"port\":53,\"outbound\":\"dns-out\"}"));
        }

        Cfg.WriteJson(Cfg.SbConfigPath, node);
    }

    // .ru / .рф / VK / Яндекс — мимо трубы. YouTube в этот список не входит.
    // Правило после Instagram/Cursor/Telegram→proxy, иначе sniff без SNI
    // мог утащить Госуслуги в туннель.
    private static void ApplyHomeDirectRoute(JsonNode node)
    {
        var rules = node["route"]?["rules"]?.AsArray();
        if (rules == null) return;

        var suffixes = new JsonArray();
        foreach (string d in Cfg.HomeDirectSuffixes) suffixes.Add(d);

        int existing = -1;
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            string? ob = null;
            try { ob = r?["outbound"]?.GetValue<string>(); } catch { }
            if (ob != "direct") continue;
            if (r?["domain_suffix"] is not JsonArray arr) continue;
            foreach (var n in arr)
            {
                string? s = null;
                try { s = n?.GetValue<string>(); } catch { }
                if (s is "youtube.com" or "ru" or "vk.com")
                {
                    existing = i;
                    break;
                }
            }
            if (existing >= 0) break;
        }

        var rule = new JsonObject
        {
            ["domain_suffix"] = suffixes,
            ["outbound"] = "direct"
        };
        if (existing >= 0)
            rules[existing] = rule;
        else
        {
            int insertAt = rules.Count;
            for (int i = 0; i < rules.Count; i++)
            {
                string? net = null;
                try { net = rules[i]?["network"]?.GetValue<string>(); } catch { }
                if (net == "udp")
                {
                    insertAt = i;
                    break;
                }
            }
            rules.Insert(insertAt, rule);
        }
        Cfg.Log("tun-on: .ru/.рф/.su + VK/Яндекс/YouTube → direct (домашний ISP)");
    }

    private static bool IsUdpDirectCatchAll(JsonNode? rule)
    {
        if (rule == null || rule["port"] != null) return false;
        string? ob = null;
        try { ob = rule["outbound"]?.GetValue<string>(); } catch { }
        if (ob != "direct") return false;
        var n = rule["network"];
        if (n is JsonValue jv && jv.TryGetValue<string>(out var s) && s == "udp")
            return true;
        if (n is JsonArray arr && arr.Count == 1 &&
            arr[0] is JsonValue a0 && a0.TryGetValue<string>(out var s2) && s2 == "udp")
            return true;
        return false;
    }

    private static void InsertGrokProcessProxy(JsonArray rules, int at)
    {
        var names = new JsonArray();
        foreach (string n in Cfg.GrokProcessNames) names.Add(n);
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (!RuleHasGrokProcess(rules[i])) continue;
            rules.RemoveAt(i);
            if (at > i) at--;
        }
        var rule = new JsonObject
        {
            ["process_name"] = names,
            ["outbound"] = "proxy"
        };
        int idx = at < 0 ? 0 : Math.Min(at, rules.Count);
        rules.Insert(idx, rule);
        Cfg.Log("tun-on: Grok Bot process → proxy (STUN/WebRTC не в LAN)");
    }

    private static bool RuleHasGrokProcess(JsonNode? rule)
    {
        if (rule?["process_name"] is not JsonArray names) return false;
        foreach (var n in names)
        {
            string? s = null;
            try { s = n?.GetValue<string>(); } catch { }
            if (s != null && s.Contains("Grok", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void InsertUdpDirectAfterQuicBlock(JsonArray rules)
    {
        int quicAt = -1;
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            var portNode = r?["port"];
            if (portNode is not JsonValue pv || !pv.TryGetValue<int>(out int port) || port != 443)
                continue;
            string? net = null;
            string? ob = null;
            try { net = r?["network"]?.GetValue<string>(); } catch { }
            try { ob = r?["outbound"]?.GetValue<string>(); } catch { }
            if (net == "udp" && ob == "block") { quicAt = i; break; }
        }
        var udpDirect = JsonNode.Parse("{\"network\":\"udp\",\"outbound\":\"direct\"}");
        if (quicAt >= 0)
            rules.Insert(quicAt + 1, udpDirect);
        else
            rules.Add(udpDirect);
    }

    private static void InjectEscapeExcludes(JsonNode node)
    {
        if (EscapeIps.Count == 0)
            EscapeIps.AddRange(CollectEscapeIps());

        var exclude = node["inbounds"]?[0]?["route_exclude_address"]?.AsArray();
        if (exclude != null)
        {
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in exclude)
            {
                if (x is JsonValue jv && jv.TryGetValue<string>(out var s) && s != null)
                    have.Add(s);
            }
            foreach (string ip in EscapeIps)
            {
                string cidr = ip.Contains(':') ? ip + "/128" : ip + "/32";
                if (have.Add(cidr)) exclude.Add(cidr);
            }
        }

        var rules = node["route"]?["rules"]?.AsArray();
        if (rules == null) return;
        foreach (var rule in rules)
        {
            var cidrs = rule?["ip_cidr"]?.AsArray();
            if (cidrs == null) continue;
            bool ours = false;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in cidrs)
            {
                if (x is not JsonValue jv || !jv.TryGetValue<string>(out var s) || s == null) continue;
                have.Add(s);
                if (s.StartsWith(Cfg.ServerIp, StringComparison.Ordinal)) ours = true;
            }
            if (!ours) continue;
            foreach (string ip in EscapeIps)
            {
                string cidr = ip.Contains(':') ? ip + "/128" : ip + "/32";
                if (have.Add(cidr)) cidrs.Add(cidr);
            }
            break;
        }
    }

    // Версия bundled sing-box — от неё зависит формат DNS-конфига.
    private static Version _sbVersion = new(1, 12, 0);

    private static void DetectSingBoxVersion(string sbExe)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(sbExe, "version")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p == null) return;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = System.Text.RegularExpressions.Regex.Match(outp, @"version (\d+)\.(\d+)\.(\d+)");
            if (m.Success)
            {
                _sbVersion = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                Cfg.Log($"tun-on: sing-box version {_sbVersion}");
            }
        }
        catch { }
    }

    private static string LogTail()
    {
        string console;
        lock (SbConsole) console = SbConsole.ToString();
        if (console.Length > 0)
        {
            var lines = console.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" | ", lines.TakeLast(4).Select(l => l.Trim()));
        }
        try
        {
            string p = Path.Combine(Cfg.Dir, "singbox-tun.log");
            if (!File.Exists(p)) return "лога нет";
            var fl = File.ReadAllLines(p);
            return string.Join(" | ", fl.TakeLast(3));
        }
        catch { return "лог не читается"; }
    }

    private static void Rollback()
    {
        KillSingBox();
        DeleteKovchegSlash1();
        ClearEscapeRoutes();
        ClearPoisonBlock();
        Netsh($"interface ipv4 delete address name=\"singbox_tun\" addr={Cfg.TunIp}");
        RestoreLanDns();
        RestoreLanIpv6();
        RestoreTailscaleDns();
        Cfg.Log("rollback: sing-box убит, маршруты и адрес сняты");
    }

    // Только sing-box из папки v2rayN или нашей bin. Amnezia/Grok/прочие не трогаем.
    private static void KillSingBox()
    {
        var dirs = new List<string> { Cfg.BinDir };
        if (Cfg.V2rayNDir() is { } v2) dirs.Add(v2);
        foreach (var p in Process.GetProcessesByName("sing-box"))
        {
            try
            {
                string? f = p.MainModule?.FileName;
                if (f != null && dirs.Any(d => f.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch { }
        }
    }

    // Зависшие elevated-воркеры (в т.ч. от старых сборок). UI не трогаем —
    // у него в командной строке нет "--elevated".
    private static void KillZombieWorkers()
    {
        try
        {
            int me = Environment.ProcessId;
            using var q = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'KovchegVPN.exe'");
            foreach (ManagementObject mo in q.Get())
            {
                int pid = Convert.ToInt32(mo["ProcessId"]);
                string cmd = mo["CommandLine"] as string ?? "";
                if (pid == me || !cmd.Contains("--elevated")) continue;
                try
                {
                    Cfg.Log($"зомби worker pid {pid} — убиваю");
                    Process.GetProcessById(pid).Kill();
                }
                catch { }
            }
        }
        catch (Exception ex) { Cfg.Log("zombie scan: " + ex.Message); }
    }

    // Только шлюз Kovcheg TUN. Голый `route delete 0.0.0.0 mask 128.0.0.0`
    // снимает и OpenVPN TAP (/1 metric 4) — ПК падает на LigaLink, Cursor мёртв.
    private static void DeleteKovchegSlash1()
    {
        Route("delete 0.0.0.0 mask 128.0.0.0 10.0.85.2");
        Route("delete 128.0.0.0 mask 128.0.0.0 10.0.85.2");
    }

    private static void Route(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("route.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            })?.WaitForExit(8000);
        }
        catch { }
    }

    private static void RouteLogged(string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("route.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p == null) { Cfg.Log($"route {args} -> не стартовал"); return; }
            string outp = (p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(8000);
            Cfg.Log($"route {args} -> код {p.ExitCode} {outp}");
        }
        catch (Exception ex) { Cfg.Log($"route {args} -> {ex.Message}"); }
    }

    private static string DnsBackupPath => Path.Combine(Cfg.Dir, "dns-backup.txt");
    private static string Ipv6BackupPath => Path.Combine(Cfg.Dir, "ipv6-backup.txt");
    private static string TailscaleDnsBackupPath => Path.Combine(Cfg.Dir, "tailscale-dns-backup.txt");

    // Только AdGuard. TunGw (10.0.85.2) внутри exclude 10/8 — первый DNS
    // таймаутился, Windows почти не доходил до hijack. Tailscale NRPT
    // перехватывал все имена на 100.100.100.100 мимо трубы.
    private static void PinLanDns()
    {
        try
        {
            DisableAutoDoh();
            DisableLanIpv6();
            var lines = new List<string>();
            bool saved = File.Exists(DnsBackupPath);
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsLanDnsAdapter(ni)) continue;
                var props = ni.GetIPProperties();
                var had = props.DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();
                if (!saved)
                    lines.Add(ni.Name + "=" + (had.Count == 0 ? "dhcp" : string.Join(",", had)));
                Netsh($"interface ipv4 set dnsservers name=\"{ni.Name}\" source=static address={Cfg.DnsPin} register=none validate=no");
                Netsh($"interface ipv6 set dnsservers name=\"{ni.Name}\" source=static address=none validate=no");
                Cfg.Log($"tun-on: DNS {ni.Name} -> {Cfg.DnsPin} (было {(had.Count == 0 ? "dhcp" : string.Join(",", had))})");
            }
            if (!saved && lines.Count > 0)
                File.WriteAllLines(DnsBackupPath, lines);
            Netsh($"interface ipv4 set dnsservers name=\"singbox_tun\" source=static address={Cfg.DnsPin} register=none validate=no");
            RestoreForeignVpnDns();
            DisableTailscaleDns();
        }
        catch (Exception ex) { Cfg.Log("tun-on: PinLanDns " + ex.Message); }
    }

    // 1.9.56 писал 10.0.85.1 на Tailscale — вернуть dhcp, иначе MagicDNS мёртв.
    private static void RestoreForeignVpnDns()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                string n = ni.Name + " " + ni.Description;
                if (!n.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) &&
                    !n.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) &&
                    !n.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase))
                    continue;
                var dns = ni.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString());
                if (!dns.Contains(Cfg.TunIp) && !dns.Contains(Cfg.TunGw)) continue;
                Netsh($"interface ipv4 set dnsservers name=\"{ni.Name}\" source=dhcp");
                Netsh($"interface ipv6 set dnsservers name=\"{ni.Name}\" source=dhcp");
                Cfg.Log($"tun-on: DNS {ni.Name} вернул dhcp (не наш VPN)");
            }
        }
        catch { }
    }

    private static void DisableTailscaleDns()
    {
        try
        {
            string? exe = TailscaleExe();
            if (exe != null)
            {
                string outp = RunCaptured(exe, "set --accept-dns=false", 15000);
                File.WriteAllText(TailscaleDnsBackupPath, "1");
                Cfg.Log("tun-on: tailscale --accept-dns=false " +
                    (string.IsNullOrWhiteSpace(outp) ? "ok" : outp));
            }
            StripTailscaleNrpt();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                string n = ni.Name + " " + ni.Description;
                if (!n.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)) continue;
                Netsh($"interface ipv4 set dnsservers name=\"{ni.Name}\" source=static address=none validate=no");
                Netsh($"interface ipv6 set dnsservers name=\"{ni.Name}\" source=static address=none validate=no");
                Cfg.Log($"tun-on: DNS {ni.Name} снял (Tailscale NRPT больше не крадёт резолв)");
            }
        }
        catch (Exception ex) { Cfg.Log("tun-on: Tailscale DNS " + ex.Message); }
    }

    private static string? TailscaleExe()
    {
        foreach (var dir in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale"),
        })
        {
            string p = Path.Combine(dir, "tailscale.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void StripTailscaleNrpt()
    {
        string[] roots =
        {
            @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig",
            @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig",
        };
        foreach (string root in roots)
        {
            using var k = Registry.LocalMachine.OpenSubKey(root, writable: true);
            if (k == null) continue;
            foreach (string sub in k.GetSubKeyNames())
            {
                bool hit = false;
                using (var sk = k.OpenSubKey(sub))
                {
                    if (sk == null) continue;
                    string blob = Convert.ToString(sk.GetValue("NameServer") ?? "") + " " +
                                  Convert.ToString(sk.GetValue("GenericDNSServers") ?? "") + " " +
                                  Convert.ToString(sk.GetValue("DisplayName") ?? "");
                    hit = blob.Contains("100.100.100.100", StringComparison.Ordinal) ||
                          blob.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
                }
                if (!hit) continue;
                try
                {
                    k.DeleteSubKeyTree(sub);
                    Cfg.Log($"tun-on: снял NRPT {root}\\{sub}");
                }
                catch (Exception ex) { Cfg.Log($"tun-on: NRPT {sub} {ex.Message}"); }
            }
        }
    }

    private static void RestoreTailscaleDns()
    {
        try
        {
            if (!File.Exists(TailscaleDnsBackupPath)) return;
            File.Delete(TailscaleDnsBackupPath);
            string? exe = TailscaleExe();
            if (exe == null) return;
            string outp = RunCaptured(exe, "set --accept-dns=true", 15000);
            Cfg.Log("tun-off: tailscale --accept-dns=true " +
                (string.IsNullOrWhiteSpace(outp) ? "ok" : outp));
        }
        catch (Exception ex) { Cfg.Log("tun-off: Tailscale DNS " + ex.Message); }
    }

    private static void DisableLanIpv6()
    {
        try
        {
            var names = new List<string>();
            bool saved = File.Exists(Ipv6BackupPath);
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsLanDnsAdapter(ni)) continue;
                if (!saved) names.Add(ni.Name);
                Netsh($"interface ipv6 set interface interface=\"{ni.Name}\" admin=disabled");
                Cfg.Log($"tun-on: IPv6 {ni.Name} выключен (Happy Eyeballs мимо /1)");
            }
            if (!saved && names.Count > 0)
                File.WriteAllLines(Ipv6BackupPath, names);
        }
        catch (Exception ex) { Cfg.Log("tun-on: IPv6 " + ex.Message); }
    }

    private static void RestoreLanIpv6()
    {
        try
        {
            if (!File.Exists(Ipv6BackupPath)) return;
            foreach (string name in File.ReadAllLines(Ipv6BackupPath))
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                Netsh($"interface ipv6 set interface interface=\"{name}\" admin=enabled");
                Cfg.Log($"tun-off: IPv6 {name} включён");
            }
            File.Delete(Ipv6BackupPath);
        }
        catch (Exception ex) { Cfg.Log("tun-off: RestoreLanIpv6 " + ex.Message); }
    }

    private static void EnsurePoisonBlock()
    {
        foreach (var n in Cfg.PoisonNets)
        {
            Route($"delete {n.Net} mask {n.Mask}");
            RouteLogged($"add {n.Net} mask {n.Mask} 127.0.0.1 metric 1");
            Cfg.Log($"tun-on: {n.Cidr} → 127.0.0.1");
        }
    }

    private static void ClearPoisonBlock()
    {
        foreach (var n in Cfg.PoisonNets)
            Route($"delete {n.Net} mask {n.Mask}");
    }

    private static string RunCaptured(string file, string args, int waitMs = 8000)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (p == null) return "не стартовал";
            string outp = (p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(waitMs);
            return outp;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static bool IsLanDnsAdapter(NetworkInterface ni)
    {
        if (ni.OperationalStatus != OperationalStatus.Up) return false;
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel) return false;
        string n = ni.Name + " " + ni.Description;
        if (Probe.IsTapNic(ni) ||
            n.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Hamachi", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("singbox", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase))
            return false;
        var props = ni.GetIPProperties();
        if (props.UnicastAddresses.Any(a => a.Address.ToString() == Cfg.TunIp))
            return false;
        return props.GatewayAddresses.Any(g =>
            g.Address.AddressFamily == AddressFamily.InterNetwork);
    }

    private static void DisableAutoDoh()
    {
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters");
            k?.SetValue("EnableAutoDoh", 0, RegistryValueKind.DWord);
            Cfg.Log("tun-on: EnableAutoDoh=0");
        }
        catch (Exception ex) { Cfg.Log("tun-on: AutoDoh " + ex.Message); }
    }

    // UI-сторож: auto_route sing-box иногда сносит /32 на VPS — без них петля.
    public static void KeepEscapeRoutes()
    {
        try
        {
            if (!Probe.TunAdapterUp()) return;
            if (string.IsNullOrEmpty(_lanGw))
                PinLanGateway();
            EnsureEscapeRoutes("сторож");
        }
        catch (Exception ex) { Cfg.Log("сторож: KeepEscapeRoutes " + ex.Message); }
    }

    public static void EmergencyRecoverDns()
    {
        try
        {
            bool leftover = File.Exists(DnsBackupPath) || File.Exists(Ipv6BackupPath) ||
                            File.Exists(TailscaleDnsBackupPath) || AdapterDnsPinned();
            ClearPoisonBlock();
            if (!leftover) return;
            // Даже если TUN ещё up: leftover 1.9.55–60 = чёрная дыра.
            // 1.9.62 после этого пин ставит заново на tun-on.
            Cfg.Log("startup: leftover DNS/IPv6/8.47 — возвращаю LAN");
            KillSingBox();
            DeleteKovchegSlash1();
            Netsh($"interface ipv4 delete address name=\"singbox_tun\" addr={Cfg.TunIp}");
            RestoreLanDns();
            RestoreLanIpv6();
            RestoreTailscaleDns();
            UnpinStuckDns();
            FlushDns();
            Cfg.Log("startup: leftover снят, DNS dhcp, IPv6 вкл");
        }
        catch (Exception ex) { Cfg.Log("startup: EmergencyRecoverDns " + ex.Message); }
    }

    private static bool AdapterDnsPinned()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var dns = ni.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString());
                if (dns.Contains(Cfg.DnsPin) || dns.Contains(Cfg.TunIp) || dns.Contains(Cfg.TunGw))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static void UnpinStuckDns()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                string n = ni.Name + " " + ni.Description;
                if (n.Contains("singbox", StringComparison.OrdinalIgnoreCase)) continue;
                var dns = ni.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToHashSet();
                if (!dns.Contains(Cfg.DnsPin) && !dns.Contains(Cfg.TunIp) && !dns.Contains(Cfg.TunGw))
                    continue;
                Netsh($"interface ipv4 set dnsservers name=\"{ni.Name}\" source=dhcp");
                Netsh($"interface ipv6 set dnsservers name=\"{ni.Name}\" source=dhcp");
                Cfg.Log($"startup: DNS {ni.Name} <- dhcp (был пин)");
            }
        }
        catch { }
    }

    private static void RestoreLanDns()
    {
        try
        {
            if (!File.Exists(DnsBackupPath)) return;
            foreach (string line in File.ReadAllLines(DnsBackupPath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string name = line[..eq];
                string val = line[(eq + 1)..];
                if (val == "dhcp" || string.IsNullOrWhiteSpace(val))
                    Netsh($"interface ipv4 set dnsservers name=\"{name}\" source=dhcp");
                else
                {
                    string[] ips = val.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    Netsh($"interface ipv4 set dnsservers name=\"{name}\" source=static address={ips[0]} register=none validate=no");
                    for (int i = 1; i < ips.Length; i++)
                        Netsh($"interface ipv4 add dnsservers name=\"{name}\" address={ips[i]} index={i + 1} validate=no");
                }
                Netsh($"interface ipv6 set dnsservers name=\"{name}\" source=dhcp");
                Cfg.Log($"tun-off: DNS {name} <- {val}");
            }
            File.Delete(DnsBackupPath);
            FlushDns();
        }
        catch (Exception ex) { Cfg.Log("tun-off: RestoreLanDns " + ex.Message); }
    }

    private static void FlushDns()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            })?.WaitForExit(5000);
        }
        catch { }
    }

    private static void Netsh(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("netsh.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            })?.WaitForExit(8000);
        }
        catch { }
    }

    private static void DisarmWatchdog()
    {
        _finished = true;
        try { _watchdog?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
    }

    private static int Ok(string msg)
    {
        DisarmWatchdog();
        WriteResult("OK|" + msg);
        return 0;
    }
    private static int Fail(string msg)
    {
        DisarmWatchdog();
        WriteResult("FAIL|" + msg);
        try { LogSender.Send(8); } catch { }
        return 1;
    }
    private static void WriteResult(string s)
    {
        try { File.WriteAllText(Cfg.ResultPath, s); } catch { }
    }
}
