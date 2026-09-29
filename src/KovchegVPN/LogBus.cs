using System.IO;
using System.Reflection;

namespace KovchegVPN;

// Живой лог: каждая строка сразу в файл (AppendAllText = без буфера) и в окно.
// Ротация: > 2 МБ → kovcheg.log.1 (одна прошлая копия).
public static class LogBus
{
    public static event Action<string>? Line;

    private const long MaxBytes = 4_000_000;
    private static int _writes;
    private static readonly object Gate = new();

    public static string Version =>
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    public static void Write(string msg) => Append($"{DateTime.Now:HH:mm:ss} {msg}", true);

    public static void Append(string line, bool toUi)
    {
        lock (Gate)
        {
            try
            {
                if (++_writes % 100 == 0) RotateIfNeeded();
                File.AppendAllText(Cfg.LogPath, line + "\r\n");
            }
            catch { }
        }
        if (toUi) { try { Line?.Invoke(line); } catch { } }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(Cfg.LogPath);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            File.Move(Cfg.LogPath, Cfg.LogPath + ".1", true);
        }
        catch { }
    }
}
