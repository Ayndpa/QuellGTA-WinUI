namespace QuellGTA.Services;

/// <summary>卡单模式，对应原版「卡单选项」。</summary>
public enum SoloMode
{
    Off = 0,
    /// <summary>1. 暂停进程（兼容性好）</summary>
    SuspendProcess = 1,
    /// <summary>2. 挂起窗口（传承版）</summary>
    SuspendWindow = 2,
    /// <summary>3. 右键菜单（增强版）：向游戏窗口发送 Alt+Space 打开系统菜单</summary>
    SystemMenu = 3,
    /// <summary>4. 阻止 P2P 端口（6672）</summary>
    BlockP2PPort = 4,
}

/// <summary>瞬间悬停模式，对应原版「瞬间悬停」菜单。</summary>
public enum HoverMode { Off = 0, SwitchCharacter = 1, AltF4BlockCloud = 2, AltF4Disconnect = 3 }

/// <summary>前台挂机方式。</summary>
public enum AfkMode { HoldRightMouse = 0, HoldS = 1 }

/// <summary>
/// 移植自原版启动窗口里的各个「_xxx按钮_被单击」子程序：杀掉进程、阻止联网、禁用网卡、恢复网络、
/// 战局卡单、瞬间悬停、连击左键、前台挂机，以及热键分发和统计数据。所有界面页面和热键都调用这里。
/// </summary>
public sealed partial class QuellCore : IDisposable
{
    public static readonly string[] HotkeyNames =
        ["杀掉键", "阻网键", "禁卡键", "复网键", "卡单键", "悬停键", "连击左键", "挂机键", "计时键", "战局甲键", "战局乙键",
         "粘贴键", "英语解键", "指纹键", "手柄跑键", "锁帧键", "骇入键"];

    /// <summary>自编宏的热键名前缀，后面跟宏的 Id。</summary>
    public const string MacroHotkeyPrefix = "宏:";

    public Settings Settings { get; } = new();
    public LogService Log { get; } = new();
    public GameService Game { get; }
    public FirewallService Firewall { get; } = new();
    public NicService Nic { get; }
    public HotkeyService Hotkeys { get; } = new();
    public SoundService Sound { get; }
    public GameFilesService Files { get; }
    public SessionLockService SessionLock { get; }
    public MacroService Macros { get; }
    public TotpService Totp { get; }
    public OverlayService Overlay { get; }
    public TeamService Team { get; }
    public BotService Bots { get; }

    private readonly Timer _monitor;
    private readonly object _soloGate = new();
    private long _lastKill, _lastCut, _lastRestore, _lastSolo, _lastHover, _lastClickToggle;
    private CancellationTokenSource? _soloCts, _clickCts, _afkCts;
    private int _hovering;

    // ───────── 状态 ─────────
    public bool GameRunning { get; private set; }
    public bool NetBlocked { get; private set; }
    public bool NicDisabled { get; private set; }
    public bool SoloActive { get; private set; }
    public bool AutoClicking => _clickCts is not null;
    public bool AfkActive => _afkCts is not null;

    /// <summary>任意状态变化时触发（可能在后台线程）。</summary>
    public event Action? StateChanged;

    public QuellCore()
    {
        Game = new GameService(Settings);
        Nic = new NicService(Settings);
        Nic.DeviceError += error => Log.Write("启用设备失败 " + error, 2);
        Sound = new SoundService(Settings);
        Files = new GameFilesService(Settings, Game, Log);
        SessionLock = new SessionLockService(Settings, Files, Log);
        Macros = new MacroService(Settings);
        Totp = new TotpService(Settings);
        Overlay = new OverlayService(Settings, Game);
        Team = new TeamService(Settings, Log, Sound);
        Team.ActionReceived += OnTeamAction;
        Bots = new BotService(Settings, Game, Log, Sound);
        InitializeExtras();

        Lang.Apply(Language);
        Game.LoadFromSettings();
        foreach (var name in HotkeyNames) Hotkeys.Set(name, GetHotkey(name));
        RegisterMacroHotkeys();
        Overlay.Refresh();
        Hotkeys.Triggered += OnHotkey;

        // 「自动恢复状态」：上次退出时网络还处于断开状态的话，界面上继续显示，方便一键恢复。
        NetBlocked = Firewall.Exists(FirewallService.BlockRule) || WfpRuleCount() > 0;
        NicDisabled = Nic.HasPendingRestore;

        _monitor = new Timer(_ => Monitor(), null, 0, 1000);
    }

    public void Dispose()
    {
        _monitor.Dispose();
        _clickCts?.Cancel();
        StopAfk(null);
        Macros.StopAll();
        DisposeExtras();
        Overlay.Dispose();
        Team.Dispose();
        if (SoloActive) ApplySolo(false); // 退出前务必把游戏放开，否则进程会一直卡死
        Hotkeys.Dispose();
    }

    private static long Now => Environment.TickCount64;
    private void Notify() => StateChanged?.Invoke();

    private void Monitor()
    {
        try
        {
            if (Game.DetectRunningEdition())
                Log.Write($"检测到正在运行的游戏：{Game.ProcessName}，已自动切换版本。", 1);
            var running = Game.IsRunning();
            if (running) RememberGamePath();
            ExtrasTick(running);
            if (running == GameRunning) return;
            GameRunning = running;
            Log.Write(running ? $"游戏已启动（{Game.ProcessName}）。" : "游戏已退出。", 1);
            if (!running)
            {
                if (SoloActive && SoloMode != SoloMode.BlockP2PPort) SetSoloState(false);
                Macros.StopAll();
                Task.Run(Files.OnGameExited);
                Task.Run(OnGameExited);
            }
            else
            {
                Task.Run(OnGameStarted);
            }
            Notify();
        }
        catch (Exception) { }
    }

    /// <summary>
    /// 刷新进程路径：把游戏 exe 的完整路径记到配置里。战局锁、启动选项这些功能在游戏没运行时也要知道游戏装在哪。
    /// 自定义进程不记录，避免把别的程序当成游戏目录。
    /// </summary>
    private void RememberGamePath()
    {
        if (_gamePathSaved || Game.Edition == GameEdition.Custom) return;
        var path = Game.GetGamePath();
        if (path.Length == 0) return;
        Settings.Set("本地", "进程路径", path);
        _gamePathSaved = true;
    }

    private bool _gamePathSaved;

    /// <summary>语言选项：1 简体中文（默认）、2 繁体中文、3 English。重启后完全生效。</summary>
    public int Language
    {
        get => Math.Clamp(Settings.GetInt("本地", "语言", 1), 1, 3);
        set => Settings.SetInt("本地", "语言", Math.Clamp(value, 1, 3));
    }

    // ───────── 界面相关的设置项 ─────────

    /// <summary>显示托盘图标（原版默认开启）。</summary>
    public bool TrayIcon
    {
        get => Settings.GetBool("本地", "托盘图标", true);
        set => Settings.SetBool("本地", "托盘图标", value);
    }
    /// <summary>启动之后最小化。</summary>
    public bool StartMinimized
    {
        get => Settings.GetBool("本地", "最小化启动", false);
        set => Settings.SetBool("本地", "最小化启动", value);
    }
    /// <summary>点关闭按钮时隐藏到托盘而不是退出。</summary>
    public bool CloseToTray
    {
        get => Settings.GetBool("本地", "关闭到托盘", false);
        set => Settings.SetBool("本地", "关闭到托盘", value);
    }
    /// <summary>重要日志是否推送系统通知（原版「不推送系统通知」的反义）。</summary>
    public bool TrayNotifications
    {
        get => Settings.GetBool("本地", "系统通知", true);
        set => Settings.SetBool("本地", "系统通知", value);
    }

    // ───────── 设置项 ─────────

    public Hotkey GetHotkey(string name) => Hotkey.Parse(Settings.Get("本地", name, ""));

    public void SetHotkey(string name, Hotkey hotkey)
    {
        Settings.Set("本地", name, hotkey.IsSet ? hotkey.Serialize() : "");
        Hotkeys.Set(name, hotkey);
    }

    public bool KillEnabled
    {
        get => Settings.GetBool("本地", "使能杀掉", true);
        set => Settings.SetBool("本地", "使能杀掉", value);
    }
    public bool OnlyGameProcess
    {
        get => Settings.GetBool("本地", "仅游戏断网", true);
        set => Settings.SetBool("本地", "仅游戏断网", value);
    }
    /// <summary>
    /// 「进程断网 - 手动选择」：指定要断网的程序路径（例如加速器），留空则自动探测游戏进程。
    /// </summary>
    public string BlockTargetPath
    {
        get => Settings.Get("本地", "被断网进程", "");
        set => Settings.Set("本地", "被断网进程", value.Trim().Trim('"'));
    }
    public BlockScope BlockScope
    {
        get => (BlockScope)Settings.GetInt("本地", "阻网范围", 0);
        set => Settings.SetInt("本地", "阻网范围", (int)value);
    }
    public bool BlockCloudSave
    {
        get => Settings.GetBool("本地", "阻云存档", true);
        set => Settings.SetBool("本地", "阻云存档", value);
    }
    public bool BlockTransaction
    {
        get => Settings.GetBool("本地", "阻交易", false);
        set => Settings.SetBool("本地", "阻交易", value);
    }
    public bool BlockJobs
    {
        get => Settings.GetBool("本地", "阻差事", false);
        set => Settings.SetBool("本地", "阻差事", value);
    }
    public SoloMode SoloMode
    {
        get => (SoloMode)Math.Clamp(Settings.GetInt("本地", "使能卡单", 1), 0, 4);
        set => Settings.SetInt("本地", "使能卡单", (int)value);
    }
    /// <summary>卡单持续时间（毫秒），原版默认 10001。</summary>
    public int SoloDurationMs
    {
        get => Math.Clamp(Settings.GetInt("本地", "卡单持续", 10001), 1000, 60000);
        set => Settings.SetInt("本地", "卡单持续", Math.Clamp(value, 1000, 60000));
    }
    /// <summary>两次卡单操作之间的安全间隔（毫秒），原版默认 300。</summary>
    public int SoloIntervalMs
    {
        get => Math.Clamp(Settings.GetInt("本地", "卡单间隔", 300), 100, 10000);
        set => Settings.SetInt("本地", "卡单间隔", Math.Clamp(value, 100, 10000));
    }
    public HoverMode HoverMode
    {
        get => (HoverMode)Settings.GetInt("本地", "悬停模式", 0);
        set => Settings.SetInt("本地", "悬停模式", (int)value);
    }
    public bool KillLauncherOnKill
    {
        get => Settings.GetBool("扩展", "杀掉时杀启动器", false);
        set => Settings.SetBool("扩展", "杀掉时杀启动器", value);
    }
    public bool KillLauncherOnBlock
    {
        get => Settings.GetBool("扩展", "阻网时杀启动器", false);
        set => Settings.SetBool("扩展", "阻网时杀启动器", value);
    }
    public bool KillLauncherOnNic
    {
        get => Settings.GetBool("扩展", "禁卡时杀启动器", false);
        set => Settings.SetBool("扩展", "禁卡时杀启动器", value);
    }
    /// <summary>杀启动器延迟（毫秒），原版默认 888。</summary>
    public int LauncherDelayMs
    {
        get => Math.Clamp(Settings.GetInt("扩展", "杀启动器延迟", 888), 0, 60000);
        set => Settings.SetInt("扩展", "杀启动器延迟", Math.Clamp(value, 0, 60000));
    }
    public bool HotkeysOnlyForeground
    {
        get => Settings.GetBool("本地", "仅焦点热键", false);
        set => Settings.SetBool("本地", "仅焦点热键", value);
    }
    public bool HotkeysOnlyRunning
    {
        get => Settings.GetBool("本地", "仅运行热键", false);
        set => Settings.SetBool("本地", "仅运行热键", value);
    }
    /// <summary>连击左键的频率（次/秒）。</summary>
    public int ClicksPerSecond
    {
        get => Math.Clamp(Settings.GetInt("本地", "连击频率", 10), 1, 50);
        set => Settings.SetInt("本地", "连击频率", Math.Clamp(value, 1, 50));
    }
    /// <summary>连击左键模式：true = 按住按键时启动，false = 按下时切换开关（原版默认）。</summary>
    public bool ClickWhileHeld
    {
        get => Settings.GetInt("本地", "连击左键模式", 2) == 1;
        set => Settings.SetInt("本地", "连击左键模式", value ? 1 : 2);
    }
    /// <summary>伪后台：挂机开始后把游戏窗口压到所有窗口的最底下。游戏仍是活动窗口，只是被别的窗口盖住了。</summary>
    public bool AfkPseudoBackground
    {
        get => Settings.GetBool("本地", "挂机伪后台", false);
        set => Settings.SetBool("本地", "挂机伪后台", value);
    }

    /// <summary>仅执行伪后台操作：窗口_强制显示(句柄, 假, 真)。</summary>
    public bool SendGameWindowToBack()
    {
        var hwnd = Game.GetWindowHandle();
        if (hwnd == 0) return false;
        const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10;
        return Native.OverlayNative.SetWindowPos(hwnd, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
    }

    public AfkMode AfkMode
    {
        get => (AfkMode)Settings.GetInt("本地", "挂机模式", 0);
        set => Settings.SetInt("本地", "挂机模式", (int)value);
    }

    // ───────── 统计数据 ─────────
    public int KillCount => Settings.GetInt("统计", "杀掉计数", 0);
    public int CutCount => Settings.GetInt("统计", "断网计数", 0);
    public int SoloCount => Settings.GetInt("统计", "卡单计数", 0);

    private void Count(string key) => Settings.SetInt("统计", key, Settings.GetInt("统计", key, 0) + 1);

    public void ResetStatistics()
    {
        foreach (var key in new[] { "杀掉计数", "断网计数", "卡单计数" }) Settings.SetInt("统计", key, 0);
        Notify();
    }

    // ───────── 杀掉进程 ─────────

    /// <summary>_杀掉按钮_被单击</summary>
    public void Kill()
    {
        if (Now - Interlocked.Exchange(ref _lastKill, Now) < 600) { Log.Write(Lang.C(72)); return; }
        Sound.Play(SoundService.Kill);
        if (Team.BroadcastKill) Team.Broadcast("KP");
        if (KillEnabled) EndGameProcess(); else Log.Write(Lang.C(73), 1);
        if (KillLauncherOnKill) Task.Run(() => KillLauncher(LauncherDelayMs));
        ReportCloudSave(isKill: true);
        Notify();
    }

    /// <summary>结束游戏进程：TerminateProcess → taskkill → CIM，逐级加码。</summary>
    private void EndGameProcess()
    {
        if (Game.Terminate()) { Killed(); return; }
        if (!Game.IsRunning()) { Log.Write(Lang.C(78)); return; }

        Log.Write(Lang.C(76) + "TSKL", 1);
        Game.TerminateWithTaskkill();
        Thread.Sleep(250);
        if (!Game.IsRunning()) { Killed(); return; }

        Log.Write(Lang.C(76) + "CIM", 1);
        Game.TerminateWithCim();
        Thread.Sleep(250);
        if (Game.IsRunning()) Log.Write(Lang.C(77), 2); else Killed();

        void Killed()
        {
            Log.Write(Lang.C(75), 2);
            Count("杀掉计数");
        }
    }

    /// <summary>结束启动器进程</summary>
    public void KillLauncher(int delayMs)
    {
        if (delayMs > 0)
        {
            Log.Write($"{Lang.C(395)}{delayMs / 1000.0:0.###} {Lang.C(593)}");
            Thread.Sleep(delayMs);
        }
        var (found, killed) = Game.KillLauncher();
        Log.Write(!found ? "Rockstar 启动器未在运行。" : killed ? "已结束 Rockstar 启动器。" : "结束 Rockstar 启动器失败。", found ? 1 : 0);
    }

    // ───────── 断网 / 复网 ─────────

    /// <summary>断网时要限制到的进程路径；取不到时返回 null（改为断开所有软件网络）。</summary>
    private string? ResolveBlockTarget()
    {
        if (!OnlyGameProcess) return null;
        var custom = Settings.Get("本地", "被断网进程", "");
        if (custom.Length > 0) return custom;
        if (!Game.IsRunning()) { Log.Write(Lang.C(146), 1); return null; }
        var path = Game.GetGamePath();
        if (path.Length == 0) { Log.Write(Lang.C(147), 1); return null; }
        return path;
    }

    /// <summary>_阻网按钮_被单击：创建防火墙规则阻止联网。</summary>
    public void BlockNetwork()
    {
        if (Now - Interlocked.Exchange(ref _lastCut, Now) < 500) { Log.Write(Lang.C(150)); return; }
        Sound.Play(SoundService.Block);
        if (Team.BroadcastBlock && Team.Broadcast("FW1"))
        {
            // 广播要先发出去，本机才能断网
            Log.Write(Lang.C(104));
            Thread.Sleep(500);
        }
        try
        {
            ApplyBlock();
            NetBlocked = true;
            if (!WfpMode && !Firewall.IsFirewallEnabled()) Log.Write(Lang.C(28), 3);
            Log.Write(Lang.C(148), 1);
            Count("断网计数");
        }
        catch (Exception ex)
        {
            Log.Write("创建防火墙规则失败：" + ex.Message, 3);
        }
        if (KillLauncherOnBlock) Task.Run(() => KillLauncher(LauncherDelayMs));
        ReportCloudSave(isKill: false);
        Notify();
    }

    /// <summary>_禁卡按钮_被单击：禁用网卡。</summary>
    public void DisableNic()
    {
        if (Now - Interlocked.Exchange(ref _lastCut, Now) < 500) { Log.Write(Lang.C(153)); return; }
        Sound.Play(SoundService.NicDisabled);
        if (Team.BroadcastNic && Team.Broadcast("BC"))
        {
            Log.Write(Lang.C(105));
            Thread.Sleep(500);
        }
        var names = Nic.Disable();
        foreach (var error in Nic.LastErrors) Log.Write("禁用网卡失败 " + error, 2);
        if (names.Length == 0)
        {
            if (Nic.LastErrors.Count == 0) Log.Write("没有找到可以禁用的网卡。", 2);
        }
        else
        {
            NicDisabled = true;
            foreach (var name in names)
                Log.Write($"{Lang.C(138)}\"{name}\"{(Nic.HardwareMode ? Lang.C(139) : Nic.IpOnly ? Lang.C(141) : Lang.C(140))}", 1);
            Count("断网计数");
        }
        if (KillLauncherOnNic) Task.Run(() => KillLauncher(LauncherDelayMs));
        ReportCloudSave(isKill: false);
        Notify();
    }

    /// <summary>_复网按钮_被单击：删除防火墙规则并恢复网卡。</summary>
    public void RestoreNetwork()
    {
        if (Now - Interlocked.Exchange(ref _lastRestore, Now) < 1600) { Log.Write(Lang.C(165)); return; }
        Sound.Play(SoundService.Restore);

        // 原版逻辑：只有一种处于断开状态时只恢复那一种；都没有（或都有）时两种都恢复一遍。
        var both = NetBlocked == NicDisabled;
        if (both || NetBlocked)
        {
            try
            {
                Firewall.Remove(FirewallService.BlockRule);
                try { Wfp.Clear(); } catch (Exception) { } // 不管当前是哪种模式，两边的规则都清掉
                Log.Write(Lang.C(149), 1);
            }
            catch (Exception ex) { Log.Write("删除防火墙规则失败：" + ex.Message, 3); }
            NetBlocked = false;
        }
        if (both || NicDisabled)
        {
            foreach (var name in Nic.Restore())
                Log.Write($"{Lang.C(142)}\"{name}\"{Lang.C(139)}", 1);
            NicDisabled = false;
        }
        Notify();
    }

    /// <summary>修复网络之后，把界面上的断网状态清掉。</summary>
    public void ResetNetworkState()
    {
        NetBlocked = false;
        NicDisabled = false;
        Notify();
    }

    // ───────── 战局卡单 ─────────

    /// <summary>_卡单按钮_被单击：开始卡单，到时间自动结束；卡单中再按一次立即结束。</summary>
    public void ToggleSolo()
    {
        lock (_soloGate)
        {
            if (Now - _lastSolo < SoloIntervalMs) return;
            _lastSolo = Now;
            if (SoloActive)
            {
                _soloCts?.Cancel();
                _soloCts = null;
                ApplySolo(false);
                if (Team.BroadcastSolo) Team.Broadcast("SOLO", "OFF");
                Sound.Play(SoundService.MacroOff);
                return;
            }

            ApplySolo(true);
            Sound.Play(SoundService.SoloOn);
            if (!SoloActive) return;
            if (Team.BroadcastSolo) Team.Broadcast("SOLO", "ON");
            StartSoloTimer(broadcastEnd: true);
        }
    }

    /// <summary>卡单自动线程：到时间自动结束卡单。调用时必须已经持有 _soloGate。</summary>
    private void StartSoloTimer(bool broadcastEnd)
    {
        var cts = _soloCts = new CancellationTokenSource();
        var duration = SoloDurationMs;
        Task.Run(async () =>
        {
            try { await Task.Delay(duration, cts.Token); }
            catch (OperationCanceledException) { return; }
            lock (_soloGate)
            {
                if (cts.IsCancellationRequested) return;
                _soloCts = null;
                Sound.Play(SoundService.SoloAutoEnd);
                ApplySolo(false);
                if (broadcastEnd && Team.BroadcastSolo) Team.Broadcast("SOLO", "OFF");
            }
        });
    }

    // ───────── 联机广播：执行队友发来的操作 ─────────

    /// <summary>
    /// 客户数据处理 / 服务数据处理里对 KP、FW1、BC、SOLO 的处理：同小队的成员做了某个操作，本机跟着做一遍
    /// （不再向外广播）。每种操作都受「广播内容」里对应开关控制。
    /// </summary>
    private void OnTeamAction(string act, string text, string user)
    {
        try
        {
            switch (act)
            {
                case "KP" when Team.BroadcastKill:
                    if (Now - Interlocked.Exchange(ref _lastKill, Now) < 500) { Log.Write(Lang.C(90) + user + Lang.C(91)); break; }
                    Sound.Play(SoundService.Kill);
                    if (KillEnabled)
                    {
                        Log.Write(Lang.C(90) + user, 1);
                        EndGameProcess();
                    }
                    else
                    {
                        Log.Write(Lang.C(90) + user + Lang.C(92), 1);
                    }
                    if (KillLauncherOnKill) Task.Run(() => KillLauncher(LauncherDelayMs));
                    break;

                case "FW1" when Team.BroadcastBlock:
                    if (Now - Interlocked.Exchange(ref _lastCut, Now) < 1600) { Log.Write(Lang.C(93) + user + Lang.C(91)); break; }
                    Sound.Play(SoundService.Block);
                    Log.Write(Lang.C(93) + user, 1);
                    try
                    {
                        ApplyBlock();
                        NetBlocked = true;
                        if (!WfpMode && !Firewall.IsFirewallEnabled()) Log.Write(Lang.C(28), 3);
                        Log.Write(Lang.C(148), 1);
                        Count("断网计数");
                    }
                    catch (Exception ex) { Log.Write("创建防火墙规则失败：" + ex.Message, 3); }
                    if (KillLauncherOnBlock) Task.Run(() => KillLauncher(LauncherDelayMs));
                    break;

                case "BC" when Team.BroadcastNic:
                    if (Now - Interlocked.Exchange(ref _lastCut, Now) < 1600) { Log.Write(Lang.C(94) + user + Lang.C(91)); break; }
                    Sound.Play(SoundService.NicDisabled);
                    Log.Write(Lang.C(94) + user, 1);
                    if (Nic.Disable().Length > 0)
                    {
                        NicDisabled = true;
                        Count("断网计数");
                    }
                    if (KillLauncherOnNic) Task.Run(() => KillLauncher(LauncherDelayMs));
                    break;

                case "SOLO" when Team.BroadcastSolo:
                    lock (_soloGate)
                    {
                        if (Now - _lastSolo < SoloIntervalMs) break;
                        _lastSolo = Now;
                        if (SoloMode == SoloMode.Off) { Log.Write(Lang.C(358) + user + Lang.C(92), 1); break; }
                        if (text == "OFF" && SoloActive)
                        {
                            Log.Write(Lang.C(368) + user, 1);
                            Sound.Play(SoundService.SoloAutoEnd);
                            _soloCts?.Cancel();
                            _soloCts = null;
                            ApplySolo(false);
                        }
                        else if (text == "ON" && !SoloActive)
                        {
                            Log.Write(Lang.C(358) + user, 1);
                            Sound.Play(SoundService.SoloOn);
                            ApplySolo(true);
                            // 原版在这里完全依赖对方发来的「停止」广播；万一那条广播丢了，游戏会一直卡着。
                            // 所以本机也按自己的卡单时长兜底结束（不再向外广播）。
                            if (SoloActive) StartSoloTimer(broadcastEnd: false);
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"执行队友的操作「{act}」出错：{ex.Message}", 3);
        }
        Notify();
    }

    /// <summary>卡单游戏(启动状态)</summary>
    private void ApplySolo(bool start)
    {
        var mode = SoloMode;
        var applied = false;
        try
        {
            if (mode == SoloMode.BlockP2PPort)
            {
                // 模式 4 不依赖游戏进程
                if (start) Firewall.BlockP2PPort(); else Firewall.Remove(FirewallService.BlockRule);
                applied = true;
            }
            else if (!Game.IsRunning())
            {
                if (start) Log.Write(Lang.C(157));
            }
            else
            {
                var hwnd = Game.GetWindowHandle();
                switch (mode)
                {
                    case SoloMode.SuspendProcess:
                        applied = Game.Suspend(start) || !start;
                        break;
                    case SoloMode.SuspendWindow:
                        if (start) applied = GameService.SuspendWindowThread(hwnd, true) >= 0;
                        else
                        {
                            // 恢复到挂起计数归零为止（原版最多尝试 5 次）
                            for (var i = 0; i < 5 && GameService.SuspendWindowThread(hwnd, false) > 1; i++) { }
                            applied = true;
                        }
                        break;
                    case SoloMode.SystemMenu:
                        if (!Game.IsForeground()) Game.BringToFront();
                        PostSystemMenuKeys(hwnd, start);
                        applied = true;
                        break;
                    default:
                        if (start) Log.Write(Lang.C(158));
                        break;
                }
                if (start && !applied && mode != SoloMode.Off) Log.Write("卡单失败，无法暂停游戏（权限不足？）。", 2);
            }
        }
        catch (Exception ex)
        {
            Log.Write("卡单出错：" + ex.Message, 3);
        }

        if (start && !applied) return;
        if (start)
        {
            Log.Write(Lang.C(62), 1);
            Count("卡单计数");
        }
        else if (SoloActive)
        {
            Log.Write(Lang.C(63), 1);
        }
        SetSoloState(start);
    }

    private void SetSoloState(bool active)
    {
        SoloActive = active;
        Notify();
    }

    /// <summary>
    /// 卡单模式 3：开始时向游戏窗口投递 Alt+Space 打开标题栏系统菜单（主线程进入菜单模态循环，效果等同暂停），
    /// 结束时投递一次 Alt 关掉菜单。lParam 的构造方式对应原版的「取扩展码」。
    /// </summary>
    private static void PostSystemMenuKeys(nint hwnd, bool open)
    {
        const int VkMenu = 0x12, VkSpace = 0x20;
        nint Down(int vk, bool alt) => (nint)(1 | (Native.NativeMethods.MapVirtualKeyW((uint)vk, 0) << 16) | (alt ? 1u << 29 : 0));
        nint Up(int vk) => (nint)(1 | (Native.NativeMethods.MapVirtualKeyW((uint)vk, 0) << 16) | (1u << 30) | (1u << 31));

        if (open)
        {
            Native.NativeMethods.PostMessageW(hwnd, Native.NativeMethods.WM_KEYDOWN, VkMenu, Down(VkMenu, true));
            Native.NativeMethods.PostMessageW(hwnd, Native.NativeMethods.WM_SYSKEYDOWN, VkSpace, Down(VkSpace, true));
            Native.NativeMethods.PostMessageW(hwnd, Native.NativeMethods.WM_KEYUP, VkMenu, Up(VkMenu));
            Native.NativeMethods.PostMessageW(hwnd, Native.NativeMethods.WM_KEYUP, VkSpace, Up(VkSpace));
        }
        else
        {
            Native.NativeMethods.PostMessageW(hwnd, Native.NativeMethods.WM_SYSKEYDOWN, VkMenu, Down(VkMenu, true));
            Native.NativeMethods.PostMessageW(hwnd, Native.NativeMethods.WM_KEYUP, VkMenu, Up(VkMenu));
        }
    }

    // ───────── 瞬间悬停 ─────────

    /// <summary>
    /// 断网悬停：临时阻断游戏联网 → 向游戏窗口发送关闭消息（等同 Alt+F4）→ 按 Esc 取消退出提示 → 撤掉临时规则。
    /// 游戏在弹出退出提示前会尝试保存，这时保存请求发不出去，于是停在保存失败的状态上，而游戏本身不会退出。
    /// 这里的 Esc 对应原版的支持库常量 #_Lib0Const178：反编译结果里没有名字，是从别处的用法反推出来的
    /// （同一组常量里 173 被注释为 Alt、179 是空格，英语锁定里它和 170 一起表示「回车或 Esc」）。
    /// </summary>
    public void Hover()
    {
        var mode = HoverMode;
        if (mode is HoverMode.Off or HoverMode.SwitchCharacter) { Log.Write("瞬间悬停功能未开启。"); return; }
        var hwnd = Game.GetWindowHandle();
        if (hwnd == 0) { Log.Write("悬停失败，游戏进程未在运行。"); return; }
        if (Interlocked.Exchange(ref _hovering, 1) == 1) return;
        try
        {
            Sound.Play(SoundService.Hover);
            Game.BringToFront();
            using var temporary = WfpMode
                ? Wfp.BlockTemporarily(new WfpRule(ResolveBlockTarget(), Protocol: mode == HoverMode.AltF4BlockCloud ? 6 : null))
                : null;
            if (!WfpMode) Firewall.BlockForHover(mode == HoverMode.AltF4BlockCloud, ResolveBlockTarget());
            if (!WfpMode && !Firewall.IsFirewallEnabled())
            {
                Log.Write(Lang.C(28), 3);
            }
            else
            {
                Thread.Sleep(20);
                Native.NativeMethods.PostMessageW(hwnd, Native.NativeMethods.WM_CLOSE, 0, 0);
                Thread.Sleep(250);
                // Esc 用来取消游戏的退出提示；游戏已不在前台时不要按，免得误触其他窗口
                if (Game.IsForeground()) InputSim.KeyPress(0x1B /* Esc */, 50);
                Log.Write(Lang.C(67), 1);
            }
        }
        catch (Exception ex)
        {
            Log.Write("悬停出错：" + ex.Message, 3);
        }
        finally
        {
            try { Firewall.Remove(FirewallService.HoverRule); } catch (Exception) { }
            Interlocked.Exchange(ref _hovering, 0);
        }
    }

    // ───────── 连击左键 ─────────

    /// <summary>「是否可用宏」：游戏在运行且位于前台时才允许模拟输入。</summary>
    private bool MacroAllowed() => Game.IsRunning() && Game.IsForeground();

    public void SetAutoClick(bool on)
    {
        if (!on)
        {
            var old = Interlocked.Exchange(ref _clickCts, null);
            if (old is null) return;
            old.Cancel();
            Sound.Play(SoundService.MacroOff);
            Log.Write("连击左键已停止。", 1);
            Notify();
            return;
        }
        if (_clickCts is not null) return;
        if (!MacroAllowed()) { Log.Write("连击左键未启动：游戏不在前台。"); return; }

        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _clickCts, cts, null) is not null) return;
        Sound.Play(SoundService.MacroOn);
        Log.Write($"连击左键已启动（{ClicksPerSecond} 次/秒）。", 1);
        Notify();
        var period = 1000 / ClicksPerSecond;
        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (!MacroAllowed()) { SetAutoClick(false); return; } // 游戏不在前台时自动停止
                InputSim.LeftClick();
                try { await Task.Delay(period, cts.Token); } catch (OperationCanceledException) { return; }
            }
        });
    }

    // ───────── 前台挂机 ─────────

    /// <summary>_挂机按钮_被单击：按住右键或 S 键防止挂机被踢；用户亲自按一下对应按键或切走游戏即结束。</summary>
    public void ToggleAfk()
    {
        if (AfkActive) { StopAfk(Lang.C(247)); return; }
        if (!Game.IsRunning()) { Log.Write("挂机未启动：游戏进程未在运行。"); return; }
        Game.BringToFront();
        Thread.Sleep(300);
        if (!Game.IsForeground()) { Log.Write("挂机未启动：无法把游戏切到前台。"); return; }

        var mode = AfkMode;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _afkCts, cts, null) is not null) return;
        const int VkRButton = 0x02, VkS = 0x53;
        if (mode == AfkMode.HoldRightMouse)
        {
            // 原版：先把鼠标移进游戏窗口点一下右键，再按住
            var hwnd = Game.GetWindowHandle();
            if (Native.NativeMethods.GetWindowRect(hwnd, out var rect))
                Native.NativeMethods.SetCursorPos(rect.Left + 100, rect.Top + 100);
            Thread.Sleep(500);
            InputSim.RightDown();
            InputSim.RightUp();
            Thread.Sleep(500);
            InputSim.RightDown();
        }
        else
        {
            InputSim.KeyDown(VkS);
        }
        Log.Write(mode == AfkMode.HoldRightMouse ? Lang.C(251) : Lang.C(246), 1);
        if (AfkPseudoBackground)
        {
            Thread.Sleep(500);
            SendGameWindowToBack();
        }
        Notify();

        Task.Run(async () => // 挂机时钟
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(500, cts.Token); } catch (OperationCanceledException) { return; }
                if (mode == AfkMode.HoldRightMouse && !InputSim.IsDown(VkRButton)) { StopAfk(Lang.C(253)); return; }
                if (mode == AfkMode.HoldS && !InputSim.IsDown(VkS)) { StopAfk(Lang.C(254)); return; }
                if (!Game.IsForeground()) { StopAfk(Lang.C(255)); return; }
            }
        });
    }

    private void StopAfk(string? reason)
    {
        var old = Interlocked.Exchange(ref _afkCts, null);
        if (old is null) return;
        old.Cancel();
        InputSim.RightUp();
        InputSim.KeyUp(0x53);
        if (reason is not null) Log.Write(reason, 1);
        Notify();
    }

    // ───────── 自编宏 ─────────

    /// <summary>把宏库里所有启用的宏的热键注册到热键引擎（先清掉旧的）。</summary>
    public void RegisterMacroHotkeys()
    {
        foreach (var name in _macroHotkeys) Hotkeys.Set(name, Hotkey.None);
        _macroHotkeys.Clear();
        foreach (var macro in Macros.Macros)
        {
            var hotkey = Hotkey.Parse(macro.Hotkey);
            if (!macro.Enabled || !hotkey.IsSet) continue;
            var name = MacroHotkeyPrefix + macro.Id;
            Hotkeys.Set(name, hotkey);
            _macroHotkeys.Add(name);
        }
    }

    private readonly List<string> _macroHotkeys = [];
    private long _lastMacro;

    /// <summary>
    /// 启动宏；正在运行时再触发一次则强制停止（原版「多槽位自编宏执行」）。
    /// bringToFront 用于从界面上点「试运行」：先把游戏切到前台再执行。
    /// </summary>
    public void ToggleMacro(string id, bool bringToFront = false)
    {
        var macro = Macros.Macros.FirstOrDefault(m => m.Id == id);
        if (macro is null) return;
        if (Now - Interlocked.Exchange(ref _lastMacro, Now) < 200) return;

        if (Macros.Stop(id))
        {
            Sound.Play(SoundService.MacroOff);
            Log.Write($"{macro.Name}...{Lang.C(404)}", 1);
            Notify();
            return;
        }
        if (bringToFront && Game.IsRunning())
        {
            Game.BringToFront();
            Thread.Sleep(300);
        }
        if (!MacroAllowed()) { Log.Write($"宏「{macro.Name}」未执行：游戏不在前台。"); return; }

        var started = Macros.Start(macro, MacroAllowed, () =>
        {
            Log.Write($"{macro.Name}...{Lang.C(403)}", 1);
            Sound.Play(SoundService.SoloAutoEnd);
            Notify();
        });
        if (!started) { Log.Write($"宏「{macro.Name}」没有可执行的操作。"); return; }
        Sound.Play(SoundService.MacroOn);
        Log.Write($"{macro.Name}...{Lang.C(402)}", 1);
        Notify();
    }

    // ───────── 简易计时 ─────────

    /// <summary>_计时按钮_被单击</summary>
    public void AdvanceTimer()
    {
        if (!Overlay.TimerEnabled) { Log.Write(Lang.C(240), 1); return; }
        if (Overlay.AdvanceTimer() == TimerState.Running) Sound.Play(11);
        Notify();
    }

    // ───────── 热键分发 ─────────

    /// <summary>热键被触发(热键名称, 按下状态)</summary>
    private void OnHotkey(string name, bool down)
    {
        try
        {
            if (HotkeysOnlyForeground && !Game.IsForeground()) return;
            if (HotkeysOnlyRunning && !GameRunning) return;

            if (name.StartsWith(MacroHotkeyPrefix, StringComparison.Ordinal))
            {
                if (!down) ToggleMacro(name[MacroHotkeyPrefix.Length..]);
                return;
            }

            switch (name)
            {
                case "计时键" when down: AdvanceTimer(); break;
                case "战局甲键" when !down: _ = Bots.JoinSlotAsync("甲"); break;
                case "战局乙键" when !down: _ = Bots.JoinSlotAsync("乙"); break;
                case "杀掉键" when down: Kill(); break;
                case "阻网键" when down: BlockNetwork(); break;
                case "禁卡键" when down: DisableNic(); break;
                case "复网键" when down: RestoreNetwork(); break;
                case "卡单键" when down: ToggleSolo(); break;
                case "挂机键" when !down: ToggleAfk(); break;
                case "悬停键":
                    if (HoverMode == HoverMode.SwitchCharacter) SwitchCharacterHover(down);
                    else if (!down && Now - Interlocked.Exchange(ref _lastHover, Now) >= 400) Hover();
                    break;
                case "粘贴键" when !down: PasteText(); break;
                case "英语解键" when !down: PauseEnglishLock(); break;
                case "指纹键" when !down: Task.Run(() => Fingerprint.Solve()); break;
                case "手柄跑键": OnGamepadRunKey(down); break;
                case "锁帧键" when down: CycleFrameLimit(); break;
                case "骇入键" when !down: AutoHack.Toggle(); break;
                case "连击左键":
                    if (ClickWhileHeld) SetAutoClick(down);
                    else if (down && Now - Interlocked.Exchange(ref _lastClickToggle, Now) > 50) SetAutoClick(!AutoClicking);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"执行热键「{name}」出错：{ex.Message}", 3);
        }
    }
}
