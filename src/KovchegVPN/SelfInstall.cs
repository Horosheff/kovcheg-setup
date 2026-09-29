using System.Diagnostics;
using System.IO;

namespace KovchegVPN;

public static class SelfInstall
{
    public static bool IsInstalled()
    {
        string cur = Environment.ProcessPath ?? "";
        return string.Equals(cur, Cfg.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    // Копия в %LOCALAPPDATA%\KovchegVPN + ярлык на рабочем столе.
    public static string? Install()
    {
        try
        {
            Directory.CreateDirectory(Cfg.Dir);
            string src = Environment.ProcessPath!;
            long srcLen = new FileInfo(src).Length;
            LogBus.Write($"install: из {src} ({srcLen} байт)");

            // Гасим свою неадминскую копию. Админских зомби не достать —
            // но им и не нужен этот файл после переименования.
            int me = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("KovchegVPN"))
            {
                try
                {
                    if (p.Id == me) continue;
                    if (p.MainModule?.FileName != null &&
                        p.MainModule.FileName.Equals(Cfg.ExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        LogBus.Write($"install: гашу старый UI pid {p.Id}");
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
            }

            // Работающий exe нельзя перезаписать, но МОЖНО переименовать.
            if (File.Exists(Cfg.ExePath))
            {
                string old = Path.Combine(Cfg.Dir, $"KovchegVPN.old.{DateTime.Now:yyyyMMddHHmmss}.exe");
                File.Move(Cfg.ExePath, old);
                LogBus.Write($"install: старый exe убран в {Path.GetFileName(old)}");
            }
            File.Copy(src, Cfg.ExePath, true);

            long dstLen = new FileInfo(Cfg.ExePath).Length;
            if (dstLen != srcLen)
            {
                LogBus.Write($"install: FAIL размер {dstLen} != {srcLen}");
                return "копия не совпала по размеру";
            }

            Cfg.ExtractAssets();

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            CreateShortcut(Path.Combine(desktop, "KovchegVPN.lnk"));
            CreateShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "KovchegVPN.lnk"));

            Process.Start(new ProcessStartInfo(Cfg.ExePath) { UseShellExecute = true });
            LogBus.Write("install: ok, перезапуск из AppData");
            return null;
        }
        catch (Exception ex)
        {
            LogBus.Write("install: FAIL " + ex.Message);
            return ex.Message;
        }
    }

    private static void CreateShortcut(string path)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic lnk = shell.CreateShortcut(path);
        lnk.TargetPath = Cfg.ExePath;
        lnk.WorkingDirectory = Cfg.Dir;
        lnk.IconLocation = Cfg.IcoPath;
        lnk.Description = "KovchegVPN";
        lnk.Save();
    }
}
