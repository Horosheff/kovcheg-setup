using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KovchegVPN;

    // Свой xray 26.7.28 на 10818/10819. Сплит-маршрутизация (РФ напрямую)
    // живёт внутри своего ядра на http-inbound.
public static class Core
{
    private static Process? _own;

    // geosite-теги v2fly (тот же источник, что у lib4u/amnezia-tunneling-ru):
    // всё российское — напрямую. YouTube не здесь: он в DomainForceProxy.
    private static readonly string[] GeoFull =
    {
        "category-ru", "category-bank-ru", "category-gov-ru", "category-media-ru",
        "category-ecommerce-ru", "category-retail-ru", "category-entertainment-ru",
        "category-travel-ru", "yandex", "vk", "mailru", "mailru-group", "ok", "dzen",
        "kinopoisk", "okko", "wink", "rutube", "ozon", "wildberries", "avito", "x5",
        "aviasales", "2gis", "mosmetro", "kaspersky", "drweb"
    };
    private static readonly string[] GeoCore =
    {
        "category-ru", "category-bank-ru", "category-gov-ru", "yandex", "vk", "mailru"
    };
    private static readonly string[] DomainAlways =
    {
        "domain:ru", "domain:xn--p1ai", "domain:su",
        "domain:vk.com", "domain:vk.me", "domain:vk.cc", "domain:mvk.com",
        "domain:userapi.com", "domain:vk-cdn.net", "domain:vk-portal.net",
        "domain:vkuseraudio.net", "domain:vkuservideo.net", "domain:vkuser.net",
        "domain:yandex.net", "domain:yandex.com", "domain:yastatic.net",
        "domain:mycdn.me", "domain:ozonstatic.net", "domain:wstatic.net",
        "domain:2gis.com",
    };
    // Сплит/geosite не должны утащить их в РФ-direct. Instagram и Cursor IDE —
    // отдельный SS без mux: HTTP/2 иначе ловит CANCEL. Telegram — другой
    // outbound с mux, иначе с дома сотни TCP и LigaLink душит все сайты.
    private static readonly string[] DomainForceProxy =
    {
        "domain:instagram.com", "domain:cdninstagram.com", "domain:ig.me",
        "domain:facebook.com", "domain:facebook.net", "domain:fbcdn.net",
        "domain:fbsbx.com", "domain:meta.com",
        "domain:cursor.sh", "domain:cursor.com", "domain:cursorapi.com",
        "domain:cursorvm.com", "domain:cursor-cdn.com",
        "domain:anysphere.co", "domain:anysphere.com",
        "domain:grok.com", "domain:x.ai",
        "domain:livekit.cloud", "domain:livekit.io",
        "domain:openai.com", "domain:chatgpt.com",
        "domain:oaistatic.com", "domain:oaiusercontent.com",
        "domain:youtube.com", "domain:youtu.be", "domain:googlevideo.com",
        "domain:ytimg.com", "domain:ggpht.com", "domain:youtube-nocookie.com",
        "domain:youtubei.googleapis.com", "full:youtube.googleapis.com",
        "domain:googleusercontent.com",
        // Охота ядра (icanhazip) иначе идёт в default mux и LigaLink её вешает.
        "domain:icanhazip.com", "domain:ipify.org",
        "full:checkip.amazonaws.com",
    };
    private static readonly string[] DomainTelegram =
    {
        "domain:telegram.org", "domain:t.me", "domain:telegram.me",
        "domain:cdn-telegram.org",
    };

    public static async Task<string?> FindLive(bool log = true)
    {
        var tasks = Cfg.Endpoints.Select(async e =>
        {
            string url = e.Kind == "socks" ? $"socks5://127.0.0.1:{e.Port}" : $"http://127.0.0.1:{e.Port}";
            string? ip = await Probe.ExitIp(url, 4, maxTries: 1);
            return (e.Kind, e.Port, ip);
        }).ToArray();
        var results = await Task.WhenAll(tasks);
        if (log)
            foreach (var r in results)
                LogBus.Write($"core: {r.Kind} {r.Port} -> {r.ip ?? "нет"}");
        foreach (var (kind, port, ip) in results) // порядок массива = приоритет
            if (Cfg.IsOurs(ip)) return $"{kind}:{port}";
        return null;
    }

    // Свой VPS: Reality xHTTP :443, Vision :8443, SS на нескольких портах,
    // nginx :80. Старые tls-xh-b / ss-ts / второй IPv4 из охоты убраны.
    private static readonly string[] AutoChain =
        { "xhttp", "vision", "ss-b-amd", "ss-b-hi", "ss-b-fresh", "ss", "ss-b-alt", "ws", "xh" };

    public static string Transport { get; private set; } = NormalizeTransport(Cfg.IniGet("Transport"));

    private static (int ProbeSec, int Rounds, int StepMs) HuntBudget(string t)
    {
        if (IsTlsFront(t)) return (8, 2, 400);
        if (IsSs(t)) return (3, 1, 250);
        return (4, 2, 300);
    }

    private static bool IsSs(string t) =>
        t is "ss" or "ss-alt" or "ss6" or "ss-b" or "ss-b-alt" or "ss-b-hi" or "ss-b-fresh"
            or "ss-ts" or "ss-b-443" or "ss-b-amd";

    private static bool IsFrontHttp(string t) =>
        t is "ws-b" or "ws" or "xh-b" or "xh";

    private static bool IsTlsFront(string t) =>
        t is "tls-xh-b" or "tls-ws-b";

    private static string NormalizeTransport(string? t) => t switch
    {
        "vision" => "vision",
        "ss" => "ss",
        "ss-alt" => "ss-alt",
        "ss-b" => "ss-b",
        "ss-b-alt" => "ss-b-alt",
        "ss-b-hi" => "ss-b-hi",
        "ss-b-fresh" => "ss-b-fresh",
        "ss-b-443" => "xhttp",
        "ss-b-amd" => "ss-b-amd",
        "ss-ts" => "ss-b-hi",
        "ss6" => "ss6",
        "tls-xh-b" => "xhttp",
        "tls-ws-b" => "ws",
        "ws-b" => "ws",
        "ws" => "ws",
        "xh-b" => "xh",
        "xh" => "xh",
        "xhttp-b" => "xhttp",
        "xhttp" => "xhttp",
        "xhttp-amd" => "ss-b-amd",
        "xhttp-amd6" => "ss-b-amd",
        _ => "xhttp",
    };

    private static bool NeedsIpv6(string t) =>
        t is "ss6" or "xhttp6" or "xhttp-amd6";

    private static bool IsAltIp(string t) =>
        t is "ss-b" or "ss-b-alt" or "ss-b-hi" or "ss-b-fresh" or "ss-b-443" or "ss-b-amd"
            or "tls-xh-b" or "tls-ws-b" or "xhttp-b" or "ws-b" or "xh-b";

    private static IEnumerable<string> TransportChain(bool v6)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (IsSs(Transport) || IsFrontHttp(Transport) || IsTlsFront(Transport)
            || Transport is "xhttp-b" or "xhttp" or "vision")
        {
            if (seen.Add(Transport))
                yield return Transport;
        }
        foreach (string t in AutoChain)
        {
            if (NeedsIpv6(t) && !v6) continue;
            if (seen.Add(t)) yield return t;
        }
    }

    private static IEnumerable<string> AltIpChain()
    {
        foreach (string t in AutoChain)
            yield return t;
    }

    // С дома IPv4 SS/Reality ESTAB без accept. Повтор каждый клик — 15 с
    // впустую, потом всё равно CF. Пока шлюз и LAN-IP те же, 45 мин не охотимся.
    private static readonly TimeSpan DirectDeadTtl = TimeSpan.FromMinutes(45);

    private static bool DirectDeadActive()
    {
        if (!long.TryParse(Cfg.IniGet("DirectDeadUntil"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long unix) || unix <= 0)
            return false;
        if (DateTimeOffset.FromUnixTimeSeconds(unix) <= DateTimeOffset.UtcNow)
            return false;
        if (!string.Equals(Cfg.IniGet("DirectDeadGw") ?? "", Cfg.Gateway, StringComparison.Ordinal))
            return false;
        if (!string.Equals(Cfg.IniGet("DirectDeadLan") ?? "", Probe.LanIpv4(), StringComparison.Ordinal))
            return false;
        return true;
    }

    private static bool AltIpDead() =>
        DirectDeadActive() &&
        string.Equals(Cfg.IniGet("DirectDeadAlt") ?? "", Cfg.ServerIpB, StringComparison.Ordinal);

    private static DateTime DirectDeadUntilUtc()
    {
        if (long.TryParse(Cfg.IniGet("DirectDeadUntil"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long unix) && unix > 0)
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return DateTime.UtcNow;
    }

    private static void MarkDirectDead(bool altTried)
    {
        var until = DateTimeOffset.UtcNow.Add(DirectDeadTtl);
        Cfg.IniSet("DirectDeadUntil", until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Cfg.IniSet("DirectDeadGw", Cfg.Gateway);
        Cfg.IniSet("DirectDeadLan", Probe.LanIpv4());
        if (altTried)
            Cfg.IniSet("DirectDeadAlt", Cfg.ServerIpB);
        LogBus.Write($"core: прямые трубы с этой сети помечены мёртвыми до {until.UtcDateTime:HH:mm} UTC");
    }

    private static void ClearDirectDead()
    {
        if (string.IsNullOrEmpty(Cfg.IniGet("DirectDeadUntil")) &&
            string.IsNullOrEmpty(Cfg.IniGet("DirectDeadAlt"))) return;
        Cfg.IniSet("DirectDeadUntil", "");
        Cfg.IniSet("DirectDeadGw", "");
        Cfg.IniSet("DirectDeadLan", "");
        Cfg.IniSet("DirectDeadAlt", "");
        LogBus.Write("core: прямая труба жива — снимаю пометку DirectDead");
    }

    // Кнопка «весь ПК» — снова пробуем прямую трубу.
    public static void ResetHuntOnUserClick()
    {
        if (string.IsNullOrEmpty(Cfg.IniGet("DirectDeadUntil"))) return;
        ClearDirectDead();
        LogBus.Write("core: ручной запуск — сбрасываю DirectDead, ищу прямую трубу");
    }

    // Сначала своё ядро 10818. tryAll=false — сторож: один транспорт, без FindLive.
    public static async Task<string?> EnsureAsync(Action<string>? status = null, bool tryAll = true)
    {
        string? ip = await Probe.ExitIp($"socks5://127.0.0.1:{Cfg.OwnSocksPort}", 2, maxTries: 1);
        LogBus.Write($"core: socks {Cfg.OwnSocksPort} -> {ip ?? "нет"} own={OwnAlive()}");
        if (Cfg.IsOurs(ip) && OwnAlive())
            return $"socks:{Cfg.OwnSocksPort}";
        if (Cfg.IsOurs(ip) && !OwnAlive())
            LogBus.Write("core: socks жив чужим процессом — переподнимаю под текущий SS");

        bool skipOld = tryAll && DirectDeadActive();
        bool skipAlt = skipOld && AltIpDead();

        await Front.RefreshAsync();
        // Полный комплект, а не только xray: иначе TUN потом упадёт на sing-box.
        if (!await Bins.EnsureAsync(status))
        {
            LogBus.Write("core: ядра нет и скачать не вышло");
            return null;
        }

        bool v6 = Probe.HasGlobalIpv6();
        if (!v6)
            LogBus.Write("core: глобального IPv6 нет — ss6/xhttp6 пропускаю");
        Probe.PrepareOpenVpnPath();
        string lan = Probe.LanBindIp();
        var tap = Probe.FindTap();
        LogBus.Write(
            "core: критерий — Kovcheg без чужого VPN. " +
            (string.IsNullOrEmpty(lan) ? "LAN bind нет" : $"охота bind LAN {lan}") +
            (tap == null ? ", TAP нет" : $", чужой TAP {tap.Value.Ip} не bind"));

        IEnumerable<string> chain;
        if (skipAlt)
        {
            LogBus.Write($"core: прямые трубы помечены мёртвыми до {DirectDeadUntilUtc():HH:mm} UTC — повтор охоты");
            chain = AltIpChain();
        }
        else if (skipOld)
        {
            LogBus.Write($"core: пробую доп. IP {Cfg.ServerIpB}, старый {Cfg.ServerIp} пропускаю");
            chain = AltIpChain();
        }
        else if (tryAll)
            chain = TransportChain(v6);
        else if (Array.IndexOf(AutoChain, Transport) >= 0)
            chain = (NeedsIpv6(Transport) && !v6)
                ? TransportChain(v6)
                : new[] { Transport };
        else
            chain = TransportChain(v6);
        StopOwn();
        await Task.Delay(200);
        bool triedAlt = false;
        foreach (string tr in chain)
        {
            if (IsAltIp(tr)) triedAlt = true;
            if (IsSs(tr) || IsFrontHttp(tr) || IsTlsFront(tr) || tr == "vision")
            {
                var (addr, port) = tr == "vision"
                    ? (Cfg.ServerIp, Cfg.VisionPort)
                    : IsSs(tr) ? SsEndpoint(tr) : FrontEndpoint(tr);
                bool tcp = await Probe.TcpOpen(addr, port, 1500);
                string lanBind = Probe.LanBindIp();
                LogBus.Write($"core: TCP {addr}:{port} {(tcp ? "открыт" : "нет")}" +
                    (string.IsNullOrEmpty(lanBind) ? "" : $" bind LAN {lanBind}"));
                if (!tcp)
                {
                    LogBus.Write($"core: {tr} — TCP нет, следующий");
                    if (!tryAll) break;
                    continue;
                }
            }
            if (IsTlsFront(tr))
                LogBus.Write("core: VLESS+TLS свой nginx :443 (Let's Encrypt), не Reality и не Cloudflare");
            if (IsFrontHttp(tr))
                LogBus.Write("core: свой nginx :80 (WS/xHTTP), не Cloudflare");
            LogBus.Write($"core: поднимаю свой xray (10818/10819), транспорт {tr}");
            status?.Invoke($"Поднимаю ядро ({tr})…");
            StopOwn();
            await Task.Delay(200);
            if (!StartOwn(tr))
            {
                LogBus.Write($"core: {tr} не стартовал, следующий");
                continue;
            }
            var huntSw = Stopwatch.StartNew();
            var (probeSec, rounds, stepMs) = HuntBudget(tr);
            string socksUrl = $"socks5://127.0.0.1:{Cfg.OwnSocksPort}";
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(stepMs);
                ip = await Probe.ExitIp(socksUrl, probeSec, maxTries: 1);
                if (Cfg.IsOurs(ip))
                {
                    AcceptTransport(tr, ip!);
                    return $"socks:{Cfg.OwnSocksPort}";
                }
                LogBus.Write($"core: {tr} round {i + 1}/{rounds} echo={ip ?? "нет"}");
                if (i < rounds - 1) continue;
                var ig = await Probe.HttpDetail("https://www.instagram.com/", socksUrl, probeSec, HttpVersion.Version11);
                LogBus.Write($"core: {tr} round {i + 1}/{rounds} echo={ip ?? "нет"} ig={ig.Detail}");
                if (Probe.HttpLooksUp(ig))
                {
                    AcceptTransport(tr, ip ?? "ig-ok");
                    return $"socks:{Cfg.OwnSocksPort}";
                }
            }
            LogBus.Write($"core: {tr} не выдал наш выход за {huntSw.Elapsed.TotalSeconds:0.0}с");
            if (!tryAll) break;
        }

        MarkDirectDead(triedAlt);
        LogBus.Write($"core: прямой VPS ({Cfg.ServerIpB}/{Cfg.ServerIp}) не отвечает с этой сети");
        return null;
    }

    private static void AcceptTransport(string tr, string ip)
    {
        if (tr != Transport) LogBus.Write($"core: транспорт {Transport} → {tr}");
        Transport = tr;
        Cfg.IniSet("Transport", tr);
        ClearDirectDead();
        LogBus.Write($"core: свой xray жив ({tr}), выход {ip} — канал Kovcheg, не чужой VPN");
    }

    public static bool OwnAlive()
    {
        try { return _own is { HasExited: false }; } catch { return false; }
    }

    public static bool StartOwn(string? transport = null)
    {
        try
        {
            transport ??= Transport;
            string? exe = Cfg.XrayExe();
            LogBus.Write($"core: xray.exe = {exe ?? "НЕ НАЙДЕН"}");
            if (exe == null) return false;

            Cfg.ExtractAssets();
            string? assetDir = EnsureGeoAssets(Path.GetDirectoryName(exe)!);
            int level = WriteValidatedConfig(exe, assetDir, transport);
            LogBus.Write($"core: сплит-правила уровень {level} ({(level == 2 ? "geosite полный" : level == 1 ? "geosite базовый" : "только .ru/.рф/.su + VK/YouTube")})");
            string lan = Probe.LanBindIp();
            var tap = Probe.FindTap();
            if (!string.IsNullOrEmpty(lan))
                LogBus.Write($"core: xray sockopt.interface={lan} (LAN)" +
                    (tap == null ? "" : $", чужой TAP {tap.Value.Ip} не bind"));
            else if (tap != null)
                LogBus.Write($"core: LAN IPv4 нет — sockopt без TAP ({tap.Value.Ip})");
            if (IsSs(transport))
                LogBus.Write("core: SS split — default mux, Instagram/Cursor без mux");
            else if (IsTlsFront(transport))
                LogBus.Write("core: VLESS+TLS nginx :443, mux нет");
            else if (IsFrontHttp(transport))
                LogBus.Write("core: VLESS через свой nginx :80, mux нет");
            KillOtherXray();

            var psi = new ProcessStartInfo(exe, $"run -c \"{Cfg.XrayConfigPath}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (assetDir != null) psi.Environment["XRAY_LOCATION_ASSET"] = assetDir;
            _own = Process.Start(psi);
            if (_own == null) return false;
            _own.OutputDataReceived += (_, e) => { if (e.Data != null && XrayLineWanted(e.Data)) LogBus.Write("xray: " + e.Data); };
            _own.ErrorDataReceived += (_, e) => { if (e.Data != null && XrayLineWanted(e.Data)) LogBus.Write("xray: " + e.Data); };
            _own.BeginOutputReadLine();
            _own.BeginErrorReadLine();
            LogBus.Write($"core: свой xray pid {_own.Id}");
            return true;
        }
        catch (Exception ex)
        {
            LogBus.Write("core: xray start FAIL " + ex.Message);
            return false;
        }
    }

    // Гасим своё ядро и любой чужой xray, запущенный с kovcheg-xray.json
    // (после обновления/краша старый процесс держит 10818 — новый конфиг
    // пишется на диск, но слушает его всё ещё Chrome-xhttp).
    public static void StopOwn()
    {
        try
        {
            if (_own is { HasExited: false })
            {
                _own.Kill();
                _own.WaitForExit(3000);
                LogBus.Write("core: свой xray остановлен");
            }
        }
        catch { }
        _own = null;
        KillStrayKovchegXray();
    }

    // v2rayN сам держит второй xray на тот же SS :2096. С дома это сотни
    // ESTAB-зомби, LigaLink душит Instagram/Cursor.
    // v2rayN сам поднимает второй xray на :2096. Убивать только xray
    // бесполезно — GUI сразу стартует новый. Гасим и GUI, пока стоит TUN.
    public static void KillOtherXray()
    {
        try
        {
            int skip = _own is { HasExited: false } ? _own.Id : -1;
            using var q = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'xray.exe'");
            foreach (ManagementObject mo in q.Get())
            {
                int pid = Convert.ToInt32(mo["ProcessId"]);
                string cmd = mo["CommandLine"] as string ?? "";
                if (pid == skip) continue;
                if (cmd.Contains("kovcheg-xray.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var p = Process.GetProcessById(pid);
                    p.Kill();
                    p.WaitForExit(2000);
                    LogBus.Write($"core: убил чужой xray pid {pid}");
                }
                catch { }
            }
        }
        catch (Exception ex) { LogBus.Write("core: other xray: " + ex.Message); }

        foreach (string name in new[] { "v2rayN", "v2rayn" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    LogBus.Write($"core: гашу {name} pid {p.Id} — иначе снова зальёт :2096");
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
            }
        }
    }

    private static void KillStrayKovchegXray()
    {
        try
        {
            int skip = _own is { HasExited: false } ? _own.Id : -1;
            using var q = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'xray.exe'");
            foreach (ManagementObject mo in q.Get())
            {
                int pid = Convert.ToInt32(mo["ProcessId"]);
                string cmd = mo["CommandLine"] as string ?? "";
                if (pid == skip) continue;
                if (!cmd.Contains("kovcheg-xray.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var p = Process.GetProcessById(pid);
                    p.Kill();
                    p.WaitForExit(2000);
                    LogBus.Write($"core: убил застрявший xray pid {pid}");
                }
                catch { }
            }
        }
        catch (Exception ex) { LogBus.Write("core: stray xray: " + ex.Message); }
    }

    private static readonly string[] XrayInteresting =
    {
        "instagram", "facebook", "fbcdn", "cdninstagram", "cursor",
        "anysphere", "8.6.", "8.47.", "rejected", "failed", "app/dial",
    };
    private static int _xrayAcc;
    private static DateTime _xrayAccMin;
    private static readonly object XrayAccGate = new();

    private static bool XrayLineWanted(string d)
    {
        if (d.Contains("[Error]", StringComparison.Ordinal) ||
            d.Contains("[Warning]", StringComparison.Ordinal) ||
            d.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("wsarecv", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!d.Contains(" accepted ", StringComparison.Ordinal)) return false;
        bool hit = false;
        foreach (string k in XrayInteresting)
        {
            if (d.Contains(k, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
        }
        if (!hit) return false;
        lock (XrayAccGate)
        {
            if (DateTime.UtcNow - _xrayAccMin > TimeSpan.FromMinutes(1))
            {
                _xrayAcc = 0;
                _xrayAccMin = DateTime.UtcNow;
            }
            if (_xrayAcc >= 40) return false;
            _xrayAcc++;
            return true;
        }
    }

    // geosite.dat/geoip.dat: свежие с Cloudflare (VPS в РФ не достучаться),
    // иначе — из папки xray v2rayN. null = пусть xray ищет сам рядом с exe.
    private static string? EnsureGeoAssets(string xrayDir)
    {
        try
        {
            Directory.CreateDirectory(Cfg.GeoDir);
            foreach (string f in new[] { "geosite.dat", "geoip.dat" })
            {
                string local = Path.Combine(Cfg.GeoDir, f);
                bool stale = !File.Exists(local) ||
                             (DateTime.UtcNow - File.GetLastWriteTimeUtc(local)) > TimeSpan.FromDays(14);
                if (stale)
                {
                    try
                    {
                        var h = new SocketsHttpHandler { UseProxy = false };
                        Probe.ApplyTapBind(h);
                        using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(60) };
                        string gh = f == "geosite.dat"
                            ? "https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat"
                            : "https://github.com/v2fly/geoip/releases/latest/download/geoip.dat";
                        string[] urls =
                        {
                            $"http://{Cfg.ServerIp}/geo/{f}",
                            $"http://{Cfg.ServerIpB}/geo/{f}",
                            gh
                        };
                        byte[]? data = null;
                        foreach (string u in urls)
                        {
                            try
                            {
                                data = c.GetByteArrayAsync(u).Result;
                                if (data.Length > 100_000) break;
                            }
                            catch (Exception ex) { LogBus.Write($"core: {f} {u} — {ex.Message}"); }
                        }
                        if (data != null && data.Length > 100_000)
                        {
                            File.WriteAllBytes(local + ".tmp", data);
                            File.Move(local + ".tmp", local, true);
                            LogBus.Write($"core: {f} обновлён с сервера ({data.Length} байт)");
                        }
                    }
                    catch (Exception ex) { LogBus.Write($"core: {f} с сервера не взялся — {ex.Message}"); }
                }
                if (!File.Exists(local))
                {
                    string src = Path.Combine(xrayDir, f);
                    if (File.Exists(src)) File.Copy(src, local, true);
                }
            }
            bool ok = File.Exists(Path.Combine(Cfg.GeoDir, "geosite.dat")) &&
                      File.Exists(Path.Combine(Cfg.GeoDir, "geoip.dat"));
            return ok ? Cfg.GeoDir : null;
        }
        catch (Exception ex)
        {
            LogBus.Write("core: geo assets FAIL " + ex.Message);
            return null;
        }
    }

    // Пишет конфиг с маршрутизацией сплита и валидирует `xray -test`.
    // Уровни: 2 = полный набор geosite, 1 = базовый, 0 = без geo (только суффиксы).
    private static int WriteValidatedConfig(string exe, string? assetDir, string transport)
    {
        for (int level = 2; level >= 0; level--)
        {
            WriteXrayConfig(level, transport);
            if (level == 0 || XrayTest(exe, assetDir)) return level;
            LogBus.Write($"core: xray -test отклонил уровень {level}, пробую ниже");
        }
        return 0;
    }

    private static (string Addr, int Port) SsEndpoint(string transport)
    {
        string address = transport switch
        {
            "ss6" => Cfg.ServerIp6,
            "ss-b" or "ss-b-alt" or "ss-b-hi" or "ss-b-fresh" or "ss-b-443" or "ss-b-amd" => Cfg.ServerIpB,
            _ => Cfg.ServerIp
        };
        int port = transport switch
        {
            "ss-b-443" => Cfg.Ss443Port,
            "ss-b-amd" => Cfg.XhttpAmdPort,
            "ss-b-hi" => Cfg.SsHiPort,
            "ss-b-fresh" => Cfg.SsFreshPort,
            "ss-alt" or "ss6" or "ss-b-alt" => Cfg.SsAltPort,
            _ => Cfg.SsPort
        };
        return (address, port);
    }

    private static (string Addr, int Port) FrontEndpoint(string transport) => transport switch
    {
        "tls-xh-b" or "tls-ws-b" => (Cfg.ServerIpB, 443),
        "ws-b" or "xh-b" => (Cfg.ServerIpB, 80),
        _ => (Cfg.ServerIp, 80),
    };

    private static void StampLan(JsonObject sockopt)
    {
        string lan = Probe.LanBindIp();
        if (string.IsNullOrEmpty(lan)) return;
        sockopt["interface"] = lan;
    }

    private static JsonObject KeepaliveSockopt()
    {
        var o = new JsonObject
        {
            ["tcpKeepAliveIdle"] = 20,
            ["tcpKeepAliveInterval"] = 20,
            ["tcpNoDelay"] = true,
        };
        StampLan(o);
        return o;
    }

    private static JsonObject VlessVnext(string address, int port) => new()
    {
        ["vnext"] = new JsonArray
        {
            new JsonObject
            {
                ["address"] = address,
                ["port"] = port,
                ["users"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = Cfg.Uuid,
                        ["encryption"] = "none",
                    }
                }
            }
        }
    };

    private static void ApplyVlessWs(JsonNode proxyOb, string transport)
    {
        var (address, port) = FrontEndpoint(transport);
        proxyOb["protocol"] = "vless";
        proxyOb["settings"] = VlessVnext(address, port);
        proxyOb["streamSettings"] = new JsonObject
        {
            ["network"] = "ws",
            ["security"] = "none",
            ["wsSettings"] = new JsonObject
            {
                ["path"] = Cfg.FrontPath,
                ["headers"] = new JsonObject { ["Host"] = address },
            },
            ["sockopt"] = KeepaliveSockopt(),
        };
        proxyOb.AsObject().Remove("mux");
    }

    private static void ApplyVlessXh(JsonNode proxyOb, string transport)
    {
        var (address, port) = FrontEndpoint(transport);
        proxyOb["protocol"] = "vless";
        proxyOb["settings"] = VlessVnext(address, port);
        proxyOb["streamSettings"] = new JsonObject
        {
            ["network"] = "xhttp",
            ["security"] = "none",
            ["xhttpSettings"] = new JsonObject
            {
                ["path"] = Cfg.FrontXhttpPath,
                ["mode"] = "stream-one",
                ["xPaddingBytes"] = "100-1000",
                ["xmux"] = new JsonObject
                {
                    ["maxConcurrency"] = 1,
                    ["cMaxReuseTimes"] = 0,
                    ["hMaxRequestTimes"] = 1,
                    ["hMaxReusableSecs"] = 0,
                    ["hKeepAlivePeriod"] = 0,
                }
            },
            ["sockopt"] = KeepaliveSockopt(),
        };
        proxyOb.AsObject().Remove("mux");
    }

    private static JsonObject TlsSettingsFor(bool http2)
    {
        var alpn = new JsonArray();
        if (http2) alpn.Add("h2");
        alpn.Add("http/1.1");
        return new JsonObject
        {
            ["serverName"] = Cfg.ServerIp,
            ["fingerprint"] = "chrome",
            ["alpn"] = alpn,
            ["allowInsecure"] = false,
        };
    }

    private static void ApplyVlessTlsWs(JsonNode proxyOb)
    {
        var (address, port) = FrontEndpoint("tls-ws-b");
        proxyOb["protocol"] = "vless";
        proxyOb["settings"] = VlessVnext(address, port);
        proxyOb["streamSettings"] = new JsonObject
        {
            ["network"] = "ws",
            ["security"] = "tls",
            ["tlsSettings"] = TlsSettingsFor(false),
            ["wsSettings"] = new JsonObject
            {
                ["path"] = Cfg.FrontPath,
                ["host"] = Cfg.ServerIp,
                ["headers"] = new JsonObject { ["Host"] = Cfg.ServerIp },
            },
            ["sockopt"] = KeepaliveSockopt(),
        };
        proxyOb.AsObject().Remove("mux");
    }

    private static void ApplyVlessTlsXh(JsonNode proxyOb)
    {
        var (address, port) = FrontEndpoint("tls-xh-b");
        proxyOb["protocol"] = "vless";
        proxyOb["settings"] = VlessVnext(address, port);
        proxyOb["streamSettings"] = new JsonObject
        {
            ["network"] = "xhttp",
            ["security"] = "tls",
            ["tlsSettings"] = TlsSettingsFor(true),
            ["xhttpSettings"] = new JsonObject
            {
                ["host"] = Cfg.ServerIp,
                ["path"] = Cfg.FrontXhttpPath,
                ["mode"] = "auto",
                ["xPaddingBytes"] = "100-1000",
                ["xmux"] = new JsonObject
                {
                    ["maxConcurrency"] = 1,
                    ["cMaxReuseTimes"] = 0,
                    ["hMaxRequestTimes"] = 1,
                    ["hMaxReusableSecs"] = 0,
                    ["hKeepAlivePeriod"] = 0,
                }
            },
            ["sockopt"] = KeepaliveSockopt(),
        };
        proxyOb.AsObject().Remove("mux");
    }

    private static void ApplyShadowsocks(JsonNode proxyOb, string transport)
    {
        var (address, port) = SsEndpoint(transport);
        proxyOb["protocol"] = "shadowsocks";
        proxyOb["settings"] = new JsonObject
        {
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = address,
                    ["port"] = port,
                    ["method"] = Cfg.SsMethod,
                    ["password"] = Cfg.SsPassword,
                    ["uot"] = false,
                }
            }
        };
        proxyOb["streamSettings"] = new JsonObject
        {
            ["sockopt"] = KeepaliveSockopt(),
        };
        // Instagram/Cursor/сайты — этот outbound без mux. Telegram — клон
        // proxy-mux в WriteXrayConfig: concurrency 4 в 1.9.65 открывал новый
        // TCP на каждые 4 стрима → 79 ESTAB и HTTP/2 CANCEL.
        proxyOb.AsObject().Remove("mux");
    }

    private static JsonNode NewSsMux(string transport)
    {
        var muxOb = new JsonObject();
        ApplyShadowsocks(muxOb, transport);
        muxOb["tag"] = "proxy-mux";
        muxOb["mux"] = new JsonObject
        {
            ["enabled"] = true,
            ["concurrency"] = 128,
            ["xudpConcurrency"] = -1,
        };
        return muxOb;
    }

    private static void WriteXrayConfig(int level, string transport)
    {
        using var s = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("KovchegVPN.Resources.kovcheg-xray.json")!;
        var node = JsonNode.Parse(new StreamReader(s).ReadToEnd())!;
        node["log"] = new JsonObject { ["loglevel"] = "warning" };

        var outbounds = node["outbounds"]!.AsArray();
        var proxyOb = outbounds[0]!;
        bool ssMuxSplit = IsSs(transport);
        if (ssMuxSplit)
        {
            ApplyShadowsocks(proxyOb, transport);
            // mux первым = default. Иначе каждый сайт (как Instagram в 1.9.68)
            // открывает свой SS TCP, LigaLink душит download.
            outbounds.Insert(0, NewSsMux(transport));
        }
        else if (transport is "tls-ws-b")
            ApplyVlessTlsWs(proxyOb);
        else if (transport is "tls-xh-b")
            ApplyVlessTlsXh(proxyOb);
        else if (transport is "ws-b" or "ws")
            ApplyVlessWs(proxyOb, transport);
        else if (transport is "xh-b" or "xh")
            ApplyVlessXh(proxyOb, transport);
        else
        {
            var stream = proxyOb["streamSettings"]!.AsObject();
            var vnext = proxyOb["settings"]!["vnext"]![0]!;
            vnext["address"] = Cfg.ServerIp;
            vnext["port"] = 443;
            stream["realitySettings"]!["publicKey"] = Cfg.RealityPub;
            stream["realitySettings"]!["shortId"] = Cfg.RealitySid;
            stream["realitySettings"]!["fingerprint"] =
                transport == "xhttp-chrome" ? "chrome" : "firefox";
            if (stream["xhttpSettings"] is JsonObject xh)
                xh["path"] = Cfg.XhttpPath;
            switch (transport)
            {
            case "xhttp-amd":
            case "xhttp-amd6":
                vnext["port"] = Cfg.XhttpAmdPort;
                stream["realitySettings"]!["serverName"] = Cfg.RealitySniAmd;
                if (transport == "xhttp-amd6")
                    vnext["address"] = Cfg.ServerIp6;
                {
                    var frag = outbounds.FirstOrDefault(o => o?["tag"]?.GetValue<string>() == "frag");
                    if (frag?["settings"]?["fragment"] is JsonObject f)
                    {
                        f["packets"] = "tlshello";
                        f["length"] = "10-80";
                        f["interval"] = "1-8";
                    }
                }
                break;
            case "xhttp-alt":
            case "xhttp-nv":
            case "xhttp-nv-plain":
                vnext["port"] = Cfg.XhttpAltPort;
                stream["realitySettings"]!["serverName"] = Cfg.RealitySniAlt;
                if (transport != "xhttp-nv")
                    stream["sockopt"]!.AsObject().Remove("dialerProxy");
                else
                {
                    var frag = outbounds.FirstOrDefault(o => o?["tag"]?.GetValue<string>() == "frag");
                    if (frag?["settings"]?["fragment"] is JsonObject f)
                    {
                        f["length"] = "50-300";
                        f["interval"] = "1-10";
                    }
                }
                break;
            case "vision":
                vnext["port"] = Cfg.VisionPort;
                vnext["users"]![0]!["flow"] = "xtls-rprx-vision";
                stream.Remove("xhttpSettings");
                stream["network"] = "tcp";
                stream["realitySettings"]!["serverName"] = Cfg.RealitySni;
                break;
            case "xhttp":
            case "xhttp-chrome":
            case "xhttp6":
            case "xhttp-b":
                stream["realitySettings"]!["serverName"] = Cfg.RealitySni;
                if (transport == "xhttp6")
                    vnext["address"] = Cfg.ServerIp6;
                if (transport == "xhttp-b")
                    vnext["address"] = Cfg.ServerIpB;
                // 1.9.34 слал tlshello кусками — с дома TCP жил, Reality нет.
                stream["sockopt"]!.AsObject().Remove("dialerProxy");
                if (stream["xhttpSettings"] is JsonObject xhMux)
                    xhMux["xmux"] = new JsonObject { ["maxConcurrency"] = 1 };
                break;
            default:
                LogBus.Write($"core: неизвестный транспорт {transport}, беру xhttp");
                stream["realitySettings"]!["serverName"] = Cfg.RealitySni;
                break;
            }
            if (stream["sockopt"] is JsonObject so)
                StampLan(so);
        }
        outbounds.Add(JsonNode.Parse("{\"tag\":\"direct\",\"protocol\":\"freedom\"}"));
        outbounds.Add(JsonNode.Parse("{\"tag\":\"block\",\"protocol\":\"blackhole\"}"));

        var domains = new JsonArray();
        foreach (string d in DomainAlways) domains.Add(d);
        string[] geo = level == 2 ? GeoFull : level == 1 ? GeoCore : Array.Empty<string>();
        foreach (string g in geo) domains.Add("geosite:" + g);

        var rules = new JsonArray();
        var forceProxy = new JsonArray();
        foreach (string d in DomainForceProxy) forceProxy.Add(d);
        // Grok/LiveKit раньше udp/443-block: иначе голос в SOCKS с доменом
        // глушится как QUIC Cursor.
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["inboundTag"] = new JsonArray("http", "socks"),
            ["domain"] = forceProxy,
            ["outboundTag"] = "proxy"
        });
        // QUIC с дома (TSPU) и через CF WS рвёт стримы Cursor. Глушим udp/443
        // всегда — приложение падает на TCP через трубу.
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["network"] = "udp",
            ["port"] = 443,
            ["outboundTag"] = "block"
        });
        var poison = new JsonArray();
        foreach (var n in Cfg.PoisonNets) poison.Add(n.Cidr);
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["ip"] = poison,
            ["outboundTag"] = "block"
        });
        string tgOut = ssMuxSplit ? "proxy-mux" : "proxy";
        var tgDomains = new JsonArray();
        foreach (string d in DomainTelegram) tgDomains.Add(d);
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["inboundTag"] = new JsonArray("http", "socks"),
            ["domain"] = tgDomains,
            ["outboundTag"] = tgOut
        });
        // РФ (.ru/.рф), VK, Яндекс, YouTube — напрямую с TUN. DNS с роутера.
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["inboundTag"] = new JsonArray("http", "socks"),
            ["domain"] = domains,
            ["outboundTag"] = "direct"
        });
        if (level > 0)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray("http", "socks"),
                ["ip"] = new JsonArray("geoip:ru", "geoip:private"),
                ["outboundTag"] = "direct"
            });
        }
        var tg = new JsonArray();
        foreach (string c in Cfg.TelegramCidrs) tg.Add(c);
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["ip"] = tg,
            ["outboundTag"] = tgOut
        });
        // Не-HTTPS через трубу = сотни TCP (Telegram DC ок, а 172.121:3100/505 — нет).
        // 80/443/853 остаются на default proxy / forceProxy.
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["network"] = "tcp",
            ["port"] = "1-79,81-442,444-852,854-65535",
            ["outboundTag"] = "direct"
        });
        node["routing"] = new JsonObject
        {
            ["domainStrategy"] = "AsIs",
            ["rules"] = rules
        };

        Cfg.WriteJson(Cfg.XrayConfigPath, node);
    }

    private static bool XrayTest(string exe, string? assetDir)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, $"-test -config \"{Cfg.XrayConfigPath}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (assetDir != null) psi.Environment["XRAY_LOCATION_ASSET"] = assetDir;
            using var p = Process.Start(psi);
            if (p == null) return false;
            string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            if (p.ExitCode != 0)
            {
                string tail = string.Join(" | ", outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(2).Select(l => l.Trim()));
                LogBus.Write("core: xray -test: " + tail);
            }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LogBus.Write("core: xray -test FAIL " + ex.Message);
            return false;
        }
    }
}
