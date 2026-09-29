using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KovchegVPN;

public class UpdateInfo
{
    public string? version { get; set; }
    public string? url { get; set; }
    public string? url_zip { get; set; }
    public string? sha256 { get; set; }
    public long size { get; set; }
    public string? notes { get; set; }
    public string? sig { get; set; }
}

public enum UpdateCheckStatus
{
    Unknown,
    Available,
    UpToDate,
    Unreachable,
}

public static class Updater
{
    // GitHub/jsDelivr и прямой VPS. Cloudflare не используем.
    public static IEnumerable<string> ManifestUrls()
    {
        yield return $"http://{Cfg.ServerIp}/latest.json";
        yield return Front.OtaLatest;
    }

    public static IEnumerable<string> BinsManifestUrls()
    {
        yield return Cfg.BinsManifestUrl;
        yield return Front.BinsManifest;
    }

    public static string Bust(string url) =>
        url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") +
        "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Публичный ключ P-256 (SPKI). Приватный — только на VPS. Манифест по HTTP,
    // поэтому без валидной подписи обновление не предлагается вовсе.
    private const string PubKeyB64 =
        "REPLACE_OTA_PUB";

    // Без UA filebin и др. отдают HTML-страницу вместо файла.
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                              "(KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    public static UpdateInfo? Available { get; private set; }
    public static UpdateCheckStatus LastStatus { get; private set; } = UpdateCheckStatus.Unknown;

    private static HttpClient MakeClient(int timeoutSec)
    {
        var h = new SocketsHttpHandler { UseProxy = false };
        Probe.ApplyTapBind(h);
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
        c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return c;
    }

    public static bool VerifyPayload(string payload, string? sig)
    {
        try
        {
            if (string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(payload)) return false;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PubKeyB64), out _);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(sig),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch { return false; }
    }

    public static bool VerifySignature(UpdateInfo i)
    {
        if (string.IsNullOrEmpty(i.sig) || i.version == null || i.url == null || i.sha256 == null) return false;
        return VerifyPayload($"{i.version}\n{i.url}\n{i.sha256}\n{i.size}\n", i.sig);
    }

    public static string ThroughFront(string url)
    {
        return url;
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        Available = null;
        LastStatus = UpdateCheckStatus.Unknown;
        UpdateInfo? best = null;
        Version bestVer = new(0, 0, 0);
        bool anyOk = false;
        Version local = new(LogBus.Version);

        foreach (string manifestUrl in ManifestUrls().Distinct())
        {
            try
            {
                using var c = MakeClient(8);
                string json = await c.GetStringAsync(Bust(manifestUrl));
                var info = JsonSerializer.Deserialize<UpdateInfo>(json);
                if (info?.version == null || info.url == null) continue;
                if (!Version.TryParse(info.version, out var remote)) continue;
                if (!VerifySignature(info))
                {
                    LogBus.Write($"update: v{info.version} в {manifestUrl} без валидной подписи — игнорирую");
                    continue;
                }
                anyOk = true;
                if (remote > bestVer)
                {
                    bestVer = remote;
                    best = info;
                }
            }
            catch (Exception ex) { LogBus.Write($"update: {manifestUrl} — {ex.Message}"); }
        }

        if (best != null && bestVer > local)
        {
            Available = best;
            LastStatus = UpdateCheckStatus.Available;
            LogBus.Write($"update: доступна v{best.version}");
            return best;
        }
        if (anyOk)
        {
            LastStatus = UpdateCheckStatus.UpToDate;
            LogBus.Write($"update: актуально (v{LogBus.Version})");
            return null;
        }
        LastStatus = UpdateCheckStatus.Unreachable;
        LogBus.Write("update: нет связи с манифестом (GitHub/VPS)");
        return null;
    }

    // null = ок (новый процесс запущен), иначе текст ошибки.
    // Вызывать только когда State == Off — сеть не рвём.
    public static async Task<string?> ApplyAsync(UpdateInfo info)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "KovchegVPN-update.exe");
        try
        {
            if (!VerifySignature(info)) return "манифест не подписан — обновление отклонено";

            byte[]? exe = await FetchExeAsync(info);
            if (exe == null) return "сервер отдал не exe (детали в логе)";

            if (info.size > 0 && exe.Length != info.size)
                return $"размер не сошёлся ({exe.Length} != {info.size})";
            string hash = Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
            if (!hash.Equals(info.sha256, StringComparison.OrdinalIgnoreCase))
            {
                ForgetPartial(info);
                return "sha256 не сошёлся";
            }
            LogBus.Write("update: подпись и sha256 ок");
            File.WriteAllBytes(tmp, exe);
            ForgetPartial(info);

            Directory.CreateDirectory(Cfg.Dir);
            if (File.Exists(Cfg.ExePath))
            {
                string old = Path.Combine(Cfg.Dir, $"KovchegVPN.old.{DateTime.Now:yyyyMMddHHmmss}.exe");
                File.Move(Cfg.ExePath, old);
            }
            File.Copy(tmp, Cfg.ExePath, true);
            try { File.Delete(tmp); } catch { }

            LogBus.Write("update: установлено, перезапуск");
            Process.Start(new ProcessStartInfo(Cfg.ExePath) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            LogBus.Write("update: FAIL " + ex.Message);
            return ex.Message;
        }
    }

    private static async Task<byte[]?> FetchExeAsync(UpdateInfo info)
    {
        if (info.url != null)
        {
            byte[]? raw = null;
            foreach (string u in new[] { ThroughFront(info.url) }.Concat(Cfg.OtaExeUrls).Distinct())
            {
                try
                {
                    raw = await Download(u, info.size);
                    LogBus.Write($"update: скачано {raw.Length} байт с {u}");
                    byte[]? exe = AsExe(raw);
                    if (exe != null) return exe;
                }
                catch (Exception ex) { LogBus.Write($"update: {u} — {ex.Message}"); }
            }
        }
        if (info.url_zip != null)
        {
            byte[] raw = await Download(ThroughFront(info.url_zip), 0);
            LogBus.Write($"update: zip скачан {raw.Length} байт");
            byte[]? ex = Unzip(raw);
            if (ex != null) return ex;
        }
        return null;
    }

    private static byte[]? AsExe(byte[] raw)
    {
        if (raw.Length < 1_000_000)
        {
            LogBus.Write($"update: это не exe, html {raw.Length} байт");
            return null;
        }
        if (raw[0] == 'M' && raw[1] == 'Z') return raw;
        if (raw[0] == 'P' && raw[1] == 'K')
        {
            byte[]? ex = Unzip(raw);
            if (ex != null) return ex;
        }
        else
            LogBus.Write("update: неизвестная сигнатура файла");
        return null;
    }

    private static string PartialPath(string url) =>
        Path.Combine(Path.GetTempPath(),
            "kovcheg-dl-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".bin");

    private static void ForgetPartial(UpdateInfo info)
    {
        var urls = new List<string>(Cfg.OtaExeUrls);
        if (!string.IsNullOrEmpty(info.url)) urls.Insert(0, info.url);
        foreach (string u in urls.Distinct())
        {
            try { File.Delete(PartialPath(ThroughFront(u))); } catch { }
        }
    }

    private static async Task<byte[]> Download(string url, long expected)
    {
        string tmp = PartialPath(url);
        long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        if (expected > 0 && have > expected)
        {
            try { File.Delete(tmp); } catch { }
            have = 0;
        }

        using var c = MakeClient(300);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
            req.Headers.Range = new RangeHeaderValue(have, null);

        using var resp = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
        {
            LogBus.Write($"update: {url} Range {have} уже полный");
            return await File.ReadAllBytesAsync(tmp);
        }
        resp.EnsureSuccessStatusCode();
        bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!partial && have > 0)
        {
            LogBus.Write($"update: {url} без Range — сначала");
            try { File.Delete(tmp); } catch { }
            have = 0;
        }
        else if (partial)
            LogBus.Write($"update: {url} догруз с {have}");

        await using (var fs = new FileStream(tmp, partial ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
            await resp.Content.CopyToAsync(fs);

        return await File.ReadAllBytesAsync(tmp);
    }

    private static byte[]? Unzip(byte[] zip)
    {
        try
        {
            using var ms = new MemoryStream(zip);
            using var za = new ZipArchive(ms, ZipArchiveMode.Read);
            var e = za.Entries.FirstOrDefault(x => x.Name.Equals("KovchegVPN.exe", StringComparison.OrdinalIgnoreCase))
                 ?? za.Entries.FirstOrDefault(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (e == null) { LogBus.Write("update: в zip нет exe"); return null; }
            using var es = e.Open();
            using var outMs = new MemoryStream();
            es.CopyTo(outMs);
            LogBus.Write($"update: из zip достали {e.Name} ({outMs.Length} байт)");
            return outMs.ToArray();
        }
        catch (Exception ex) { LogBus.Write("update: unzip FAIL " + ex.Message); return null; }
    }
}
