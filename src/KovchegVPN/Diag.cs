using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace KovchegVPN;

// Снимок «живого» ПК в kovcheg.log: DNS, резолв, HTTP/1.1 vs HTTP/2, процессы, маршруты.
// Без этого в логе только «instagram 200» у воркера, а браузер/Cursor молчат.
public static class Diag
{
    private static readonly object Gate = new();
    private static DateTime _last;

    // tun-on: два коротких HTTPS. 1.9.67 пять по 4 с = 20 с таймаутов
    // в момент, когда труба и так забита Telegram TCP.
    private static readonly (string Name, string Url)[] Sites =
    {
        ("instagram", "https://www.instagram.com/"),
        ("icanhazip", "https://icanhazip.com/"),
    };

    private static readonly string[] Hosts =
    {
        "www.instagram.com", "instagram.com",
        "www.facebook.com", "scontent.cdninstagram.com",
        "api2.cursor.sh", "api3.cursor.sh", "www.cursor.com", "cursor.sh",
        "test.us8.cursorvm.com", "grok.com", "api.x.ai",
        "chatgpt.com", "openai.com", "cdn.oaistatic.com",
        "i.ytimg.com", "yt3.ggpht.com", "www.youtube.com",
        "vk.com", "gosuslugi.ru", "rutube.ru",
        "icanhazip.com",
    };

    public static void Snapshot(string why)
    {
        if (!Monitor.TryEnter(Gate, 0)) return;
        try
        {
            if ((DateTime.UtcNow - _last) < TimeSpan.FromSeconds(20) && why != "tun-on")
                return;
            _last = DateTime.UtcNow;
            Run(why);
        }
        finally { Monitor.Exit(Gate); }
    }

    private static void Run(string why)
    {
        Cfg.Log($"diag: ---- {why} v{LogBus.Version} transport={Core.Transport} tun={Probe.TunAdapterUp()} ----");
        var tap = Probe.FindTap();
        Cfg.Log($"diag: tap={(tap == null ? "нет" : $"{tap.Value.Name} {tap.Value.Ip}")} local100={Probe.HasLocalCgNat()}");
        LogAdapters();
        LogProcesses();
        LogRoutes();
        LogResolves();
        if (why == "tun-on")
            LogHttp();
        else
            Cfg.Log("diag: http пропуск (tick) — минутный HTTPS сам душит трубу");
        LogSingBoxTail();
        Cfg.Log("diag: ---- конец ----");
    }

    private static void LogAdapters()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.OperationalStatus is OperationalStatus.Down or OperationalStatus.NotPresent)
                    continue;
                var p = ni.GetIPProperties();
                var v4 = p.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString());
                var dns = p.DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString());
                var gw = p.GatewayAddresses
                    .Where(g => g.Address?.AddressFamily == AddressFamily.InterNetwork)
                    .Select(g => g.Address!.ToString());
                Cfg.Log($"diag: nic «{ni.Name}» desc={ni.Description} status={ni.OperationalStatus} [{ni.NetworkInterfaceType}] ip={Join(v4)} dns={Join(dns)} gw={Join(gw)}");
            }
        }
        catch (Exception ex) { Cfg.Log("diag: nic " + ex.Message); }
    }

    private static void LogProcesses()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (string name in new[]
                     { "xray", "sing-box", "v2rayN", "v2rayn", "Grok Bot", "GrokBot", "Grok" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    string exe = "?";
                    try { exe = p.MainModule?.FileName ?? "?"; } catch { }
                    sb.Append(name).Append('#').Append(p.Id).Append('{').Append(exe).Append("} ");
                }
            }
            Cfg.Log("diag: proc " + (sb.Length == 0 ? "нет xray/sing-box/v2rayN" : sb.ToString()));
        }
        catch (Exception ex) { Cfg.Log("diag: proc " + ex.Message); }
    }

    private static void LogRoutes()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("route.exe", "print -4")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p == null) return;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            int n = 0;
            foreach (string line in outp.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("0.0.0.0") || t.Contains("128.0.0.0") ||
                    t.Contains("8.47.") || t.Contains("8.6.") ||
                    t.Contains("10.0.85") || t.Contains("10.105.") || t.Contains("10.126.") ||
                    t.Contains(Cfg.ServerIp) ||
                    t.Contains("94.140") || t.Contains("1.1.1.1"))
                {
                    Cfg.Log("diag: route " + t);
                    if (++n >= 18) break;
                }
            }
        }
        catch (Exception ex) { Cfg.Log("diag: route " + ex.Message); }
    }

    private static void LogResolves()
    {
        foreach (string host in Hosts)
        {
            try
            {
                var t = Task.Run(() => Dns.GetHostAddresses(host));
                if (!t.Wait(4000))
                {
                    Cfg.Log($"diag: dns {host} TIMEOUT 4с");
                    continue;
                }
                var ips = t.Result
                    .Select(a => a.ToString())
                    .Take(6)
                    .ToArray();
                string joined = ips.Length == 0 ? "пусто" : string.Join(",", ips);
                string mark = ips.Any(IsPoison) ? " POISON" : "";
                Cfg.Log($"diag: dns {host} -> {joined}{mark}");
            }
            catch (Exception ex)
            {
                Cfg.Log($"diag: dns {host} FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void LogHttp()
    {
        foreach (var (name, url) in Sites)
        {
            var tun = Probe.HttpDetail(url, null, 3, HttpVersion.Version11).GetAwaiter().GetResult();
            Cfg.Log($"diag: http TUN-h11 {name} {tun.Detail}");
        }
    }

    private static void LogSingBoxTail()
    {
        try
        {
            string p = Path.Combine(Cfg.Dir, "singbox-tun.log");
            if (!File.Exists(p))
            {
                Cfg.Log("diag: singbox-tun.log нет");
                return;
            }
            var lines = File.ReadAllLines(p);
            foreach (string l in lines.TakeLast(8))
            {
                string t = l.Trim();
                if (t.Length > 0) Cfg.Log("diag: sb " + t);
            }
        }
        catch (Exception ex) { Cfg.Log("diag: sb " + ex.Message); }
    }

    private static bool IsPoison(string ip) =>
        ip.StartsWith("8.47.", StringComparison.Ordinal) ||
        ip.StartsWith("8.6.", StringComparison.Ordinal);

    private static string Join(IEnumerable<string> xs)
    {
        string s = string.Join(",", xs);
        return s.Length == 0 ? "-" : s;
    }
}
