using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace KovchegVPN;

// Ядра для ПК без v2rayN: xray.exe, sing-box.exe, wintun.dll.
// Манифест bins.json с VPS/GitHub, подписан P-256; без валидной
// подписи и совпавшего sha256 файл на диск не ложится.
public static class Bins
{
    private static readonly string[] Required = { "xray.exe", "sing-box.exe", "wintun.dll" };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool Present() =>
        Required.All(f => File.Exists(Path.Combine(Cfg.BinDir, f)));

    // true — ядра есть (были или скачались). Пишет прогресс в лог.
    public static async Task<bool> EnsureAsync(Action<string>? status = null)
    {
        if (Cfg.V2rayNDir() != null && Cfg.XrayExe() != null) return true;
        if (Present()) return true;

        await Gate.WaitAsync();
        try
        {
            if (Present()) return true;
            Directory.CreateDirectory(Cfg.BinDir);
            status?.Invoke("Скачиваю ядро с сервера…");
            LogBus.Write("bins: v2rayN нет — беру ядра с VPS/GitHub");

            var h = new SocketsHttpHandler { UseProxy = false };
            Probe.ApplyTapBind(h);
            using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(300) };
            string? json = null;
            Exception? last = null;
            foreach (string url in Updater.BinsManifestUrls().Distinct())
            {
                try
                {
                    json = await c.GetStringAsync(Updater.Bust(url));
                    LogBus.Write($"bins: манифест {url}");
                    last = null;
                    break;
                }
                catch (Exception ex) { last = ex; LogBus.Write($"bins: {url} — {ex.Message}"); }
            }
            if (json == null)
            {
                LogBus.Write("bins: FAIL " + (last?.Message ?? "манифест недоступен"));
                return false;
            }
            var list = JsonSerializer.Deserialize<UpdateInfo[]>(json) ?? Array.Empty<UpdateInfo>();

            foreach (string name in Required)
            {
                string target = Path.Combine(Cfg.BinDir, name);
                if (File.Exists(target)) continue;
                var info = list.FirstOrDefault(i => string.Equals(i.version, name, StringComparison.OrdinalIgnoreCase));
                if (info == null || info.url == null || info.sha256 == null)
                {
                    LogBus.Write($"bins: в манифесте нет {name}");
                    return false;
                }
                if (!Updater.VerifySignature(info))
                {
                    LogBus.Write($"bins: {name} без валидной подписи — отказ");
                    return false;
                }
                status?.Invoke($"Скачиваю {name}…");
                string fileUrl = Updater.ThroughFront(info.url);
                byte[] data = MaybeUnzip(await c.GetByteArrayAsync(fileUrl), name);
                if (info.size > 0 && data.Length != info.size)
                {
                    LogBus.Write($"bins: {name} размер {data.Length} != {info.size}");
                    return false;
                }
                string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                if (!hash.Equals(info.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    LogBus.Write($"bins: {name} sha256 не сошёлся");
                    return false;
                }
                File.WriteAllBytes(target + ".tmp", data);
                File.Move(target + ".tmp", target, true);
                LogBus.Write($"bins: {name} готов ({data.Length} байт)");
            }
            return Present();
        }
        catch (Exception ex)
        {
            LogBus.Write("bins: FAIL " + ex.Message);
            return false;
        }
        finally { Gate.Release(); }
    }

    private static byte[] MaybeUnzip(byte[] raw, string name)
    {
        if (raw.Length < 4 || raw[0] != (byte)'P' || raw[1] != (byte)'K') return raw;
        using var ms = new MemoryStream(raw);
        using var za = new ZipArchive(ms, ZipArchiveMode.Read);
        var e = za.Entries.FirstOrDefault(x =>
            x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            x.FullName.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
        if (e == null)
        {
            LogBus.Write($"bins: в zip нет {name}");
            return raw;
        }
        using var es = e.Open();
        using var outMs = new MemoryStream();
        es.CopyTo(outMs);
        LogBus.Write($"bins: из zip достали {e.FullName} ({outMs.Length} байт)");
        return outMs.ToArray();
    }
}
