using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace KovchegVPN;

public enum VpnState { Off, Split, Full }

public class VpnController
{
    public VpnState State { get; private set; } = VpnState.Off;
    public string? ExitIp { get; private set; }
    public string Message { get; private set; } = "";
    public bool MessageIsError { get; private set; }

    private int _splitFailCount;
    private int _fullFailCount;
    private DateTime _fullSince = DateTime.MinValue;

    // UI выставляет на время операции: сторож молчит, пока идёт tun-on/off.
    public bool OperationInProgress;

    // Туннель пропал не по нашей команде (сон, смена сети, упал sing-box).
    public bool TunnelLost;

    private bool _healing;
    private DateTime _lastHeal = DateTime.MinValue;
    private DateTime _lastExitProbe = DateTime.MinValue;
    // Пауза между лечениями 15→30 мин: рестарт ядра = пачка хендшейков, ТСПУ злится.
    private int _healBackoffMin = 15;

    public async Task RefreshAsync(bool deep)
    {
        if (Probe.TunAdapterUp())
        {
            if (State != VpnState.Full) _fullSince = DateTime.UtcNow;
            State = VpnState.Full;
            _splitFailCount = 0;
            Core.KillOtherXray();
            if (deep)
            {
                bool socks = await Probe.PortOpen(Cfg.OwnSocksPort);
                bool alive = Core.OwnAlive();
                bool grace = OperationInProgress ||
                             (DateTime.UtcNow - _fullSince) < TimeSpan.FromSeconds(30);

                // Дешёвая проверка: адаптер + локальный socks. Эхо через туннель
                // и FindLive — новые Reality-хендшейки, их ТСПУ считает.
                if (alive && socks)
                {
                    if (_lastExitProbe != DateTime.MinValue &&
                        (DateTime.UtcNow - _lastExitProbe) < TimeSpan.FromMinutes(5))
                        return;
                    _lastExitProbe = DateTime.UtcNow;
                    if (Cfg.IsOurs(await Probe.ExitIp($"socks5://127.0.0.1:{Cfg.OwnSocksPort}", 5, maxTries: 1)))
                    {
                        ExitIp = Cfg.ServerIp;
                        _fullFailCount = 0;
                        _healBackoffMin = 15;
                    }
                    else
                    {
                        _fullFailCount++;
                        LogBus.Write($"watchdog: редкая проба не увидела выход (подряд {_fullFailCount}) — ядро не трогаю");
                    }
                    return;
                }

                _fullFailCount++;
                LogBus.Write($"watchdog: выход молчит (подряд {_fullFailCount}, socks={socks}, alive={alive}, grace={grace})");
                if (!grace && !alive && !_healing &&
                    (DateTime.UtcNow - _lastHeal) > TimeSpan.FromMinutes(_healBackoffMin))
                {
                    _healing = true;
                    _lastHeal = DateTime.UtcNow;
                    try
                    {
                        LogBus.Write("watchdog: процесс ядра мёртв — поднимаю прямую трубу");
                        string? c = await Core.EnsureAsync(tryAll: true);
                        if (c != null)
                        {
                            SetMessage("Ядро перезапущено, туннель снова работает.");
                            Jarvis.Say(JarvisClip.Rebooted);
                            _fullFailCount = 0;
                            _healBackoffMin = 15;
                        }
                        else
                        {
                            _healBackoffMin = Math.Min(_healBackoffMin * 2, 30);
                            SetMessage($"Связь просела (ТСПУ). Не долблю — следующая попытка через {_healBackoffMin} мин, или нажми кнопку.", true);
                            Jarvis.Say(JarvisClip.Warning);
                        }
                    }
                    finally { _healing = false; }
                }
            }
            return;
        }
        if (State == VpnState.Full && !OperationInProgress)
        {
            LogBus.Write("watchdog: TUN-адаптер пропал без команды");
            TunnelLost = true;
            // Мёртвый xray иначе вечно долбит Reality Chrome-JA3, который ТСПУ режет.
            Core.StopOwn();
            SetMessage("Туннель отвалился. Нажми кнопку, чтобы включить снова.", true);
        }
        _fullFailCount = 0;

        var (en, srv) = SysProxy.Get();
        bool split = en && srv.StartsWith("127.0.0.1:", StringComparison.Ordinal);
        State = split ? VpnState.Split : VpnState.Off;

        if (deep)
        {
            if (split)
            {
                ExitIp = await Probe.ExitIp("http://" + srv);
                if (ExitIp == null)
                {
                    // Своё ядро умерло (например, после перезагрузки) — поднять,
                    // а не сразу снимать прокси.
                    if (srv.EndsWith(":" + Cfg.OwnHttpPort) && !_healing)
                    {
                        _healing = true;
                        try
                        {
                            LogBus.Write("watchdog: сплит-ядро молчит — перезапускаю");
                            if (await Core.EnsureAsync() != null)
                            {
                                ExitIp = await Probe.ExitIp("http://" + srv);
                                if (ExitIp != null) { _splitFailCount = 0; SetMessage("Ядро перезапущено, сплит работает."); Jarvis.Say(JarvisClip.Rebooted); return; }
                            }
                        }
                        finally { _healing = false; }
                    }
                    // Мёртвый прокси нельзя оставлять: браузеры потеряют интернет.
                    if (++_splitFailCount >= 2)
                    {
                        SysProxy.Disable();
                        State = VpnState.Off;
                        ExitIp = null;
                        SetMessage("Прокси не отвечал — снял его, интернет не пострадал.", true);
                        Jarvis.Say(JarvisClip.Off);
                        _ = Task.Run(() => LogSender.Send());
                    }
                }
                else _splitFailCount = 0;
            }
            else
            {
                ExitIp = null;
                _splitFailCount = 0;
            }
        }
    }

    public async Task EnableFullAsync()
    {
        LogBus.Write("UI: клик — весь ПК");
        Core.ResetHuntOnUserClick();
        // leftover TUN без живого socks заворачивает охоту ядра в петлю —
        // эхо TIMEOUT, UI пишет «ядро не поднялось», лог на :80 тоже не улетает.
        if (Probe.TunAdapterUp())
        {
            LogBus.Write("UI: leftover TUN — снимаю до охоты ядра");
            SetMessage("Снимаю старый TUN…");
            await RunElevated("tun-off");
        }
        Probe.PrepareOpenVpnPath();
        _ = Task.Run(() => LogSender.Send());
        SetMessage("Проверяю ядро…");
        string? core = await Core.EnsureAsync(s => SetMessage(s));
        if (core == null && Probe.TunAdapterUp())
        {
            LogBus.Write("UI: ядро молчит и leftover TUN ещё up — tun-off и повтор");
            SetMessage("Снимаю leftover и поднимаю ядро…");
            await RunElevated("tun-off");
            core = await Core.EnsureAsync(s => SetMessage(s));
        }
        else if (core == null)
            LogBus.Write("UI: ядро молчит — LigaLink, не TAP. Чужой /1 не трогаю (его выключишь сам после живого Kovcheg)");
        if (core == null)
        {
            SetMessage(Cfg.XrayExe() == null
                ? "Не удалось получить ядро (xray) с сервера. Проверь интернет и нажми ещё раз."
                : "Ядро не поднялось — не достучался до сервера. Лог улетел, нажми ещё раз.", true);
            await Task.Run(() => LogSender.Send());
            return;
        }
        LogBus.Write($"UI: ядро = {core}");
        if (core.StartsWith("http", StringComparison.Ordinal))
            LogBus.Write("UI: ядро по http — UDP/голос может не идти");

        if (SysProxy.IsOurProxy()) SysProxy.Disable();

        // Ядро могло подняться без sing-box (недокачанный комплект, антивирус) —
        // для TUN он нужен здесь и сейчас.
        if (Cfg.SingBoxExe() == null)
        {
            LogBus.Write("UI: sing-box нет — докачиваю ядра перед TUN");
            await Bins.EnsureAsync(s => SetMessage(s));
            if (Cfg.SingBoxExe() == null)
            {
                SetMessage("sing-box не найден и не скачался. Проверь интернет/антивирус и нажми ещё раз.", true);
                _ = Task.Run(() => LogSender.Send());
                return;
            }
        }

        SetMessage("Поднимаю TUN…");
        LogBus.Write("UI: запускаю tun-on");
        var (_, text, isErr) = await RunElevated("tun-on");
        LogBus.Write($"UI: tun-on результат: {text}");
        SetMessage(text, isErr);
        if (!isErr)
            _ = Task.Run(() => LogSender.Send());
        await RefreshAsync(true);
    }

    public async Task DisableFullAsync()
    {
        LogBus.Write("UI: клик — выключить (tun-off)");
        SetMessage("Снимаю TUN…");
        var (_, text, isErr) = await RunElevated("tun-off");
        LogBus.Write($"UI: tun-off результат: {text}");
        SetMessage(text, isErr);
        await RefreshAsync(true);
    }

    public async Task EnableSplitAsync()
    {
        LogBus.Write("UI: клик — сплит");
        if (Probe.TunAdapterUp())
        {
            SetMessage("TUN активен — сначала выключи его круглой кнопкой.", true);
            return;
        }
        SetMessage("Поднимаю ядро со сплит-правилами…");
        // Сплит-маршрутизация (РФ/YouTube напрямую) есть только в нашем ядре —
        // поднимаем его первым, чужие 12809/10809 — резерв без правил.
        string? core = await Core.EnsureAsync(s => SetMessage(s));
        int? port = await FindLiveHttpPort();
        LogBus.Write($"UI: ядро = {core ?? "нет"}, http-порт = {port?.ToString() ?? "нет"}");
        if (port == null)
        {
            SetMessage("Живого прокси нет (10819/12809/10809). Смотри лог ниже.", true);
            await Task.Run(() => LogSender.Send());
            return;
        }
        SysProxy.Enable(port.Value);
        string note = port == Cfg.OwnHttpPort ? $"РФ и YouTube напрямую, остальное — {Cfg.ExitCountry}." : "резервное ядро без RU-правил.";
        SetMessage($"Сплит: 127.0.0.1:{port}. {note}");
        await RefreshAsync(true);
    }

    public async Task DisableSplitAsync()
    {
        SysProxy.Disable();
        SetMessage("Прокси снят.");
        await RefreshAsync(true);
    }

    public async Task DisableAllAsync()
    {
        SysProxy.Disable();
        if (Probe.TunAdapterUp()) await RunElevated("tun-off");
        SetMessage("Всё выключено.");
        await RefreshAsync(true);
    }

    // Один клик = смена режима: старый режим гасится, новый поднимается.
    public async Task SwitchModeAsync(bool wantFull)
    {
        if (wantFull && State == VpnState.Split)
        {
            LogBus.Write("UI: смена режима сплит → весь ПК");
            SysProxy.Disable();
            await EnableFullAsync();
        }
        else if (!wantFull && State == VpnState.Full)
        {
            LogBus.Write("UI: смена режима весь ПК → сплит");
            await DisableFullAsync();
            await EnableSplitAsync();
        }
    }

    private void SetMessage(string msg, bool isError = false)
    {
        Message = msg;
        MessageIsError = isError;
    }

    private static async Task<int?> FindLiveHttpPort()
    {
        foreach (int p in Cfg.HttpPorts)
        {
            string? ip = await Probe.ExitIp($"http://127.0.0.1:{p}", 5);
            if (Cfg.IsOurs(ip)) return p;
        }
        return null;
    }

    private static async Task<(bool ok, string text, bool isErr)> RunElevated(string mode)
    {
        try
        {
            try { File.Delete(Cfg.ResultPath); } catch { }
            string exe = Environment.ProcessPath ?? Cfg.ExePath;
            var psi = new ProcessStartInfo(exe, $"--elevated {mode}");
            if (Native.IsElevated())
            {
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
            }
            else
            {
                psi.UseShellExecute = true;
                psi.Verb = "runas";
            }
            var p = Process.Start(psi);
            if (p == null) return (false, "Не удалось запросить права.", true);
            LogBus.Write($"UI: elevated worker pid {p.Id} запущен");

            // Worker имеет watchdog 200с; здесь ждём чуть дольше и отпускаем UI.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(220));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                LogBus.Write("UI: worker не отвечает 220с — отпускаю UI");
                return (false, "Worker не отвечает 220с. Повторный запуск убьёт зависшие процессы.", true);
            }
            LogBus.Write($"UI: worker завершился, код {p.ExitCode}");

            string res = "";
            try { res = File.ReadAllText(Cfg.ResultPath).Trim(); } catch { }
            if (res.StartsWith("OK|")) return (true, res[3..], false);
            if (res.StartsWith("FAIL|")) return (false, res[5..], true);
            return p.ExitCode == 0 ? (true, "Готово.", false) : (false, $"Ошибка ({p.ExitCode}). Лог: {Cfg.LogPath}", true);
        }
        catch (Win32Exception)
        {
            return (false, "UAC отменён — ничего не менял.", true);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, true);
        }
    }
}
