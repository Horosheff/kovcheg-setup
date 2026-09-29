using Microsoft.Win32;

namespace KovchegVPN;

public static class SysProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    // РФ-домены и локальные адреса идут напрямую, остальное — через VPS.
    // YouTube в обход не входит.
    private const string OverrideList =
        "<-loopback>;<local>;" +
        "*.ru;*.su;*.рф;*.xn--p1ai;" +
        "vk.com;*.vk.com;*.vk.me;*.vk.cc;*.mvk.com;" +
        "*.vkuseraudio.net;*.vkuservideo.net;*.vkuser.net;" +
        "*.userapi.com;*.vk-cdn.net;*.vk-portal.net;*.mycdn.me;" +
        "yandex.net;*.yandex.net;yandex.com;*.yandex.com;yastatic.net;*.yastatic.net;" +
        "ozonstatic.net;*.ozonstatic.net;wstatic.net;*.wstatic.net;" +
        "*.2gis.com;*.2gis.ru;";

    public static (bool Enabled, string Server) Get()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        bool en = k?.GetValue("ProxyEnable") is int v && v == 1;
        string srv = k?.GetValue("ProxyServer") as string ?? "";
        return (en, srv);
    }

    public static bool IsOurProxy()
    {
        var (en, srv) = Get();
        return en && srv.StartsWith("127.0.0.1:", StringComparison.Ordinal);
    }

    public static void Enable(int port)
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)!;
        k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        k.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
        k.SetValue("ProxyOverride", OverrideList, RegistryValueKind.String);
        Native.NotifyProxyChanged();
    }

    public static void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)!;
        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        Native.NotifyProxyChanged();
    }
}
