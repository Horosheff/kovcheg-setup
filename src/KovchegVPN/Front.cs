namespace KovchegVPN;

// Прямой VPS + GitHub OTA. Cloudflare в клиенте не используется.
public static class Front
{
    public static string OtaLatest => $"http://{Cfg.ServerIp}/latest.json";
    public static string OtaExe => $"http://{Cfg.ServerIp}/KovchegVPN.exe";
    public static string BinsManifest => Cfg.BinsManifestUrl;

    public static Task RefreshAsync() => Task.CompletedTask;
}
