using System.Diagnostics;
using Microsoft.Win32;

namespace KovchegVPN;

// Пользовательские настройки: автозапуск с Windows, последний режим, тема.
public static class Prefs
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "KovchegVPN";
    private const string TaskName = "KovchegVPN";

    public static bool AutoStart
    {
        get => TaskExists() || RunValue() != null;
        set
        {
            try
            {
                ClearRunKey();
                if (value) CreateTask();
                else DeleteTask();
                LogBus.Write($"prefs: автозапуск = {value}");
            }
            catch (Exception ex) { LogBus.Write("prefs: автозапуск FAIL " + ex.Message); }
        }
    }

    // HKCU\Run не стартует exe с requireAdministrator. Задача ONLOGON /RL HIGHEST
    // поднимает уже с правами, без UAC на каждом входе в Windows.
    public static void MigrateRunKeyToTask()
    {
        if (RunValue() == null) return;
        try
        {
            ClearRunKey();
            CreateTask();
            LogBus.Write("prefs: автозапуск перенесён в задачу (права админа)");
        }
        catch (Exception ex) { LogBus.Write("prefs: миграция автозапуска FAIL " + ex.Message); }
    }

    // "full" | "split"
    public static string LastMode
    {
        get => Cfg.IniGet("LastMode") ?? "full";
        set => Cfg.IniSet("LastMode", value);
    }

    // "pink" | "anon"
    public static string Theme
    {
        get => Cfg.IniGet("Theme") ?? "pink";
        set => Cfg.IniSet("Theme", value);
    }

    private static string? RunValue()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) as string;
        }
        catch { return null; }
    }

    private static void ClearRunKey()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue(RunName, false);
        }
        catch { }
    }

    private static bool TaskExists() => Schtasks("/Query", "/TN", TaskName) == 0;

    private static void CreateTask()
    {
        int code = Schtasks(
            "/Create", "/TN", TaskName,
            "/TR", $"\"{Cfg.ExePath}\" --autostart",
            "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
        if (code != 0) throw new InvalidOperationException($"schtasks create {code}");
    }

    private static void DeleteTask() => Schtasks("/Delete", "/TN", TaskName, "/F");

    private static int Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return -1;
        p.WaitForExit(8000);
        return p.HasExited ? p.ExitCode : -1;
    }
}
