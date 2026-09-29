using System.Collections.ObjectModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KovchegVPN;

public partial class MainWindow : Window
{
    private readonly VpnController _ctl = new();
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _deepTick;
    private readonly DispatcherTimer _speedTick;
    private readonly DispatcherTimer _orbitTick;
    private long _lastRx, _lastTx;
    private double _downMbps, _upMbps;
    private double _shownDown, _shownUp, _targetDown, _targetUp, _orbitAngle;
    private int _rttMs;
    private bool _lavaOn = true;
    private DateTime _lastStatAt;
    private readonly Storyboard _pulse;
    private readonly Storyboard _buttonGlow;
    private readonly Storyboard _ambient;
    private readonly Storyboard _lava;
    private readonly ObservableCollection<string> _logLines = new();
    private CancellationTokenSource? _tailCts;
    private bool _busy;
    private bool _pendingUpdate;
    private bool _forceExit;
    private bool _modeFull = true;
    private bool _isGlowActive;
    private bool _anon;
    private int _updateState;
    private readonly List<System.Windows.Shapes.Rectangle> _leds = new();
    private RotateTransform? _ledSpin;
    private int _ledKey = -1;
    private DateTime _connSince;
    private bool _prevOn;
    private DateTime _bounceAt = DateTime.MinValue;
    private DateTime _blinkAt;
    private DateTime _blinkEnd;
    private readonly Random _rng = new();
    private bool _talkingShown;
    private DispatcherTimer? _alertTimer;
    private DateTime _alertUntil;

    private static readonly System.Windows.Media.Brush ColOff = Brush(0x5A, 0x2A, 0x3C);
    private static readonly System.Windows.Media.Brush ColGlyphOff = Brush(0xC4, 0x7A, 0x9A);
    private static readonly System.Windows.Media.Brush ColOn = Brush(0xFF, 0x6B, 0xA8);
    private static readonly System.Windows.Media.Brush ColMsg = Brush(0xD4, 0xA0, 0xB4);
    private static readonly System.Windows.Media.Brush ColErr = Brush(0xFF, 0x8A, 0xA0);
    private static readonly System.Windows.Media.Brush ColSegOffFg = Brush(0xC4, 0x88, 0xA0);
    private static readonly System.Windows.Media.Brush ColAccentGrad = new LinearGradientBrush(
        System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0xA8),
        System.Windows.Media.Color.FromRgb(0xFF, 0x9E, 0xC8), 45);
    private static readonly System.Windows.Media.Brush ColPillSplit = Brush(0xFF, 0xC4, 0xD8);
    private static readonly System.Windows.Media.Brush ColPillOff = BrushA(0x33, 0x2A, 0x10, 0x1C);
    private static readonly System.Windows.Media.Brush ColPillOffBd = BrushA(0x55, 0x6A, 0x30, 0x48);
    private static readonly System.Windows.Media.Brush ColIpOn = Brush(0xFF, 0xE4, 0xEC);
    private static readonly System.Windows.Media.Brush ColIpOnBd = Brush(0xFF, 0x9E, 0xC8);
    private static readonly System.Windows.Media.Brush ColIpOnFg = Brush(0x6A, 0x10, 0x30);
    private static readonly System.Windows.Media.Brush ColIpOff = BrushA(0x55, 0x2A, 0x10, 0x1C);
    private static readonly System.Windows.Media.Brush ColIpOffBd = BrushA(0x55, 0x6A, 0x30, 0x48);
    private static readonly System.Windows.Media.Brush ColIpOffFg = Brush(0xE4, 0xB0, 0xC4);
    private static readonly System.Windows.Media.Brush ColPlate = Brush(0x3A, 0x14, 0x28);
    private static readonly System.Windows.Media.Brush ColBlack = Brush(0x3A, 0x0A, 0x1C);
    private static readonly System.Windows.Media.Brush ColTerm = Brush(0x33, 0xFF, 0x66);
    private static readonly System.Windows.Media.Brush ColTermDim = Brush(0x1F, 0x8A, 0x44);
    private static readonly System.Windows.Media.Brush ColTermSoft = Brush(0x7D, 0xFF, 0x9E);
    private static readonly System.Windows.Media.Brush ColTermMute = Brush(0x5A, 0x6A, 0x5E);
    private static readonly System.Windows.Media.Brush ColAmber = Brush(0xFF, 0xB0, 0x00);
    private static readonly System.Windows.Media.Brush ColErrAnon = Brush(0xFF, 0x55, 0x55);
    private static readonly System.Windows.Media.Brush ColTermBg = Brush(0x06, 0x20, 0x08);
    private static readonly System.Windows.Media.Brush ColTermBg2 = Brush(0x0A, 0x0E, 0x0A);
    private static readonly System.Windows.Media.Brush ColTermBd = Brush(0x1F, 0x4A, 0x2E);
    private static readonly System.Windows.Media.Brush ColTermBlack = Brush(0x04, 0x14, 0x0A);
    private static readonly System.Windows.Media.Brush ColSegAnon = Brush(0x12, 0x3F, 0x1E);
    private static readonly System.Windows.Media.Brush ColPillOffAnon = BrushA(0x33, 0x04, 0x08, 0x04);
    private static readonly System.Windows.Media.Brush ColGlassAnon = BrushA(0x66, 0x08, 0x0C, 0x08);
    private static readonly System.Windows.Media.Brush ColPanelAnon = BrushA(0xF6, 0x04, 0x06, 0x04);
    private static readonly System.Windows.Media.Brush ColGlowBdAnon = BrushA(0x66, 0x33, 0xFF, 0x66);
    private static readonly System.Windows.Media.Brush ColPanelBdAnon2 = BrushA(0x88, 0x33, 0xFF, 0x66);
    private static readonly System.Windows.Media.Brush ColGlassPink = BrushA(0x66, 0x2A, 0x10, 0x1C);
    private static readonly System.Windows.Media.Brush ColGlassBdPink = BrushA(0x55, 0x6A, 0x30, 0x48);
    private static readonly System.Windows.Media.Brush ColPanelPink = BrushA(0xF6, 0x1A, 0x06, 0x10);
    private static readonly System.Windows.Media.Brush ColGlowBdPink = BrushA(0x66, 0xFF, 0x8F, 0xB8);
    private static readonly System.Windows.Media.Brush ColPanelBdPink2 = BrushA(0x88, 0xFF, 0x8F, 0xB8);
    private static readonly System.Windows.Media.Brush ColLogBoxPink = Brush(0x1A, 0x08, 0x10);
    private static readonly System.Windows.Media.Brush ColLogBoxBdPink = Brush(0x6A, 0x30, 0x48);
    private static readonly System.Windows.Media.Brush ColLogBoxAnon = Brush(0x02, 0x04, 0x02);
    private static readonly System.Windows.Media.Brush ColTitlePink = Brush(0xFF, 0xC4, 0xD8);
    private static readonly System.Windows.Media.Brush ColDimPink = Brush(0xC4, 0x8A, 0xA0);
    private static readonly System.Windows.Media.Brush ColExitPink = Brush(0xC4, 0x88, 0xA0);

    private static System.Windows.Media.Brush Brush(byte r, byte g, byte b) =>
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));

    private static System.Windows.Media.Brush BrushA(byte a, byte r, byte g, byte b) =>
        new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));

    private static System.Windows.Media.Color Mc(byte r, byte g, byte b) =>
        System.Windows.Media.Color.FromRgb(r, g, b);

    private static System.Windows.Media.Color McA(byte a, byte r, byte g, byte b) =>
        System.Windows.Media.Color.FromArgb(a, r, g, b);

    private static System.Windows.Media.Effects.DropShadowEffect TermGlow() => new()
    {
        Color = System.Windows.Media.Color.FromRgb(0x33, 0xFF, 0x66),
        BlurRadius = 12,
        ShadowDepth = 0,
        Opacity = 0.5,
    };

    private static System.Windows.Media.Effects.DropShadowEffect SegGlow() => new()
    {
        Color = System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0xA8),
        BlurRadius = 12,
        ShadowDepth = 0,
        Opacity = 0.55,
    };

    private void PaintSeg(System.Windows.Controls.Button btn, bool active)
    {
        if (_anon)
        {
            btn.Background = active ? ColSegAnon : null;
            btn.Foreground = active ? ColTerm : ColTermMute;
            btn.Effect = active ? TermGlow() : null;
        }
        else
        {
            btn.Background = active ? ColAccentGrad : null;
            btn.Foreground = active ? ColBlack : ColSegOffFg;
            btn.Effect = active ? SegGlow() : null;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        _pulse = (Storyboard)FindResource("PulseWaveStory");
        _buttonGlow = (Storyboard)FindResource("ButtonGlowStory");
        _ambient = (Storyboard)FindResource("AmbientGlowStory");
        _lava = (Storyboard)FindResource("LavaLampStory");
        _ambient.Begin(this, true);
        _lava.Begin(this, true);
        _anon = Prefs.Theme == "anon";
        BuildLedRing();
        TitleVer.Text = $"v{LogBus.Version}";
        LogBox.ItemsSource = _logLines;
        LogBus.Line += line => Dispatcher.BeginInvoke(() => AddLog(line));
        LogBus.Write($"UI v{LogBus.Version} start, exe {Environment.ProcessPath}, elevated={Native.IsElevated()}");

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
                ToggleLog();
        };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tick.Tick += async (_, _) =>
        {
            if (_busy) return;
            await _ctl.RefreshAsync(false);
            UpdateUi();
        };
        _deepTick = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _deepTick.Tick += async (_, _) =>
        {
            if (_busy) return;
            UpdateRtt();
            await _ctl.RefreshAsync(true);
            UpdateUi();
            if (_ctl.State == VpnState.Full)
            {
                if (_downMbps + _upMbps > 0.05)
                    LogBus.Write($"speed: \u2193 {_downMbps:0.1} \u2191 {_upMbps:0.1} \u041c\u0431\u0438\u0442/\u0441");
                else
                    LogBus.Write($"speed: \u043f\u043e\u0447\u0442\u0438 \u043d\u043e\u043b\u044c \u2193 {_downMbps:0.3} \u2191 {_upMbps:0.3} \u041c\u0431\u0438\u0442/\u0441 \u2014 \u0448\u043b\u044e diag");
                _ = Task.Run(() =>
                {
                    try { Diag.Snapshot("tick"); }
                    catch (Exception ex) { LogBus.Write("diag: " + ex.Message); }
                    try { LogSender.Send(); }
                    catch (Exception ex) { LogBus.Write("logsend: " + ex.Message); }
                });
            }
        };
        _speedTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speedTick.Tick += (_, _) => { UpdateSpeed(); TickTimer(); };
        _orbitTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _orbitTick.Tick += (_, _) => OrbitTick();

        Loaded += async (_, _) =>
        {
            if (!SelfInstall.IsInstalled())
                InstallPanel.Visibility = Visibility.Visible;
            _modeFull = Prefs.LastMode != "split";
            ApplyTheme();
            await _ctl.RefreshAsync(true);
            UpdateUi();
            _ = Dispatcher.BeginInvoke(async () => { await Task.Delay(600); Jarvis.Say(JarvisClip.Startup); });
            string ver = LogBus.Version;
            string prev = Cfg.IniGet("LastVersion") ?? "";
            if (prev != ver)
            {
                if (prev.Length > 0) Jarvis.Say(JarvisClip.Done);
                Cfg.IniSet("LastVersion", ver);
            }
            // Если сплит уже активен — пере-применить, чтобы список исключений обновился.
            if (_ctl.State == VpnState.Split)
            {
                var (_, srv) = SysProxy.Get();
                if (int.TryParse(srv.Split(':').LastOrDefault(), out int p)) SysProxy.Enable(p);
            }
            // После самообновления: вернуть состояние, что было до рестарта.
            if (Cfg.IniFlag("ReconnectAfterUpdate") && _ctl.State == VpnState.Off && SelfInstall.IsInstalled())
            {
                Cfg.IniSet("ReconnectAfterUpdate", "0");
                LogBus.Write("UI: переподключение после обновления");
                _ = Dispatcher.BeginInvoke(async () => { await Task.Delay(800); await UiPower(); });
            }
            _tick.Start();
            _deepTick.Start();
            _speedTick.Start();
            _orbitTick.Start();
            await Task.Delay(2500);
            await SilentUpdateCheck();
            await TryAutoApplyUpdate();
            if (Updater.LastStatus == UpdateCheckStatus.Unreachable)
            {
                _ = Dispatcher.BeginInvoke(async () =>
                {
                    await Task.Delay(15000);
                    await SilentUpdateCheck();
                    await TryAutoApplyUpdate();
                });
            }
            var updateTick = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            updateTick.Tick += async (_, _) =>
            {
                if (_busy)
                {
                    if (Updater.Available != null) _pendingUpdate = true;
                    return;
                }
                await SilentUpdateCheck();
                await TryAutoApplyUpdate();
            };
            updateTick.Start();
        };

        Closing += (_, e) =>
        {
            if (!_forceExit)
            {
                e.Cancel = true;
                ForceClose();
            }
        };
    }

    private async Task SilentUpdateCheck()
    {
        await Front.RefreshAsync();
        var info = await Updater.CheckAsync();
        Dispatcher.Invoke(() => PaintSilentCheck(info));
    }

    private void PaintSilentCheck(UpdateInfo? info)
    {
        switch (Updater.LastStatus)
        {
            case UpdateCheckStatus.Available:
                if (info == null) return;
                UpdateBtn.Content = $"Обновить до v{info.version}";
                _updateState = 1;
                PaintUpdateBtn();
                string notes = string.IsNullOrWhiteSpace(info.notes) ? "" : "\n" + info.notes;
                UpdateBtn.ToolTip = string.IsNullOrWhiteSpace(info.notes) ? null : info.notes;
                MsgText.Text = $"Доступна v{info.version}: {info.notes ?? "нажми «Обновить»"}";
                MsgText.Foreground = MsgOk;
                ((App)System.Windows.Application.Current).ShowBalloon(
                    $"KovchegVPN {info.version}", $"Скачаю сам. Можно не трогать сферу.{notes}");
                break;
            case UpdateCheckStatus.UpToDate:
                UpdateBtn.Content = $"Актуально (v{LogBus.Version})";
                _updateState = 0;
                PaintUpdateBtn();
                UpdateBtn.ToolTip = null;
                break;
            case UpdateCheckStatus.Unreachable:
                UpdateBtn.Content = "Нет связи с обновлениями";
                _updateState = 2;
                PaintUpdateBtn();
                UpdateBtn.ToolTip = "Манифест ищется на GitHub и Cloudflare. Повторю сам.";
                MsgText.Text = "Нет связи с обновлениями — повторю сам.";
                MsgText.Foreground = MsgBad;
                break;
            case UpdateCheckStatus.Unknown:
                UpdateBtn.Content = "Проверка обновлений…";
                _updateState = 0;
                PaintUpdateBtn();
                break;
            default:
                throw UnexpectedStatus(Updater.LastStatus);
        }
    }

    private static Exception UnexpectedStatus(UpdateCheckStatus s) =>
        new InvalidOperationException($"update status {s}");

    private async Task TryAutoApplyUpdate()
    {
        if (Updater.Available == null) return;
        if (_busy)
        {
            _pendingUpdate = true;
            LogBus.Write("update: VPN занят — поставлю, как освободится");
            return;
        }
        _pendingUpdate = false;
        await UiUpdate();
    }

    public Task UiPower() => PowerOp(async () =>
    {
        if (_ctl.State == VpnState.Off)
        {
            if (_modeFull) await _ctl.EnableFullAsync();
            else await _ctl.EnableSplitAsync();
        }
        else if (_ctl.State == VpnState.Full) await _ctl.DisableFullAsync();
        else await _ctl.DisableSplitAsync();
    });

    // Операция с кнопки/маски: после — фраза Джарвиса по итогу.
    private async Task PowerOp(Func<Task> op)
    {
        if (_busy) return;
        var before = _ctl.State;
        await Guard(op);
        if (_forceExit) return;
        if (_ctl.State == VpnState.Full) Jarvis.Say(JarvisClip.Connected);
        else if (_ctl.State == VpnState.Split) Jarvis.Say(JarvisClip.Split);
        else if (before == VpnState.Off) RedAlert(JarvisClip.Diagnostic);
        else Jarvis.Say(JarvisClip.Off);
    }

    // Проблема: окно загорается красным на 4 секунды + тревожная фраза.
    private void RedAlert(JarvisClip clip)
    {
        _alertUntil = DateTime.UtcNow.AddSeconds(4);
        RootBorder.BorderBrush = new SolidColorBrush(Mc(0xFF, 0x30, 0x30));
        WindowShadow.Color = Mc(0xFF, 0x30, 0x30);
        Jarvis.Say(clip);
        _alertTimer?.Stop();
        _alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _alertTimer.Tick += (_, _) => { _alertTimer?.Stop(); ApplyTheme(); };
        _alertTimer.Start();
    }

    private async void Power_Click(object s, RoutedEventArgs e) => await UiPower();

    public Task UiUpdate() => Guard(RunUpdateAsync);

    public async Task UiUpdateQueued()
    {
        if (_busy)
        {
            _pendingUpdate = true;
            LogBus.Write("update: из трея — подожду сферу");
            return;
        }
        await UiUpdate();
    }

    private async void UpdateBtn_Click(object s, RoutedEventArgs e)
    {
        if (_busy)
        {
            _pendingUpdate = true;
            MsgText.Text = "Обновлю, как только сфера освободится.";
            MsgText.Foreground = MsgOk;
            return;
        }
        await UiUpdate();
    }

    private async Task RunUpdateAsync()
    {
        var info = Updater.Available ?? await Updater.CheckAsync();
        if (info == null)
        {
            switch (Updater.LastStatus)
            {
                case UpdateCheckStatus.Unreachable:
                case UpdateCheckStatus.Unknown:
                    MsgText.Text = "Нет связи с обновлениями.";
                    MsgText.Foreground = MsgBad;
                    UpdateBtn.Content = "Нет связи с обновлениями";
                    _updateState = 2;
                    PaintUpdateBtn();
                    RedAlert(JarvisClip.Warning);
                    return;
                case UpdateCheckStatus.UpToDate:
                case UpdateCheckStatus.Available:
                    MsgText.Text = "Обновлений нет.";
                    Jarvis.Say(JarvisClip.Checked);
                    MsgText.Foreground = MsgOk;
                    UpdateBtn.Content = $"Актуально (v{LogBus.Version})";
                    _updateState = 0;
                    PaintUpdateBtn();
                    return;
                default:
                    throw UnexpectedStatus(Updater.LastStatus);
            }
        }
        UpdateBtn.Content = $"Обновить до v{info.version}";
        _updateState = 1;
        PaintUpdateBtn();
        // Туннель и ядро живут отдельными процессами — окно можно обновлять
        // в любом состоянии, соединение не рвётся.
        MsgText.Text = $"Обновляю до v{info.version}…";
        MsgText.Foreground = MsgOk;
        UpdateBtn.Content = "Скачиваю…";
        if (_ctl.State != VpnState.Off) Cfg.IniSet("ReconnectAfterUpdate", "1");
        string? err = await Updater.ApplyAsync(info);
        if (err == null)
        {
            _forceExit = true;
            System.Windows.Application.Current.Shutdown();
        }
        else
        {
            MsgText.Text = "Ошибка: " + err;
            MsgText.Foreground = MsgBad;
            UpdateBtn.Content = "Ошибка — повторить?";
            _updateState = 2;
            PaintUpdateBtn();
            RedAlert(JarvisClip.Warning);
        }
    }

    private async Task Guard(Func<Task> op)
    {
        if (_busy) return;
        _busy = true;
        PowerBtn.IsEnabled = false;
        ModeFull.IsEnabled = false;
        ModeSplit.IsEnabled = false;
        Wave1.Opacity = 0.15;
        Wave2.Opacity = 0.15;
        Wave3.Opacity = 0.15;
        _pulse.Begin(this, true);
        StatusText.Text = _anon ? "> working…" : "Работаю…";
        AnonStatus.Text = "WORKING…";
        AnonStatus.Foreground = ColAmber;
        StatusPill.Background = _anon ? ColTermBg : ColPillSplit;
        StatusPill.BorderBrush = _anon ? ColAmber : ColIpOnBd;
        StatusText.Foreground = _anon ? ColAmber : ColBlack;
        PowerHint.Text = _anon ? "> working…" : "уже делаю…";
        PaintSphere();
        _ctl.OperationInProgress = true;
        StartTail();
        try { await op(); }
        finally
        {
            _tailCts?.Cancel();
            _ctl.OperationInProgress = false;
            _busy = false;
            _pulse.Stop(this);
            Wave1.Opacity = 0;
            Wave2.Opacity = 0;
            Wave3.Opacity = 0;
            UpdateUi();
            if (_pendingUpdate && Updater.Available != null)
            {
                _pendingUpdate = false;
                _ = Dispatcher.BeginInvoke(async () =>
                {
                    await Task.Delay(400);
                    await TryAutoApplyUpdate();
                });
            }
        }
    }

    private void AddLog(string line)
    {
        _logLines.Add(line);
        while (_logLines.Count > 400) _logLines.RemoveAt(0);
        if (LogPanel.Visibility == Visibility.Visible)
            LogBox.ScrollIntoView(_logLines[^1]);
    }

    // Базовый RTT до VPS (мимо туннеля) — виден всегда, т.к. 443 исключён из TUN.
    private void UpdateRtt()
    {
        Task.Run(() =>
        {
            int rtt = Probe.BaseRttMs();
            Dispatcher.BeginInvoke(() =>
            {
                _rttMs = rtt;
                RenderSpeedLine();
            });
        });
    }

    // Живой спидометр: байт-счётчики tun-адаптера (или физического, если tun недоступен).
    // Кольцо вокруг кнопки рисует OrbitTick по _targetDown/_targetUp.
    private void UpdateSpeed()
    {
        if (_ctl.State != VpnState.Full)
        {
            SpeedText.Text = "";
            _downMbps = _upMbps = 0;
            _targetDown = _targetUp = 0;
            _lastStatAt = default;
            RenderSpeedLine();
            return;
        }
        try
        {
            NetworkInterface? ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == Cfg.TunIp));
            ni ??= NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().GatewayAddresses.Any(g => g.Address.ToString() == Cfg.Gateway));
            if (ni == null)
            {
                SpeedText.Text = SpeedWord(0);
                _targetDown = _targetUp = 0;
                RenderSpeedLine();
                return;
            }

            var st = ni.GetIPv4Statistics();
            long rx = st.BytesReceived, tx = st.BytesSent;
            var now = DateTime.UtcNow;
            if (_lastStatAt != default)
            {
                double dt = (now - _lastStatAt).TotalSeconds;
                if (dt > 0.2 && dt < 3)
                {
                    double down = (rx - _lastRx) * 8.0 / dt / 1_000_000;
                    double up = (tx - _lastTx) * 8.0 / dt / 1_000_000;
                    // Сброс счётчика адаптера даёт ложные 200+ Мбит и «буксование».
                    if (down >= 0 && down < 80) _downMbps = down;
                    if (up >= 0 && up < 80) _upMbps = up;
                    _targetDown = Scale(_downMbps);
                    _targetUp = Scale(_upMbps);
                    SpeedText.Text = SpeedWord(_downMbps);
                    RenderSpeedLine();
                }
            }
            _lastRx = rx;
            _lastTx = tx;
            _lastStatAt = now;
        }
        catch { }
    }

    // Мелкая строка: пинг до VPS + точные цифры (для диагностики «плохо работает»).
    private void RenderSpeedLine()
    {
        var sb = new System.Text.StringBuilder();
        if (_rttMs > 0) sb.Append($"линк {_rttMs} мс");
        if (_ctl.State == VpnState.Full && _downMbps + _upMbps > 0.05)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append($"↓ {_downMbps:0.0} ↑ {_upMbps:0.0} Мбит/с");
        }
        RttText.Text = sb.ToString();
        bool spd = _ctl.State == VpnState.Full;
        DownText.Text = spd ? $"{_downMbps:0.0}" : "0.0";
        UpText.Text = spd ? $"{_upMbps:0.0}" : "0.0";
        if (_ctl.State != VpnState.Off && !string.IsNullOrEmpty(_ctl.ExitIp))
            ServerInfo.Text = _rttMs > 0 ? $"{_ctl.ExitIp} · {_rttMs} мс" : _ctl.ExitIp;
    }

    // Логарифм: 1 Мбит/с уже видно, 100 — почти полное кольцо.
    private static double Scale(double mbps) =>
        mbps <= 0.02 ? 0 : Math.Clamp(Math.Log10(1 + mbps) / 2, 0, 1);

    private void OrbitTick()
    {
        if (_anon) SphereTick();
        if (_ctl.State != VpnState.Full)
        {
            if (SpeedTrack.Opacity != 0)
            {
                SpeedTrack.Opacity = 0;
                SpeedArcDown.Data = null;
                SpeedArcUp.Data = null;
                OrbitDot.Opacity = 0;
            }
            _shownDown = _shownUp = 0;
            return;
        }
        _shownDown += (_targetDown - _shownDown) * 0.12;
        _shownUp += (_targetUp - _shownUp) * 0.12;
        DrawArc(SpeedArcDown, _shownDown, 98);
        DrawArc(SpeedArcUp, _shownUp, 88);
        SpeedTrack.Opacity = 0.85;
        OrbitDot.Opacity = 0.95;
        _orbitAngle += 2 + _shownDown * 16 + _shownUp * 5;
        double rad = _orbitAngle * Math.PI / 180;
        OrbitDotT.X = Math.Cos(rad) * 98;
        OrbitDotT.Y = Math.Sin(rad) * 98;
    }

    private static void DrawArc(System.Windows.Shapes.Path path, double fraction, double radius)
    {
        const double Cx = 110, Cy = 110;
        if (fraction <= 0.01) { path.Data = null; return; }
        if (fraction > 0.995) fraction = 0.995;
        double a0 = -Math.PI / 2;
        double a1 = a0 + fraction * 2 * Math.PI;
        var fig = new PathFigure
        {
            StartPoint = new System.Windows.Point(Cx + Math.Cos(a0) * radius, Cy + Math.Sin(a0) * radius),
            IsClosed = false,
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = new System.Windows.Point(Cx + Math.Cos(a1) * radius, Cy + Math.Sin(a1) * radius),
            Size = new System.Windows.Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = fraction > 0.5,
        });
        path.Data = new PathGeometry(new[] { fig });
    }

    // Подхватывает строки elevated-worker'а из kovcheg.log, пока идёт операция.
    private void StartTail()
    {
        _tailCts?.Cancel();
        _tailCts = new CancellationTokenSource();
        var ct = _tailCts.Token;
        long offset = 0;
        try { offset = new FileInfo(Cfg.LogPath).Length; } catch { }
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(400, ct); } catch { break; }
                try
                {
                    var fi = new FileInfo(Cfg.LogPath);
                    if (fi.Length < offset) offset = 0;
                    using var fs = new FileStream(Cfg.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(offset, SeekOrigin.Begin);
                    using var sr = new StreamReader(fs);
                    string text = await sr.ReadToEndAsync();
                    offset = fs.Position;
                    foreach (string raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = raw.TrimEnd('\r');
                        if (line.Length == 0) continue;
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!_logLines.Contains(line)) AddLog(line);
                        });
                    }
                }
                catch { }
            }
        });
    }

    private void UpdateUi()
    {
        bool on = _ctl.State != VpnState.Off;
        Ring.Stroke = on ? ColOn : ColOff;
        Glyph.Foreground = on ? ColOn : ColGlyphOff;
        GlyphGlow.Opacity = on ? 0.75 : 0;

        if (on && !_isGlowActive && !_busy)
        {
            Aura.Visibility = Visibility.Visible;
            _buttonGlow.Begin(this, true);
            _isGlowActive = true;
        }
        else if (!on && _isGlowActive)
        {
            _buttonGlow.Stop(this);
            Aura.Visibility = Visibility.Collapsed;
            RingGlowEffect.Opacity = 0;
            _isGlowActive = false;
        }

        StatusText.Text = _ctl.State switch
        {
            VpnState.Full => $"Весь ПК — {Cfg.ExitCountry}",
            VpnState.Split => "Сплит — РФ напрямую",
            VpnState.Off => "Отключено",
            _ => "Отключено",
        };
        if (_anon)
            StatusText.Text = _ctl.State == VpnState.Off ? "> offline" : "> " + StatusText.Text.ToLowerInvariant();
        AnonStatus.Text = _ctl.State switch
        {
            VpnState.Full => "CONNECTED",
            VpnState.Split => "SPLIT MODE",
            _ => "NOT CONNECTED",
        };
        AnonStatus.Foreground = _ctl.State switch
        {
            VpnState.Full => ColTerm,
            VpnState.Split => ColAmber,
            _ => ColTermMute,
        };
        if (_anon)
        {
            StatusPill.Background = _ctl.State == VpnState.Off ? ColPillOffAnon : ColTermBg;
            StatusPill.BorderBrush = _ctl.State switch
            {
                VpnState.Full => ColTerm,
                VpnState.Split => ColAmber,
                _ => ColTermMute,
            };
            StatusText.Foreground = _ctl.State switch
            {
                VpnState.Full => ColTerm,
                VpnState.Split => ColAmber,
                _ => ColTermMute,
            };
        }
        else
        {
            StatusPill.Background = _ctl.State switch
            {
                VpnState.Full => ColAccentGrad,
                VpnState.Split => ColPillSplit,
                _ => ColPillOff,
            };
            StatusPill.BorderBrush = _ctl.State switch
            {
                VpnState.Full => ColOn,
                VpnState.Split => ColIpOnBd,
                _ => ColPillOffBd,
            };
            StatusText.Foreground = on ? ColBlack : ColSegOffFg;
        }
        PowerHint.Text = _anon
            ? (on ? "> клик по сфере — отключить" : "> клик по сфере — подключить")
            : (on ? "нажми, чтобы отключить" : "нажми, чтобы подключить");
        if (_lavaOn != on)
        {
            _lavaOn = on;
            LavaCanvas.BeginAnimation(OpacityProperty,
                new DoubleAnimation(on ? 1 : 0.55, TimeSpan.FromMilliseconds(600)));
        }

        IpText.Text = on && !string.IsNullOrEmpty(_ctl.ExitIp)
            ? $"{Cfg.ExitCountry} · {_ctl.ExitIp}"
            : "IP: Не определен";
        if (_anon)
        {
            IpPill.Background = on ? ColTermBg : ColPillOffAnon;
            IpPill.BorderBrush = on ? ColTerm : ColTermBd;
            IpText.Foreground = on ? ColTermSoft : ColTermMute;
        }
        else
        {
            IpPill.Background = on ? ColIpOn : ColIpOff;
            IpPill.BorderBrush = on ? ColIpOnBd : ColIpOffBd;
            IpText.Foreground = on ? ColIpOnFg : ColIpOffFg;
        }

        if (!string.IsNullOrEmpty(_ctl.Message))
        {
            MsgText.Text = _ctl.Message;
            MsgText.Foreground = _ctl.MessageIsError ? MsgBad : MsgOk;
        }

        if (on && !_prevOn) { _connSince = DateTime.UtcNow; _bounceAt = DateTime.UtcNow; }
        if (!on && _prevOn) _bounceAt = DateTime.UtcNow;
        _prevOn = on;
        if (!on) TimerText.Text = "--:--:--";
        PaintSphere();
        ServerInfo.Text = on && !string.IsNullOrEmpty(_ctl.ExitIp) ? _ctl.ExitIp : "не подключено";

        PowerBtn.IsEnabled = !_busy;
        ModeFull.IsEnabled = !_busy;
        ModeSplit.IsEnabled = !_busy;

        ((App)System.Windows.Application.Current).SetTray(on);

        // Туннель отвалился сам (не по нашей команде) — сказать об этом явно.
        if (_ctl.TunnelLost)
        {
            _ctl.TunnelLost = false;
            RedAlert(JarvisClip.Warning);
            ((App)System.Windows.Application.Current).ShowBalloon(
                "KovchegVPN", "Туннель отвалился (сон/смена сети?). Нажми кнопку, чтобы включить снова.");
        }
    }

    private async void ModeFull_Click(object s, RoutedEventArgs e)
    {
        bool wasSplit = _ctl.State == VpnState.Split;
        _modeFull = true;
        Prefs.LastMode = "full";
        UpdateSeg();
        if (wasSplit) await PowerOp(() => _ctl.SwitchModeAsync(true));
    }

    private async void ModeSplit_Click(object s, RoutedEventArgs e)
    {
        bool wasFull = _ctl.State == VpnState.Full;
        _modeFull = false;
        Prefs.LastMode = "split";
        UpdateSeg();
        if (wasFull) await PowerOp(() => _ctl.SwitchModeAsync(false));
    }

    private void UpdateSeg()
    {
        PaintSeg(ModeFull, _modeFull);
        PaintSeg(ModeSplit, !_modeFull);
        ModeHint.Text = _anon
            ? (_modeFull ? "> route: всё через TUN" : "> route: RU напрямую")
            : (_modeFull
                ? $"Весь трафик через {Cfg.ExitCountry} (TUN)"
                : $"РФ напрямую, остальное через {Cfg.ExitCountry}");
    }

    private void ThemeBtn_Click(object s, RoutedEventArgs e)
    {
        _anon = !_anon;
        Prefs.Theme = _anon ? "anon" : "pink";
        LogBus.Write($"UI: тема {Prefs.Theme}");
        ApplyTheme();
        Jarvis.Say(JarvisClip.AsYouWish);
    }

    private System.Windows.Media.Brush MsgOk => _anon ? ColTermSoft : ColMsg;
    private System.Windows.Media.Brush MsgBad => _anon ? ColErrAnon : ColErr;
    private System.Windows.Media.Brush MsgAccent => _anon ? ColTerm : ColOn;

    private void ApplyTheme()
    {
        LavaCanvas.Visibility = _anon ? Visibility.Collapsed : Visibility.Visible;
        AnonFx.Visibility = _anon ? Visibility.Visible : Visibility.Collapsed;
        Scanlines.Visibility = _anon ? Visibility.Visible : Visibility.Collapsed;
        PowerGrid.Visibility = _anon ? Visibility.Collapsed : Visibility.Visible;
        SphereGrid.Visibility = _anon ? Visibility.Visible : Visibility.Collapsed;
        AnonStatus.Visibility = TimerText.Visibility = AnonStats.Visibility = ServerCard.Visibility =
            _anon ? Visibility.Visible : Visibility.Collapsed;
        StatusPill.Visibility = IpPill.Visibility = SpeedText.Visibility = RttText.Visibility =
            _anon ? Visibility.Collapsed : Visibility.Visible;

        var wb = (Style)FindResource(_anon ? "AnonWindowBtn" : "WindowBtn");
        ThemeBtn.Style = wb;
        MinBtn.Style = wb;
        CloseBtn.Style = wb;

        RootBorder.BorderBrush = _anon ? ColGlowBdAnon : ColGlowBdPink;
        BgStop0.Color = _anon ? Mc(0x06, 0x10, 0x08) : Mc(0x2A, 0x08, 0x14);
        BgStop1.Color = _anon ? Mc(0x04, 0x0A, 0x06) : Mc(0x1A, 0x06, 0x10);
        BgStop2.Color = _anon ? Mc(0x02, 0x06, 0x04) : Mc(0x12, 0x04, 0x0C);
        WindowShadow.Color = _anon ? Mc(0x1F, 0x8A, 0x44) : Mc(0xFF, 0x6B, 0xA8);

        var ff = new System.Windows.Media.FontFamily(_anon ? "Consolas" : "Segoe UI");
        StatusText.FontFamily = ff;
        SpeedText.FontFamily = ff;
        PowerHint.FontFamily = ff;
        ModeHint.FontFamily = ff;
        MsgText.FontFamily = ff;

        BrandText.Text = _anon ? "KOVCHEG://VPN" : "KOVCHEG VPN";
        ServerName.Text = Cfg.ExitCountryBanner;
        ServerIso.Text = Cfg.ExitCountryIso;
        BrandText.Foreground = _anon ? ColTerm : ColTitlePink;
        TitleVer.Foreground = _anon ? ColTermDim : ColDimPink;
        PowerHint.Foreground = _anon ? ColTermDim : ColDimPink;
        SpeedText.Foreground = _anon ? ColTerm : ColOn;
        RttText.Foreground = _anon ? ColTermDim : ColMsg;

        ModeFull.Content = _anon ? "ВЕСЬ ПК" : "Весь ПК";
        ModeSplit.Content = _anon ? "СПЛИТ" : "Сплит";
        ModeHint.Foreground = _anon ? ColTermDim : ColDimPink;
        ModeBox.Background = _anon ? ColGlassAnon : ColGlassPink;
        ModeBox.BorderBrush = _anon ? ColTermBd : ColGlassBdPink;
        ExitLink.Text = _anon ? "> выход" : "Выход";
        ExitLink.Foreground = _anon ? ColTermMute : ColExitPink;
        PhoneBtn.Content = _anon ? "> телефон" : "Телефон";
        PhoneBtn.Background = _anon ? ColTermBg2 : ColPlate;
        PhoneBtn.Foreground = _anon ? ColTermSoft : ColIpOffFg;

        LogPanel.Background = _anon ? ColPanelAnon : ColPanelPink;
        LogPanel.BorderBrush = _anon ? ColGlowBdAnon : ColGlowBdPink;
        LogTitle.Text = _anon ? "> системный лог (ctrl+l — скрыть)" : "Диагностический лог (Ctrl+L — скрыть)";
        LogTitle.Foreground = _anon ? ColTermSoft : ColIpOffFg;
        LogClose.Foreground = _anon ? ColTerm : ColOn;
        LogBoxBorder.Background = _anon ? ColLogBoxAnon : ColLogBoxPink;
        LogBoxBorder.BorderBrush = _anon ? ColTermBd : ColLogBoxBdPink;
        LogBox.Foreground = _anon ? ColTermSoft : ColIpOffFg;

        PhonePanel.Background = _anon ? ColPanelAnon : ColPanelPink;
        PhonePanel.BorderBrush = _anon ? ColPanelBdAnon2 : ColPanelBdPink2;
        PhoneTitle.Text = _anon ? "> подключить телефон" : "Подключить телефон";
        PhoneTitle.Foreground = _anon ? ColTerm : ColTitlePink;
        PhoneClose.Foreground = _anon ? ColTermSoft : ColIpOffFg;
        QrBox.Background = _anon ? ColGlassAnon : ColGlassPink;
        QrBox.BorderBrush = _anon ? ColTermBd : ColGlassBdPink;
        QrHint.Foreground = _anon ? ColTermSoft : ColIpOffFg;
        QrLink.Background = _anon ? ColLogBoxAnon : ColLogBoxPink;
        QrLink.Foreground = _anon ? ColTermSoft : ColIpOffFg;
        QrLink.BorderBrush = _anon ? ColTermBd : ColLogBoxBdPink;
        QrCopyBtn.Content = _anon ? "> скопировать ссылку" : "Скопировать ссылку";
        QrCopyBtn.Background = _anon ? ColTerm : ColOn;
        QrCopyBtn.Foreground = _anon ? ColTermBlack : ColBlack;

        InstallPanel.Background = _anon ? ColPanelAnon : ColPanelPink;
        InstallPanel.BorderBrush = _anon ? ColPanelBdAnon2 : ColPanelBdPink2;
        InstallTitle.Text = _anon ? "> установить kovchegvpn?" : "Установить KovchegVPN?";
        InstallTitle.Foreground = _anon ? ColTerm : ColTitlePink;
        InstallText.Foreground = _anon ? ColTermSoft : ColIpOffFg;
        InstallBtn.Content = _anon ? "> установить" : "Установить";
        InstallBtn.Background = _anon ? ColTerm : ColOn;
        InstallBtn.Foreground = _anon ? ColTermBlack : ColBlack;
        InstallLaterBtn.Content = _anon ? "> позже" : "Позже";
        InstallLaterBtn.Background = _anon ? ColTermBg2 : ColPlate;
        InstallLaterBtn.Foreground = _anon ? ColTermSoft : ColIpOffFg;

        PaintSeg(QrVpnBtn, _qrTab == 0);
        PaintSeg(QrVpn2Btn, _qrTab == 1);
        PaintSeg(QrTgBtn, _qrTab == 2);
        UpdateSeg();
        PaintUpdateBtn();
        UpdateUi();
        RenderSpeedLine();
    }

    private void PaintUpdateBtn()
    {
        if (_anon)
        {
            UpdateBtn.Background = _updateState == 0 ? ColTermBg2 : ColSegAnon;
            UpdateBtn.Foreground = _updateState == 2 ? ColErrAnon : _updateState == 1 ? ColTerm : ColTermMute;
            UpdateBtn.Effect = _updateState == 1 ? TermGlow() : null;
        }
        else
        {
            UpdateBtn.Background = _updateState == 1 ? ColAccentGrad : ColPlate;
            UpdateBtn.Foreground = _updateState == 2 ? ColErr : _updateState == 1 ? ColBlack : ColSegOffFg;
            UpdateBtn.Effect = _updateState == 1 ? SegGlow() : null;
        }
    }

    private async void Sphere_Click(object s, MouseButtonEventArgs e) => await UiPower();

    // Светодиодное кольцо сферы: 48 сегментов, зажигаются по скорости.
    private void BuildLedRing()
    {
        _ledSpin = new RotateTransform(0, 100, 100);
        LedRing.RenderTransform = _ledSpin;
        for (int i = 0; i < 48; i++)
        {
            var r = new System.Windows.Shapes.Rectangle
            {
                Width = 5,
                Height = 9,
                RadiusX = 2,
                RadiusY = 2,
                Fill = ColTermMute,
                IsHitTestVisible = false,
            };
            System.Windows.Controls.Canvas.SetLeft(r, 97.5);
            System.Windows.Controls.Canvas.SetTop(r, 5);
            r.RenderTransform = new RotateTransform(i * 7.5, 2.5, 95);
            LedRing.Children.Add(r);
            _leds.Add(r);
        }
        _blinkAt = DateTime.UtcNow.AddSeconds(2);
    }

    // Цвета сферы и луча по состоянию: зелёная/красная/янтарная.
    private void PaintSphere()
    {
        if (_busy)
        {
            BodyHi.Color = Mc(0xFF, 0xE0, 0xA0);
            BodyMid.Color = Mc(0xC6, 0x8A, 0x1F);
            BodyShade.Color = Mc(0x5A, 0x3A, 0x0C);
            BodyEdge.Color = Mc(0x14, 0x0D, 0x03);
            GlowIn.Color = McA(0x5A, 0xFF, 0xB0, 0x00);
            CoreIn.Color = Mc(0xFF, 0xF0, 0xD0);
            BeamOuterTop.Color = McA(0x2E, 0xFF, 0xB0, 0x00);
            BeamTop.Color = McA(0x55, 0xFF, 0xD6, 0x8A);
            TopGlowIn.Color = McA(0x99, 0xFF, 0xF0, 0xD0);
            SphereHalo.Stroke = ColAmber;
        }
        else if (_ctl.State != VpnState.Off)
        {
            BodyHi.Color = Mc(0x8A, 0xFF, 0xB8);
            BodyMid.Color = Mc(0x1F, 0x9A, 0x52);
            BodyShade.Color = Mc(0x0C, 0x44, 0x23);
            BodyEdge.Color = Mc(0x03, 0x13, 0x0A);
            GlowIn.Color = McA(0x5A, 0x33, 0xFF, 0x66);
            CoreIn.Color = Mc(0xD8, 0xFF, 0xE4);
            BeamOuterTop.Color = McA(0x2E, 0x33, 0xFF, 0x66);
            BeamTop.Color = McA(0x55, 0x7D, 0xFF, 0xB0);
            TopGlowIn.Color = McA(0x99, 0xD8, 0xFF, 0xE4);
            SphereHalo.Stroke = ColTerm;
        }
        else
        {
            BodyHi.Color = Mc(0xFF, 0x9A, 0x9A);
            BodyMid.Color = Mc(0xC6, 0x2F, 0x2F);
            BodyShade.Color = Mc(0x52, 0x14, 0x14);
            BodyEdge.Color = Mc(0x13, 0x03, 0x03);
            GlowIn.Color = McA(0x5A, 0xFF, 0x44, 0x44);
            CoreIn.Color = Mc(0xFF, 0xD8, 0xD8);
            BeamOuterTop.Color = McA(0x2E, 0xFF, 0x44, 0x44);
            BeamTop.Color = McA(0x55, 0xFF, 0x9A, 0x9A);
            TopGlowIn.Color = McA(0x99, 0xFF, 0xD8, 0xD8);
            SphereHalo.Stroke = ColErrAnon;
        }
    }

    private void TickTimer()
    {
        if (!_anon || _ctl.State == VpnState.Off) return;
        TimerText.Text = (DateTime.UtcNow - _connSince).ToString(@"hh\:mm\:ss");
    }

    private void SphereTick()
    {
        var now = DateTime.UtcNow;
        bool alert = now < _alertUntil;
        bool on = _ctl.State != VpnState.Off;
        bool full = _ctl.State == VpnState.Full;

        // Дыхание + пружинка после переключения.
        double dt = (now - _bounceAt).TotalSeconds;
        double bounce = dt is >= 0 and < 1 ? 0.14 * Math.Exp(-5 * dt) * Math.Cos(16 * dt) : 0;
        double breath = 0.018 * Math.Sin(Environment.TickCount / 4000.0 * 2 * Math.PI);
        SphereScale.ScaleX = SphereScale.ScaleY = 1 + breath + bounce;

        // Свечение дышит со скоростью.
        SphereGlow.Opacity = full ? 0.55 + 0.4 * _shownDown : on ? 0.6 : 0.45;

        // Кольцо: режим + счётчик зажжённых.
        int mode = alert ? 3 : _busy ? 2 : full ? 1 : 0;
        int count = mode == 3 ? 48 : mode == 2 ? 24 : full ? 8 + (int)Math.Round(_shownDown * 40) : 3;
        int flash = mode == 3 ? (now.Millisecond / 250) % 2 : 0;
        int key = mode * 1000 + count * 10 + flash;
        if (key != _ledKey)
        {
            _ledKey = key;
            var lit = mode == 3 ? Mc(0xFF, 0x30, 0x30)
                : mode == 2 ? Mc(0xFF, 0xB0, 0x00)
                : full ? Mc(0x2B, 0xFF, 0x70) : Mc(0xFF, 0x3B, 0x3B);
            var dim = mode == 3 ? Mc(0x4A, 0x12, 0x12)
                : mode == 2 ? Mc(0x4A, 0x32, 0x08)
                : full ? Mc(0x0E, 0x4A, 0x24) : Mc(0x3A, 0x12, 0x12);
            for (int i = 0; i < _leds.Count; i++)
                _leds[i].Fill = new SolidColorBrush(i < count && flash == 0 ? lit : dim);
        }
        if (_ledSpin != null && (mode == 2 || mode == 3))
            _ledSpin.Angle = (_ledSpin.Angle + (mode == 3 ? 12 : 9)) % 360;

        // Моргание.
        double open = alert ? 1.0 : _busy ? 0.85 : full ? 1.0 : on ? 0.9 : 0.55;
        if (now >= _blinkAt)
        {
            _blinkEnd = now.AddMilliseconds(130);
            _blinkAt = now.AddSeconds(2.5 + _rng.NextDouble() * 3);
        }
        EyeLScale.ScaleY = EyeRScale.ScaleY = now < _blinkEnd ? 0.08 : open;

        // Рот: болтает, пока Джарвис говорит.
        bool talking = Jarvis.Talking;
        if (talking != _talkingShown)
        {
            _talkingShown = talking;
            MouthSmile.Visibility = talking ? Visibility.Collapsed : Visibility.Visible;
            MouthOpen.Visibility = talking ? Visibility.Visible : Visibility.Collapsed;
        }
        if (talking)
            MouthScale.ScaleY = 0.35 + 0.65 * Math.Abs(Math.Sin(Environment.TickCount / 1000.0 * 2 * Math.PI * 7));
    }

    private string SpeedWord(double down)
    {
        if (_anon)
            return down < 0.05 ? "idle" : down < 1.5 ? "low" : down < 8 ? "warm" : down < 25 ? "hot" : "critical";
        return down < 0.05 ? "тихо…"
            : down < 1.5 ? "спокойно"
            : down < 8 ? "бодро"
            : down < 25 ? "быстро" : "летит!";
    }

    public void ToggleLog()
    {
        LogPanel.Visibility = LogPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (LogPanel.Visibility == Visibility.Visible && _logLines.Count > 0)
            LogBox.ScrollIntoView(_logLines[^1]);
    }

    public async Task UiSendLog()
    {
        MsgText.Text = "Отправляю лог…";
        MsgText.Foreground = MsgOk;
        string? name = await Task.Run(() => LogSender.Send());
        MsgText.Text = name != null ? $"Лог отправлен ({name})" : "Лог не ушёл — нет сети до сервера";
        MsgText.Foreground = name != null ? MsgAccent : MsgBad;
        if (name != null) Jarvis.Say(JarvisClip.Done); else RedAlert(JarvisClip.Warning);
    }

    private void LogClose_Click(object s, MouseButtonEventArgs e) =>
        LogPanel.Visibility = Visibility.Collapsed;

    // ---- Панель «Телефон»: QR Reality :443, Vision :8443, Telegram MTProto ----
    private int _qrTab; // 0 = xHTTP :443, 1 = Vision :8443, 2 = Telegram proxy

    private void PhoneBtn_Click(object s, RoutedEventArgs e)
    {
        PhonePanel.Visibility = Visibility.Visible;
        ShowQr(_qrTab);
    }

    private void PhoneClose_Click(object s, MouseButtonEventArgs e) => PhonePanel.Visibility = Visibility.Collapsed;
    private void QrVpn_Click(object s, RoutedEventArgs e) => ShowQr(0);
    private void QrVpn2_Click(object s, RoutedEventArgs e) => ShowQr(1);
    private void QrTg_Click(object s, RoutedEventArgs e) => ShowQr(2);

    private void ShowQr(int tab)
    {
        _qrTab = tab;
        string link = tab switch
        {
            1 => Cfg.PhoneVlessLinkVision,
            2 => Cfg.TgProxyLink,
            _ => Cfg.PhoneVlessLinkXhttp,
        };
        QrLink.Text = tab == 2 ? Cfg.TgProxyLink : link;
        QrHint.Text = tab switch
        {
            2 => "MTProto-прокси Telegram (FakeTLS google.com :2087). В Telegram: Настройки → Данные и память → Прокси → Добавить → QR или ссылка.",
            1 => $"VLESS+Vision+REALITY на {Cfg.ServerIp}:{Cfg.VisionPort} ({Cfg.ExitCountry}). iPhone: Streisand или Happ. Android: v2rayNG / Hiddify.",
            _ => $"VLESS+XHTTP+REALITY на {Cfg.ServerIp}:443 ({Cfg.ExitCountry}, SNI intel.com). На iPhone бери вкладку Vision — xHTTP там часто не открывается.",
        };
        foreach (var (btn, idx) in new[] { (QrVpnBtn, 0), (QrVpn2Btn, 1), (QrTgBtn, 2) })
            PaintSeg(btn, idx == tab);
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(link, QRCoder.QRCodeGenerator.ECCLevel.M);
            byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(8,
                new byte[] { 0x2A, 0x08, 0x14, 0xFF }, new byte[] { 0xFF, 0xF5, 0xF8, 0xFF });
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            using var ms = new MemoryStream(png);
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            QrImage.Source = bmp;
        }
        catch (Exception ex) { LogBus.Write("qr: " + ex.Message); }
    }

    private void QrCopy_Click(object s, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(QrLink.Text.Split('\n')[0]);
            QrHint.Text = "Ссылка скопирована.";
            Jarvis.Say(JarvisClip.YesSir);
        }
        catch (Exception ex) { QrHint.Text = "Не скопировалось: " + ex.Message; }
    }

    private void TitleDrag(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void BtnMin_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object s, RoutedEventArgs e) => ForceClose();

    private void ExitLink_Click(object s, MouseButtonEventArgs e) => ForceClose();

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        Topmost = false;
        Activate();
    }

    // «Выход» из трея: снять сплит/TUN, погасить своё ядро, потом закрыться.
    // Иначе останется мёртвый системный прокси → браузеры без интернета.
    public async void ForceClose()
    {
        if (_forceExit) return;
        _forceExit = true;
        try
        {
            if (_ctl.State != VpnState.Off)
            {
                StatusText.Text = "Выключаю…";
                await Guard(_ctl.DisableAllAsync);
            }
        }
        catch { }
        Core.StopOwn();
        Close();
    }

    private void BtnInstall_Click(object s, RoutedEventArgs e)
    {
        string? err = SelfInstall.Install();
        if (err == null)
        {
            _forceExit = true;
            System.Windows.Application.Current.Shutdown();
        }
        else
        {
            InstallPanel.Visibility = Visibility.Collapsed;
            MsgText.Text = "Не установилось: " + err;
            MsgText.Foreground = MsgBad;
        }
    }

    private void BtnInstallLater_Click(object s, RoutedEventArgs e) =>
        InstallPanel.Visibility = Visibility.Collapsed;
}
