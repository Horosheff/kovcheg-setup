using System.Diagnostics;
using System.IO;
using System.Management;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace KovchegVPN;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private WinForms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(Cfg.Dir);
        // Старый UI мог остаться в трее (крестик только прятал окно) и держать
        // мьютекс — тогда новый exe молча выходил. Гасим его сами.
        KillOtherUi();
        Thread.Sleep(500);

        _mutex = new Mutex(true, "KovchegVPN.GUI", out bool created);
        if (!created)
        {
            Native.ForegroundExisting();
            Shutdown();
            return;
        }

        CleanupOldFiles();
        Cfg.ExtractAssets();
        ElevatedWorker.EmergencyRecoverDns();

        // Скачанный exe сам ставится в AppData и закрывается — без «найди Выход в трее».
        if (!SelfInstall.IsInstalled())
        {
            string? err = SelfInstall.Install();
            if (err == null)
            {
                Shutdown();
                return;
            }
            LogBus.Write("install: автоустановка не вышла — " + err);
        }

        Prefs.MigrateRunKeyToTask();

        var w = new MainWindow();
        MainWindow = w;
        SetupTray(w);
        w.Show();
    }

    // Любой KovchegVPN.exe без --elevated, кроме нас. WMI — MainModule часто
    // недоступен, и тогда старый UI в трее переживал обновление.
    public static void KillOtherUi()
    {
        int me = Environment.ProcessId;
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'KovchegVPN.exe'");
            foreach (ManagementObject mo in q.Get())
            {
                int pid = Convert.ToInt32(mo["ProcessId"]);
                string cmd = mo["CommandLine"] as string ?? "";
                if (pid == me) continue;
                if (cmd.Contains("--elevated", StringComparison.OrdinalIgnoreCase)) continue;
                KillPid(pid);
            }
        }
        catch (Exception ex)
        {
            LogBus.Write("kill-ui WMI: " + ex.Message);
            foreach (var p in Process.GetProcessesByName("KovchegVPN"))
            {
                try
                {
                    if (p.Id == me) continue;
                    KillPid(p.Id);
                }
                catch { }
            }
        }
    }

    private static void KillPid(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            LogBus.Write($"kill-ui: pid {pid}");
            p.Kill();
            p.WaitForExit(5000);
        }
        catch { }
    }

    private static void CleanupOldFiles()
    {
        try
        {
            foreach (string f in Directory.GetFiles(Cfg.Dir, "KovchegVPN.old.*.exe"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private void SetupTray(MainWindow w)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Renderer = new DarkMenuRenderer();
        menu.BackColor = System.Drawing.Color.FromArgb(0x0D, 0x11, 0x17);
        menu.ForeColor = System.Drawing.Color.FromArgb(0xE5, 0xE7, 0xEB);
        menu.Items.Add("Открыть", null, (_, _) => w.ShowFromTray());
        menu.Items.Add("Включить / выключить", null, async (_, _) => await w.UiPower());
        menu.Items.Add("Обновить", null, async (_, _) => await w.UiUpdateQueued());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        var autoStart = new WinForms.ToolStripMenuItem("Автозапуск с Windows") { CheckOnClick = true, Checked = Prefs.AutoStart };
        autoStart.CheckedChanged += (_, _) => Prefs.AutoStart = autoStart.Checked;
        menu.Items.Add(autoStart);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Показать лог", null, (_, _) => { w.ShowFromTray(); w.ToggleLog(); });
        menu.Items.Add("Отправить лог разработчику", null, async (_, _) => await w.UiSendLog());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => w.ForceClose());

        _trayIcon = new System.Drawing.Icon(Cfg.IcoOffPath);
        _tray = new WinForms.NotifyIcon
        {
            Text = "KovchegVPN",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = _trayIcon
        };
        _tray.DoubleClick += (_, _) => w.ShowFromTray();
    }

    public void ShowBalloon(string title, string text)
    {
        try { _tray?.ShowBalloonTip(6000, title, text, WinForms.ToolTipIcon.None); } catch { }
    }

    public void SetTray(bool on)
    {
        if (_tray == null) return;
        try
        {
            var fresh = new System.Drawing.Icon(on ? Cfg.IcoOnPath : Cfg.IcoOffPath);
            _tray.Icon = fresh;
            _trayIcon?.Dispose();
            _trayIcon = fresh;
            _tray.Text = on ? $"KovchegVPN — {Cfg.ExitCountry}" : "KovchegVPN — выкл";
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Если сплит на нашем ядре — прокси уже снят в ForceClose; ядро гасим здесь.
        if (!SysProxy.IsOurProxy()) Core.StopOwn();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }
}
