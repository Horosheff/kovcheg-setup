using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace KovchegVPN;

// Через socks xray открывает свой :80 (hairpin).
public static class LogSender
{
    private const int MaxChars = 3_000_000;
    private const int TailChars = 80_000;

    public static string? Send(int timeoutSec = 20)
    {
        try
        {
            Ping();
            string content = ReadLog();
            if (content.Length > MaxChars)
                content = content[^MaxChars..];

            // Сначала хвост: 1.5 МБ за 8 с LigaLink рвёт, и лог не улетает.
            string? name = PutChunk(content.Length > TailChars ? content[^TailChars..] : content, timeoutSec, "tail");
            if (name != null && content.Length > TailChars)
                PutChunk(content, Math.Max(timeoutSec, 25), "full");
            return name;
        }
        catch (Exception ex)
        {
            Cfg.Log("logsend: FAIL " + ex.Message);
            return null;
        }
    }

    private static string ReadLog()
    {
        using var fs = new FileStream(Cfg.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static SocketsHttpHandler DirectHandler()
    {
        var h = new SocketsHttpHandler { UseProxy = false };
        Probe.ApplyTapBind(h);
        return h;
    }

    private static void Ping()
    {
        try
        {
            using var c = new HttpClient(DirectHandler()) { Timeout = TimeSpan.FromSeconds(6) };
            string url = $"http://{Cfg.ServerIp}/latest.json?logping={LogBus.Version}&t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var resp = c.GetAsync(url).GetAwaiter().GetResult();
            Cfg.Log($"logsend: ping {(int)resp.StatusCode} {url}");
        }
        catch (Exception ex) { Cfg.Log("logsend: ping " + ex.Message); }
    }

    private static string? PutChunk(string content, int timeoutSec, string kind)
    {
        string name = $"kovcheg-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        int slice = Math.Clamp(timeoutSec, 8, 30);

        if (Put(content, slice, socks: false,
                $"http://{Cfg.ServerIp}/logs/{name}") != null)
        {
            Cfg.Log($"logsend: {kind} {content.Length} байт ок");
            return name;
        }

        bool socksOpen = Probe.PortOpen(Cfg.OwnSocksPort).GetAwaiter().GetResult();
        if (!socksOpen)
        {
            Cfg.Log($"logsend: {kind} прямая отправка не вышла, socks закрыт");
            return null;
        }
        Cfg.Log($"logsend: {kind} напрямую не вышло — пробую socks");
        return Put(content, slice, socks: true,
                $"http://{Cfg.ServerIp}/logs/{name}") != null
            ? name
            : null;
    }

    private static string? Put(string content, int timeoutSec, bool socks, params string[] urls)
    {
        var h = new SocketsHttpHandler();
        if (socks)
        {
            h.Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{Cfg.OwnSocksPort}"));
            h.UseProxy = true;
        }
        else
        {
            h.UseProxy = false;
            Probe.ApplyTapBind(h);
        }

        using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        foreach (string url in urls)
        {
            try
            {
                var resp = c.PutAsync(url, new StringContent(content, Encoding.UTF8, "text/plain"))
                    .GetAwaiter().GetResult();
                Cfg.Log($"logsend: {(int)resp.StatusCode} {url}{(socks ? " via socks" : "")}");
                if ((int)resp.StatusCode == 200) return url;
            }
            catch (Exception ex) { Cfg.Log($"logsend: {url} — {ex.Message}"); }
        }
        return null;
    }
}
