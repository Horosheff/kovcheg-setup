using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KovchegVPN;

public static class Cfg
{
    // Заглушки. scripts/stamp.py подставляет client.env с вашего VPS.
    // YouTube здесь не в домашнем обходе: он идёт через туннель.
    public const string ServerIp = "203.0.113.10";
    public const string ServerIpB = "203.0.113.10";
    public const string ServerIp6 = "2001:db8::10";
    public const string ServerIp6Prefix = "2001:db8:ffff:";
    public const string ExitCountry = "REPLACE_COUNTRY";
    public const string ExitCountryIso = "REPLACE_ISO";
    public const string ExitCountryBanner = "REPLACE_BANNER";
    public static bool IsOurs(string? ip) =>
        !string.IsNullOrEmpty(ip) &&
        (ip == ServerIp ||
         ip == ServerIpB ||
         ip == ServerIp6 ||
         ip.StartsWith(ServerIp6Prefix, StringComparison.OrdinalIgnoreCase));
    public const int VisionPort = 8443;
    // Запасной xHTTP-порт: если ТСПУ прижал IP:443, пробуем тот же Reality на 2053.
    public const int XhttpAltPort = 2053;
    // Свежий порт: LigaLink профилирует 443+apple и 2053+nvidia за сутки.
    public const int XhttpAmdPort = 2083;
    public const int Ss443Port = 443;
    public const int SsPort = 2096;
    // Тот же SS на порту, куда LigaLink с дома уже пускает TCP (2053).
    public const int SsAltPort = 2053;
    // :2053/:2096 LigaLink уже душит пачку TCP. Свежий SS, тот же ключ.
    public const int SsHiPort = 9443;
    // Свежий SS: LigaLink уже ест payload на :9443/:2053/:2096 (TCP жив, xray не accepted).
    public const int SsFreshPort = 8444;
    public const string SsMethod = "2022-blake3-aes-128-gcm";
    public const string SsPassword = "REPLACE_SS_PASSWORD";
    // Высокий порт + другой SNI: после суток Apple:443 LigaLink начал
    // глушить ClientHello (8 сен, 20:11 UTC — 0 успешных при живом TCP).
    public const int XhttpHiPort = 41631;
    public const string Uuid = "00000000-0000-4000-8000-000000000001";
    public const string RealityPub = "REPLACE_REALITY_PUB";
    public const string RealitySid = "0011223344556677";
    public const string XhttpPath = "/replacepath";
    // Только хосты с КОРОТКОЙ цепочкой (<~5 КБ). intel.com ~2.8 КБ, TLS 1.3.
    // apple/microsoft на прошлом IP LigaLink уже профилировал.
    public const string RealitySni = "www.intel.com";
    public const string RealitySniAlt = "www.nvidia.com";
    public const string RealitySniAmd = "www.amd.com";
    public const string BinsManifestUrl = "http://203.0.113.10/bin/bins.json";
    public static readonly string[] OtaExeUrls =
    {
        $"http://{ServerIp}/KovchegVPN.exe",
    };
    public const string FrontPath = "/vlessws";
    public const string FrontXhttpPath = "/xh";

    // Ссылки для телефона. fp=firefox: на РФ-сетях (ТСПУ) Chrome-отпечаток
    // TLS режут, Firefox проходит — подтверждено в обсуждениях telemt/xray.
    // 1) TCP+Reality+Vision :8443 — понимают все клиенты (v2rayNG, Hiddify, Streisand, Happ).
    public static string PhoneVlessLink => PhoneVlessLinkXhttp;
    public static string PhoneVlessLinkVision =>
        $"vless://{Uuid}@{ServerIp}:{VisionPort}?encryption=none&security=reality&sni={RealitySni}&fp=firefox" +
        $"&pbk={RealityPub}&sid={RealitySid}&type=tcp&flow=xtls-rprx-vision#KovchegVPN-vision";
    public const string MtgSecret = "REPLACE_MTG_SECRET";
    public static string TgProxyLink =>
        $"https://t.me/proxy?server={ServerIp}&port=2087&secret={MtgSecret}";
    public static string PhoneSsLink
    {
        get
        {
            string userinfo = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{SsMethod}:{SsPassword}"));
            return $"ss://{userinfo}@{ServerIp}:{SsPort}#KovchegVPN-SS";
        }
    }
    // 3) xHTTP :443 — как запас на чистых сетях; только Xray-клиенты.
    public static string PhoneVlessLinkXhttp =>
        $"vless://{Uuid}@{ServerIp}:443?encryption=none&security=reality&sni={RealitySni}&fp=firefox" +
        $"&pbk={RealityPub}&sid={RealitySid}&type=xhttp&path=%2F{XhttpPath.TrimStart('/')}&mode=auto#KovchegVPN-443";
    public static string PhoneVlessLinkXhttp6 =>
        $"vless://{Uuid}@[{ServerIp6}]:443?encryption=none&security=reality&sni={RealitySni}&fp=firefox" +
        $"&pbk={RealityPub}&sid={RealitySid}&type=xhttp&path=%2F{XhttpPath.TrimStart('/')}&mode=auto#KovchegVPN-443v6";

    // Телефон: SS-2022 и ссылка MTProto, если install.sh поднял mtg.

    public const int SocksPort = 10808;
    public const int OwnSocksPort = 10818;
    public const int OwnHttpPort = 10819;
    // Своё ядро первым: только в нём есть сплит-маршрутизация (РФ напрямую).
    public static readonly int[] HttpPorts = { OwnHttpPort, 12809, 10809 };
    public const string TunIp = "10.0.85.1";
    // Шлюз TUN для /1. Не ставить его DNS-сервером: 10.0.85.2 внутри
    // route_exclude 10.0.0.0/8 — Windows ждёт таймаут, трафик 0.1 кбит/с.
    public const string TunGw = "10.0.85.2";
    // Публичный DNS не из Auto-DoH Windows. Пакет идёт в TUN → hijack.
    public const string DnsPin = "94.140.14.14";
    // LigaLink травит A-записи в эти сети. Блокируем, даже если DNS утечёт.
    public static readonly (string Net, string Mask, string Cidr)[] PoisonNets =
    {
        ("8.47.0.0", "255.255.0.0", "8.47.0.0/16"),
        ("8.6.0.0", "255.255.0.0", "8.6.0.0/16"),
    };
    // Grok Bot (локальное приложение). UDP/STUN иначе уходит в LAN —
    // «сбой соединения в реальном времени».
    public static readonly string[] GrokProcessNames =
    {
        "Grok Bot.exe", "GrokBot.exe", "Grok.exe",
        "Grok Bot", "GrokBot", "Grok",
    };
    // LigaLink травит заблокированные имена в 8.47, если DNS в 1.1.1.1 с LAN.
    // Эти суффиксы — DoH через трубу. Остальной чужой DNS тоже (final=secure).
    public static readonly string[] TunnelDnsSuffixes =
    {
        "grok.com", "x.ai", "livekit.cloud", "livekit.io",
        "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com",
        "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com",
        "youtube-nocookie.com", "youtubei.googleapis.com", "youtube.googleapis.com",
        "googleusercontent.com",
    };

    // Домашний обход TUN: .ru / .рф / VK / Яндекс. YouTube сюда не входит —
    // у обычного провайдера он режется, поэтому идёт в туннель.
    public static readonly string[] HomeDirectSuffixes =
    {
        "ru", "xn--p1ai", "su",
        "vk.com", "vk.me", "vk.cc", "mvk.com",
        "userapi.com", "vk-cdn.net", "vk-portal.net",
        "vkuseraudio.net", "vkuservideo.net", "vkuser.net",
        "yandex.net", "yandex.com", "yastatic.net",
        "mycdn.me", "ozonstatic.net", "wstatic.net", "2gis.com",
    };

    // Telegram DC — через TUN, как Cursor. Не в РФ-direct.
    public static readonly string[] TelegramCidrs =
    {
        "149.154.160.0/20",
        "91.108.4.0/22",
        "91.108.8.0/22",
        "91.108.12.0/22",
        "91.108.16.0/22",
        "91.108.20.0/22",
        "91.108.36.0/23",
        "91.108.56.0/22",
    };
    public const string PoisonNet = "8.47.0.0";
    public const string PoisonMask = "255.255.0.0";
    public const string PoisonCidr = "8.47.0.0/16";

    // Порядок выбора ядра для TUN: socks лучше (UDP для голоса).
    public static readonly (string Kind, int Port)[] Endpoints =
    {
        ("socks", SocksPort), ("socks", OwnSocksPort),
        ("http", 12809), ("http", 10809), ("http", OwnHttpPort)
    };

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KovchegVPN");
    public static string ExePath => Path.Combine(Dir, "KovchegVPN.exe");
    public static string IcoPath => Path.Combine(Dir, "kovcheg.ico");
    public static string IcoOnPath => Path.Combine(Dir, "kovcheg_on.ico");
    public static string IcoOffPath => Path.Combine(Dir, "kovcheg_off.ico");
    public static string LogPath => Path.Combine(Dir, "kovcheg.log");
    public static string SbConfigPath => Path.Combine(Dir, "singbox-tun.json");
    public static string XrayConfigPath => Path.Combine(Dir, "kovcheg-xray.json");
    public static string GeoDir => Path.Combine(Dir, "geo");
    // Свои ядра (для ПК без v2rayN): скачиваются с VPS по подписанному bins.json.
    public static string BinDir => Path.Combine(Dir, "bin");
    public static string ResultPath => Path.Combine(Dir, "tun-result.txt");
    public static string IniPath => Path.Combine(Dir, "settings.ini");

    public static string Gateway => IniGet("Gateway") ?? "192.168.0.1";

    // 1 = сплит TUN (РФ напрямую), 0 = весь ПК через трубу.
    public static bool TunSplit => IniGet("TunSplit") == "1";

    public static string? IniGet(string key)
    {
        try
        {
            if (!File.Exists(IniPath)) return null;
            foreach (var line in File.ReadAllLines(IniPath))
            {
                var t = line.Trim();
                if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    return t[(key.Length + 1)..].Trim();
            }
        }
        catch { }
        return null;
    }

    public static string? V2rayNDir()
    {
        var ini = IniGet("V2rayNDir");
        if (!string.IsNullOrWhiteSpace(ini) && HasSingBox(ini)) return ini;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try
        {
            var winget = Path.Combine(local, @"Microsoft\WinGet\Packages");
            if (Directory.Exists(winget))
                foreach (var d in Directory.GetDirectories(winget, "2dust.v2rayN*"))
                {
                    var hit = FindSingBox(d, 2);
                    if (hit != null) return hit;
                }
        }
        catch { }

        foreach (var c in new[] { Path.Combine(local, "v2rayN"), @"C:\Tools\v2rayN" })
            if (HasSingBox(c)) return c;
        return null;
    }

    private static bool HasSingBox(string dir) =>
        File.Exists(Path.Combine(dir, "bin", "sing_box", "sing-box.exe"));

    private static string? FindSingBox(string root, int depth)
    {
        try
        {
            if (HasSingBox(root)) return root;
            if (depth <= 0) return null;
            foreach (var d in Directory.GetDirectories(root))
            {
                var r = FindSingBox(d, depth - 1);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }

    // Порядок: v2rayN (если стоит) → свои ядра в %LOCALAPPDATA%\KovchegVPN\bin.
    public static string? SingBoxExe()
    {
        var d = V2rayNDir();
        if (d != null) return Path.Combine(d, "bin", "sing_box", "sing-box.exe");
        string own = Path.Combine(BinDir, "sing-box.exe");
        return File.Exists(own) && File.Exists(Path.Combine(BinDir, "wintun.dll")) ? own : null;
    }

    public static string? V2rayNExe()
    {
        var d = V2rayNDir();
        if (d == null) return null;
        var p = Path.Combine(d, "v2rayN.exe");
        return File.Exists(p) ? p : null;
    }

    public static string? XrayExe()
    {
        var d = V2rayNDir();
        if (d != null)
        {
            var p = Path.Combine(d, "bin", "xray", "xray.exe");
            if (File.Exists(p)) return p;
        }
        string own = Path.Combine(BinDir, "xray.exe");
        return File.Exists(own) ? own : null;
    }

    public static void ExtractAssets()
    {
        Extract("KovchegVPN.Resources.kovcheg.ico", IcoPath);
        Extract("KovchegVPN.Resources.kovcheg_on.ico", IcoOnPath);
        Extract("KovchegVPN.Resources.kovcheg_off.ico", IcoOffPath);
        Extract("KovchegVPN.Resources.singbox-tun.json", SbConfigPath);
        Extract("KovchegVPN.Resources.kovcheg-xray.json", XrayConfigPath);
    }

    private static void Extract(string res, string path)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
            if (s == null) return;
            using var f = File.Create(path);
            s.CopyTo(f);
        }
        catch { }
    }

    public static void Log(string msg) =>
        LogBus.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}", false);

    // JsonNode.ToJsonString(new JsonSerializerOptions{WriteIndented=true}) в
    // single-file .NET 8 падает: "must specify a TypeInfoResolver". Пишем через
    // Utf8JsonWriter — конфиг xray/sing-box иначе не создаётся, ядро «не поднимается».
    public static void WriteJson(string path, JsonNode node)
    {
        using var fs = File.Create(path);
        using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        node.WriteTo(writer);
    }

    public static void IniSet(string key, string value)
    {
        try
        {
            var lines = File.Exists(IniPath) ? File.ReadAllLines(IniPath).ToList() : new List<string>();
            int idx = lines.FindIndex(l => l.Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) lines[idx] = $"{key}={value}";
            else lines.Add($"{key}={value}");
            File.WriteAllLines(IniPath, lines);
        }
        catch { }
    }

    public static bool IniFlag(string key) => IniGet(key) is "1" or "true" or "yes";
}
