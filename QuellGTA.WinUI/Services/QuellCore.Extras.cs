using System.Net;
using System.Net.Sockets;
using System.Text;
using static QuellGTA.Native.NativeMethods;

namespace QuellGTA.Services;

/// <summary>第三方小工具入口：(名称, 用来识别它的窗口标题, 下载页)。</summary>
public sealed record ExternalTool(string Name, string[] WindowTitles, string DownloadUrl);

/// <summary>
/// QuellCore 的第二部分：挂机防踢、切换角色悬停、粘贴文字、英语输入锁定、云存档提示、使用时长统计、
/// 游戏启动 / 退出时的联动、游戏窗口隐藏与移动、UDP 指令、第三方工具入口。
/// </summary>
public sealed partial class QuellCore
{
    public FingerprintService Fingerprint { get; private set; } = null!;
    public WaitService Waiting { get; private set; } = null!;
    public ProfileService Profiles { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;
    public SelfUpdateService SelfUpdate { get; private set; } = null!;
    public AutoHackService AutoHack { get; private set; } = null!;

    /// <summary>「游戏退出时关闭」触发：请求主窗口退出程序。</summary>
    public event Action? ExitRequested;
    /// <summary>检测到游戏启动 / 退出（后台线程）。</summary>
    public event Action? GameStarted, GameExited;
    public PluginManager Plugins { get; private set; } = null!;

    private void InitializeExtras()
    {
        Fingerprint = new FingerprintService(Settings, Game, Log, Sound);
        Waiting = new WaitService(Settings, this);
        Profiles = new ProfileService(Settings, Game, Files, Log);
        Updates = new UpdateService(Settings, Log, Sound);
        SelfUpdate = new SelfUpdateService(Settings, Log);
        Plugins = new PluginManager(Settings, Log);
        Task.Run(async () => { await Task.Delay(5000); SelfUpdate.CleanUp(); });
        ApplyLogOptions();
        _usageSeconds = Settings.GetInt("统计", "使用时长", 0);
        _gameSeconds = Settings.GetInt("统计", "游戏时长", 0);
        Hotkeys.IgnoreMods = IgnoredModifiers;
        Hotkeys.Engine = HotkeyEngine;
        _englishThread = new Thread(EnglishLockLoop) { IsBackground = true, Name = "QuellGTA.EnglishLock" };
        _englishThread.Start();
        if (UdpEnabled) StartUdp();
        InitializeGamepad();
        InitializeRtss();
        AutoHack = new AutoHackService(Settings, Game, Log);
    }

    private void DisposeExtras()
    {
        _extrasStopping = true;
        Plugins.Dispose(); // 先停插件，它们可能还在用下面这些服务
        SaveStatistics();
        StopUdp();
        Waiting.Stop(silent: true);
        Fingerprint.Dispose();
        DisposeGamepad();
        DisposeRtss();
        AutoHack.Dispose();
    }

    private volatile bool _extrasStopping;

    // ───────── WFP 断网模式 ─────────

    public WfpService Wfp { get; } = new();

    /// <summary>
    /// WFP 模式：阻止联网时不建 Windows 防火墙规则，而是直接向 Windows 筛选平台加过滤器。
    /// 防火墙被关掉的电脑也能用，生效更快。原版通过内嵌的 WFPcon.exe 实现。
    /// </summary>
    public bool WfpMode
    {
        get => Settings.GetBool("本地", "WFP模式", false);
        set => Settings.SetBool("本地", "WFP模式", value);
    }

    private int WfpRuleCount()
    {
        try { return Wfp.Count(); }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// 按「断网」页的设置创建阻止联网的规则。WFP 模式下各种范围对应的规则与原版调用 WFPcon 的参数一致：
    /// 仅 UDP → 协议 17；仅 6672 → 本地端口 6672；仅 TCP → 协议 6；仅 80、443 → 两条远程端口规则；指定服务器 → 每个地址一条。
    /// </summary>
    private void ApplyBlock()
    {
        var options = new BlockOptions(BlockScope, BlockCloudSave, BlockTransaction, BlockJobs, ResolveBlockTarget());
        if (!WfpMode)
        {
            Firewall.Block(options);
            return;
        }
        var path = options.ApplicationPath;
        switch (options.Scope)
        {
            case BlockScope.UdpOnly: Wfp.Block(new WfpRule(path, Protocol: 17)); break;
            case BlockScope.Port6672: Wfp.Block(new WfpRule(path, LocalPort: 6672)); break;
            case BlockScope.TcpOnly: Wfp.Block(new WfpRule(path, Protocol: 6)); break;
            case BlockScope.Ports80443:
                Wfp.Block(new WfpRule(path, RemotePort: 80));
                Wfp.Block(new WfpRule(path, RemotePort: 443));
                break;
            case BlockScope.Servers:
                var addresses = FirewallService.ResolveServers(options, out var everything);
                if (everything || addresses.Count == 0) Wfp.Block(new WfpRule(path));
                else foreach (var address in addresses) Wfp.Block(new WfpRule(path, RemoteAddress: IPAddress.Parse(address)));
                break;
            default: Wfp.Block(new WfpRule(path)); break;
        }
    }

    // ───────── 日志选项 ─────────

    public bool LogDisabled
    {
        get => Settings.GetBool("本地", "禁用日志", false);
        set { Settings.SetBool("本地", "禁用日志", value); ApplyLogOptions(); }
    }
    /// <summary>每天自动存日志：写到数据目录的 Logs 文件夹，一天一个文件。</summary>
    public bool LogDailySave
    {
        get => Settings.GetBool("本地", "自动存日志", false);
        set { Settings.SetBool("本地", "自动存日志", value); ApplyLogOptions(); }
    }

    private void ApplyLogOptions()
    {
        Log.Disabled = LogDisabled;
        Log.DailyDirectory = LogDailySave ? Path.Combine(Settings.DataDirectory, "Logs") : null;
    }

    // ───────── 每秒一次（由游戏状态监视调用） ─────────

    private int _tickCount;

    private void ExtrasTick(bool running)
    {
        _tickCount++;
        _usageSeconds++;
        if (running) _gameSeconds++;
        if (_tickCount % 60 == 0) SaveStatistics();

        if (running)
        {
            AntiAfkTick();
            if (CloudSaveTracking) TrackCloudSave();
            if (_tickCount % 2 == 0) MissionTick();
        }
        else
        {
            _afkSeconds = 0;
        }
        GamepadTick(running);
        if (_tickCount % 5 == 0) HotkeySelfCheck();
    }

    // ───────── 统计：软件使用时长 / 游戏运行时长 ─────────

    private int _usageSeconds, _gameSeconds;
    public TimeSpan UsageTime => TimeSpan.FromSeconds(_usageSeconds);
    public TimeSpan GameTime => TimeSpan.FromSeconds(_gameSeconds);

    /// <summary>需要重启程序时触发（例如换上了另一个游戏版本的配置存档）。</summary>
    public event Action? RestartRequested;

    /// <summary>游戏版本切换之后调用：开了双版本存档时换上目标版本的配置并重启。</summary>
    public void OnEditionChanged(GameEdition before)
    {
        if (Profiles.SwitchEditionProfile(before, Game.Edition, SaveStatistics)) RestartRequested?.Invoke();
    }

    private void SaveStatistics()
    {
        Settings.SetInt("统计", "使用时长", _usageSeconds);
        Settings.SetInt("统计", "游戏时长", _gameSeconds);
    }

    public void ResetTimeStatistics()
    {
        _usageSeconds = _gameSeconds = 0;
        SaveStatistics();
    }

    // ───────── 自动挂机防踢 ─────────

    /// <summary>长时间没有操作时，自动替你按一下键，防止因挂机被踢出战局。</summary>
    public bool AntiAfkEnabled
    {
        get => Settings.GetBool("扩展", "自动挂机", false);
        set => Settings.SetBool("扩展", "自动挂机", value);
    }
    /// <summary>自动挂机超时（秒），原版默认 120。</summary>
    public int AntiAfkTimeout
    {
        get => Math.Clamp(Settings.GetInt("扩展", "自挂机超时", 120), 10, 3600);
        set => Settings.SetInt("扩展", "自挂机超时", Math.Clamp(value, 10, 3600));
    }
    /// <summary>超时后按下的按键，原版默认 Z（可选 S / Z / V 或自定义键码）。</summary>
    public int AntiAfkKey
    {
        get => Math.Clamp(Settings.GetInt("扩展", "自挂机键码", 0x5A), 1, 254);
        set => Settings.SetInt("扩展", "自挂机键码", Math.Clamp(value, 1, 254));
    }
    /// <summary>游戏后台时切换：游戏不在前台时，先把它切到前台再按键。</summary>
    public bool AntiAfkBringToFront
    {
        get => Settings.GetBool("扩展", "挂机后台切换", true);
        set => Settings.SetBool("扩展", "挂机后台切换", value);
    }

    private int _afkSeconds;

    private void AntiAfkTick()
    {
        if (!AntiAfkEnabled) { _afkSeconds = 0; return; }
        var foreground = Game.IsForeground();
        // 游戏在前台时看键鼠有没有动过；在后台时不管你在别处干什么，对游戏来说都是没操作
        if (foreground && InputSim.IdleMilliseconds() < 900) _afkSeconds = 0;
        else _afkSeconds++;
        if (_afkSeconds <= AntiAfkTimeout) return;
        _afkSeconds = 0;

        if (GamepadAfk)
        {
            // 模拟手柄挂机：游戏在后台也收得到手柄输入，只有窗口被最小化时才需要先切回来
            if (AntiAfkBringToFront && GameMinimized())
            {
                Game.BringToFront();
                Thread.Sleep(500);
            }
            GamepadAfkPush();
            return;
        }

        var key = AntiAfkKey;
        if (!foreground)
        {
            if (!AntiAfkBringToFront) { Log.Write(Lang.C(524)); return; }
            Game.BringToFront();
            Thread.Sleep(500);
            if (!Game.IsForeground()) { Log.Write(Lang.C(524)); return; }
        }
        InputSim.KeyPress(key, 60);
        Log.Write(Lang.C(196) + Hotkey.KeyName(key));
    }

    // ───────── 切换角色悬停 ─────────

    /// <summary>
    /// 「宏内角键」：游戏里「切换到某个故事模式角色」的按键，原版默认 F6。
    /// </summary>
    public int SwitchCharacterKey
    {
        get => Math.Clamp(Settings.GetInt("本地", "宏内角键", 0x75), 1, 254);
        set => Settings.SetInt("本地", "宏内角键", Math.Clamp(value, 1, 254));
    }

    private long _switchStart;

    /// <summary>
    /// 切换角色悬停：按住悬停键时替你按住切换角色键，松开时（至少按满 625 毫秒）松开它并按 Esc 取消。
    /// 对应原版「自动悬停start / 自动悬停stop」。
    /// </summary>
    private void SwitchCharacterHover(bool down)
    {
        if (!MacroAllowed()) return;
        var key = SwitchCharacterKey;
        if (down)
        {
            if (Now - _lastHover < 400) return;
            _lastHover = Now + 650;
            InputSim.KeyDown(key);
            Sound.Play(SoundService.Hover);
            _switchStart = Now;
            return;
        }
        var remaining = 625 - (Now - _switchStart);
        if (remaining > 0) Thread.Sleep((int)remaining);
        InputSim.KeyUp(key);
        Thread.Sleep(100);
        InputSim.KeyPress(0x1B /* Esc */, 100);
        Log.Write(Lang.C(67), 1);
    }

    // ───────── 粘贴文字 ─────────

    /// <summary>粘贴文字：游戏的聊天框不支持 Ctrl+V，用热键把剪贴板里的文字打进去。</summary>
    public bool PasteEnabled
    {
        get => Settings.GetBool("本地", "使能粘贴", false);
        set => Settings.SetBool("本地", "使能粘贴", value);
    }

    private int _pasting;

    public void PasteText()
    {
        if (!PasteEnabled) { Log.Write(Lang.C(258)); return; }
        if (!GameRunning || !Game.IsForeground()) return;
        if (Interlocked.Exchange(ref _pasting, 1) == 1) { Log.Write(Lang.C(257)); return; }
        try
        {
            var text = InputSim.GetClipboardText();
            if (text.Length > 256) text = text[..256];
            if (Game.DocumentsDirName == "GTA V")
            {
                // 传承版：直接投递字符，中文也能输入
                InputSim.PostText(Game.GetWindowHandle(), text);
            }
            else
            {
                // 增强版只认真实的按键，所以只能打出键盘上有的字符
                InputSim.TypeText(text);
            }
            Log.Write(Lang.C(259), 1);
        }
        finally { Interlocked.Exchange(ref _pasting, 0); }
    }

    // ───────── 英语输入锁定 ─────────

    /// <summary>
    /// 输入锁定：定时让游戏窗口切回英文输入法，避免中文输入法把游戏按键吃掉。
    /// 要在聊天框里打中文时按「英语解键」，锁定会暂停到你按下回车或 Esc 为止。
    /// </summary>
    public bool EnglishLock
    {
        get => Settings.GetBool("本地", "英语锁定", false);
        set => Settings.SetBool("本地", "英语锁定", value);
    }
    /// <summary>切换频率（每秒几次）：1 / 2 / 5 / 10 / 20，原版默认 5。</summary>
    public int EnglishLockRate
    {
        get
        {
            var rate = Settings.GetInt("本地", "英语频率", 5);
            return rate is 1 or 2 or 5 or 10 or 20 ? rate : 5;
        }
        set => Settings.SetInt("本地", "英语频率", value);
    }

    private Thread? _englishThread;
    private volatile bool _englishPaused;

    public void PauseEnglishLock()
    {
        if (EnglishLock) _englishPaused = true;
    }

    private void EnglishLockLoop()
    {
        const int VkReturn = 0x0D, VkEscape = 0x1B;
        const int EnglishUs = 0x04090409;
        while (!_extrasStopping)
        {
            try
            {
                if (!EnglishLock)
                {
                    _englishPaused = false;
                    Thread.Sleep(500);
                    continue;
                }
                if (_englishPaused)
                {
                    // 暂停期间高频检查，回车或 Esc（发出或取消聊天）之后恢复锁定
                    if (InputSim.IsDown(VkReturn) || InputSim.IsDown(VkEscape)) _englishPaused = false;
                    Thread.Sleep(40);
                    continue;
                }
                if (GameRunning)
                {
                    var hwnd = Game.GetWindowHandle();
                    if (hwnd != 0) PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, 1, EnglishUs);
                }
                Thread.Sleep(1000 / EnglishLockRate);
            }
            catch (Exception) { Thread.Sleep(500); }
        }
    }

    // ───────── 查云存档 ─────────

    /// <summary>保存成功时记录日志。</summary>
    public bool CloudSaveLog
    {
        get => Settings.GetBool("扩展", "云存时提醒", false);
        set => Settings.SetBool("扩展", "云存时提醒", value);
    }
    /// <summary>保存成功时播放声音（需同时开启记录日志）。</summary>
    public bool CloudSaveSound
    {
        get => Settings.GetBool("扩展", "云存时声音", false);
        set => Settings.SetBool("扩展", "云存时声音", value);
    }
    /// <summary>操作时提示保存成功间隔：杀进程 / 断网时，如果 10 秒内刚存过档就在日志里提示。</summary>
    public bool CloudSaveInterval
    {
        get => Settings.GetBool("扩展", "云存间隔提示", false);
        set => Settings.SetBool("扩展", "云存间隔提示", value);
    }
    /// <summary>罪神时评估进度保留情况：联机状态下杀进程时，如果 10 秒内刚存过档就弹出警告。</summary>
    public bool CloudSaveHeistWarning
    {
        get => Settings.GetBool("扩展", "罪神评估", false);
        set => Settings.SetBool("扩展", "罪神评估", value);
    }

    private bool CloudSaveTracking => CloudSaveLog || CloudSaveInterval || CloudSaveHeistWarning;

    private string _cloudFile = "", _cloudStamp = "";
    private long _lastCloudSave; // Unix 秒，0 表示还没观察到

    public DateTimeOffset? LastCloudSave => _lastCloudSave == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(_lastCloudSave);

    /// <summary>
    /// 记录云存档：pc_settings.bin 里 931 / 932 两项合起来是「上一次线上云存档成功」的时间戳
    /// （MP_CLOUD_POSIXTIME_HIGH32 / LOW32）。它变了就说明游戏刚刚成功存了一次档。
    /// </summary>
    private void TrackCloudSave()
    {
        var profile = Files.LatestProfile();
        if (profile.Length == 0) return;
        var file = Path.Combine(Files.ProfilesDir, profile, "pc_settings.bin");
        if (file != _cloudFile)
        {
            _cloudFile = file;
            _cloudStamp = "";
        }
        var stamp = GameFilesService.ReadCloudStamp(file);
        if (stamp is null) return;
        if (_cloudStamp.Length > 0 && stamp != _cloudStamp)
        {
            _lastCloudSave = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (CloudSaveLog)
            {
                Log.Write(Lang.C(470));
                if (CloudSaveSound) Sound.Play(21);
            }
        }
        _cloudStamp = stamp;
    }

    /// <summary>提示上次云存档：在杀进程 / 断网之后调用。</summary>
    private void ReportCloudSave(bool isKill)
    {
        if (_lastCloudSave == 0 || !CloudSaveTracking) return;
        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastCloudSave;
        var online = Team.Mode is TeamMode.Client or TeamMode.Host;
        if (CloudSaveHeistWarning && online && isKill && elapsed < 10)
        {
            // 刚存完档就杀进程：罪神进度很可能已经被云端记下来了
            Log.Write($"{Lang.C(468)}{elapsed} s。{Lang.C(469)}", 3);
            return;
        }
        if (CloudSaveInterval && elapsed < 10) Log.Write($"{Lang.C(468)}{elapsed} s");
        else if (CloudSaveHeistWarning && online && isKill) Log.Write($"{Lang.C(468)}{elapsed} s");
    }

    // ───────── 游戏启动 / 退出时的联动 ─────────

    public bool KillLauncherOnGameStart
    {
        get => Settings.GetBool("扩展", "启动时杀启动器", false);
        set => Settings.SetBool("扩展", "启动时杀启动器", value);
    }
    public bool KillLauncherOnGameExit
    {
        get => Settings.GetBool("扩展", "退出时杀启动器", false);
        set => Settings.SetBool("扩展", "退出时杀启动器", value);
    }
    /// <summary>游戏退出时关闭本程序。</summary>
    public bool ExitWithGame
    {
        get => Settings.GetBool("扩展", "随游戏退出", false);
        set => Settings.SetBool("扩展", "随游戏退出", value);
    }

    /// <summary>游戏启动事件</summary>
    private void OnGameStarted()
    {
        Sound.Play(13);
        Thread.Sleep(500); // 等游戏启动完全
        if (KillLauncherOnGameStart) KillLauncher(LauncherDelayMs);
        foreach (var tool in Tools)
            if (GetToolAutoRun(tool) && FindToolWindow(tool) == 0) RunTool(tool, quiet: true);
        _gameWindowHidden = false;
        try { GameStarted?.Invoke(); } catch (Exception ex) { Log.Write("插件处理游戏启动事件出错：" + ex.Message, 2); }
    }

    /// <summary>游戏退出事件</summary>
    private void OnGameExited()
    {
        Thread.Sleep(500); // 等游戏的附属进程退出
        SaveStatistics();
        if (KillLauncherOnGameExit) KillLauncher(LauncherDelayMs);
        try { GameExited?.Invoke(); } catch (Exception ex) { Log.Write("插件处理游戏退出事件出错：" + ex.Message, 2); }
        if (ExitWithGame) ExitRequested?.Invoke();
    }

    // ───────── 游戏窗口：隐藏、移动 ─────────

    private bool _gameWindowHidden;
    public bool GameWindowHidden => _gameWindowHidden;

    /// <summary>窗口隐藏：把游戏窗口藏起来（游戏照常运行），再调一次显示回来。</summary>
    public bool SetGameWindowHidden(bool hidden)
    {
        var hwnd = hidden ? Game.GetWindowHandle() : _hiddenWindow;
        if (hwnd == 0) return false;
        if (hidden) _hiddenWindow = hwnd; // 隐藏之后按「可见窗口」找不到它，所以先记下句柄
        ShowWindowAsync(hwnd, hidden ? 0 /* SW_HIDE */ : 5 /* SW_SHOW */);
        _gameWindowHidden = hidden;
        Log.Write(hidden ? "已隐藏游戏窗口。" : "已显示游戏窗口。", 1);
        Notify();
        return true;
    }

    private nint _hiddenWindow;

    /// <summary>窗口移动：把游戏窗口移到指定位置，大小不变。horizontal / vertical 取 -1（左 / 上）、0（不变）、1（右 / 下）。</summary>
    public bool MoveGameWindow(int horizontal, int vertical)
    {
        var hwnd = Game.GetWindowHandle();
        if (hwnd == 0 || !GetWindowRect(hwnd, out var rect)) return false;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var x = horizontal < 0 ? 0 : horizontal > 0 ? GetSystemMetrics(0) - width : rect.Left;
        var y = vertical < 0 ? 0 : vertical > 0 ? GetSystemMetrics(1) - height : rect.Top;
        return MoveWindow(hwnd, x, y, width, height, true);
    }

    public bool MoveGameWindowTo(int x, int y)
    {
        var hwnd = Game.GetWindowHandle();
        if (hwnd == 0 || !GetWindowRect(hwnd, out var rect)) return false;
        return MoveWindow(hwnd, x, y, rect.Right - rect.Left, rect.Bottom - rect.Top, true);
    }

    // ───────── 热键引擎选项 ─────────

    /// <summary>组合键忽略：勾选后，判断热键时不看对应的修饰键有没有按着（原版「禁用 Alt / Ctrl / Shift 检测」）。</summary>
    public HotkeyMods IgnoredModifiers
    {
        get => (HotkeyMods)Settings.GetInt("本地", "忽略组合键", 0);
        set
        {
            Settings.SetInt("本地", "忽略组合键", (int)value);
            Hotkeys.IgnoreMods = value;
        }
    }
    /// <summary>
    /// 热键引擎：钩子（原版「代理」）、轮询、原始输入。原版用「轮询热键」「原始热键」两个开关保存，这里沿用。
    /// </summary>
    public HotkeyEngine HotkeyEngine
    {
        get => Settings.GetBool("本地", "原始热键", false) ? HotkeyEngine.RawInput
            : Settings.GetBool("本地", "轮询热键", false) ? HotkeyEngine.Polling : HotkeyEngine.Hook;
        set
        {
            Settings.SetBool("本地", "原始热键", value == HotkeyEngine.RawInput);
            Settings.SetBool("本地", "轮询热键", value == HotkeyEngine.Polling);
            Hotkeys.Engine = value;
            Hotkeys.Restart();
        }
    }
    /// <summary>热键引擎有效性自检（原版默认开启）。</summary>
    public bool HotkeySelfCheckEnabled
    {
        get => Settings.GetBool("本地", "热键自检", true);
        set => Settings.SetBool("本地", "热键自检", value);
    }

    private int _hookSilentChecks;

    /// <summary>
    /// 自检：系统显示刚有键鼠输入，钩子却一直没收到任何事件，说明钩子已经被系统摘掉了（常见于钩子回调超时），自动重装。
    /// </summary>
    private void HotkeySelfCheck()
    {
        if (!HotkeySelfCheckEnabled || Hotkeys.Engine == HotkeyEngine.Polling || !Hotkeys.IsRunning) return;
        var recentInput = InputSim.IdleMilliseconds() < 3000;
        var hookQuiet = Environment.TickCount64 - Hotkeys.LastEventTick > 15000;
        _hookSilentChecks = recentInput && hookQuiet ? _hookSilentChecks + 1 : 0;
        if (_hookSilentChecks < 3) return;
        _hookSilentChecks = 0;
        Hotkeys.Restart();
        Log.Write("热键引擎自检：钩子没有响应，已自动重启。", 1);
    }

    // ───────── UDP 指令 ─────────

    /// <summary>
    /// UDP 指令：让其他程序通过 UDP 数据报触发操作（原版的「UDP 触发程序」插件就是这么用的）。
    /// 指令：kill fwon fwoff single bancard paste timerst timerzero timerstart timerstop joinbot1 joinbot2 winmini。
    /// 与原版不同：只监听本机回环地址，局域网里的其他设备发不进来。
    /// </summary>
    public bool UdpEnabled
    {
        get => Settings.GetBool("本地", "UDP指令", false);
        set
        {
            Settings.SetBool("本地", "UDP指令", value);
            StopUdp();
            if (value) StartUdp();
        }
    }
    public int UdpPort
    {
        get => Math.Clamp(Settings.GetInt("本地", "UDP端口", 55378), 1, 65534);
        set => Settings.SetInt("本地", "UDP端口", Math.Clamp(value, 1, 65534));
    }

    private UdpClient? _udp;
    public bool UdpListening => _udp is not null;

    private void StartUdp()
    {
        try
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, UdpPort));
            _udp = client;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    UdpReceiveResult result;
                    try { result = await client.ReceiveAsync(); }
                    catch (Exception) { return; }
                    // 触发程序发的是自己的文件名，Windows 的文件名不分大小写（Kill.exe 也算 kill）
                    var command = Encoding.ASCII.GetString(result.Buffer).Trim().ToLowerInvariant();
                    _ = Task.Run(() => RunCommand(command));
                }
            });
            Log.Write($"UDP 指令已开启，监听 127.0.0.1:{UdpPort}。", 1);
        }
        catch (Exception ex)
        {
            _udp = null;
            Log.Write($"UDP 指令开启失败（端口 {UdpPort} 可能被占用）：{ex.Message}", 3);
        }
    }

    private void StopUdp()
    {
        var client = Interlocked.Exchange(ref _udp, null);
        try { client?.Close(); } catch (Exception) { }
    }

    /// <summary>执行一条外部指令（UDP 或命令行）。返回是否认识这条指令。</summary>
    public bool RunCommand(string command)
    {
        switch (command)
        {
            case "kill": Kill(); break;
            case "fwon": BlockNetwork(); break;
            case "fwoff": RestoreNetwork(); break;
            case "single": ToggleSolo(); break;
            case "bancard": DisableNic(); break;
            case "paste": PasteText(); break;
            case "timerst": AdvanceTimer(); break;
            case "timerzero": Overlay.ResetTimer(); Notify(); break;
            case "timerstart": Overlay.StartTimer(); Sound.Play(11); Notify(); break;
            case "timerstop": Overlay.StopTimer(); Notify(); break;
            case "joinbot1": _ = Bots.JoinSlotAsync("甲"); break;
            case "joinbot2": _ = Bots.JoinSlotAsync("乙"); break;
            case "winmini": MinimizeGame(); break;
            default: return false;
        }
        return true;
    }

    /// <summary>
    /// 切出游戏（winmini 指令）：把游戏窗口最小化，焦点交给任务栏。出自原版较早的版本（_切出按钮_被单击 的模式 3），
    /// Build 0216 的界面上已经没有这个按钮，但 UDP 触发程序仍然带着这条指令。
    /// </summary>
    public void MinimizeGame()
    {
        var hwnd = Game.GetWindowHandle();
        if (hwnd == 0) { Log.Write(Lang.C(157)); return; }
        Native.NativeMethods.ShowWindow(hwnd, 6 /* SW_MINIMIZE */);
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar != 0) Native.NativeMethods.SetForegroundWindow(taskbar);
        Log.Write("已切出游戏。", 1);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string? title);

    // ───────── 第三方工具入口 ─────────

    public static readonly ExternalTool[] Tools =
    [
        new("GTAOL按键宏", ["GTAOL按键宏"], "https://onelymaker.lanzoue.com/b00n6ejle"),
        new("穿越激光", ["CrossLaser"], "https://lanzouq.com/s/CrossLazer"),
        new("战局网管", ["GTA 在线模式 & Red Dead 在线模式 战局管理工具"], "https://github.com/FlowingWhiteHairMatchstickMan/GTAO_PC_NetworkChecker/releases/latest"),
        new("精英速通工具", ["Elite Speedrun Tool", "精英速通工具"], "https://github.com/GTA5Oracle/EliteSpeedrunTool/releases/latest"),
        new("快捷差传", ["快捷差传"], "https://lxcoo.lanzouv.com/b0j0dycwb"),
        new("MikiGTA5盒子", ["MikiGTAOL线上多功能盒子"], "https://mod.3dmgame.com/mod/181690?for=download"),
        new("多合一开锁助手", ["GTA开锁助手"], "https://github.com/laughingman56/GTAOL-TOOL"),
    ];

    public string GetToolPath(ExternalTool tool) => Settings.Get("外部", tool.Name, "");
    public bool GetToolAutoRun(ExternalTool tool) => Settings.GetBool("外部", tool.Name + "自启", false);
    public void SetToolAutoRun(ExternalTool tool, bool value) => Settings.SetBool("外部", tool.Name + "自启", value);

    private static nint FindToolWindow(ExternalTool tool)
    {
        nint found = 0;
        var buffer = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            buffer.Clear();
            GetWindowTextW(hwnd, buffer, buffer.Capacity);
            var title = buffer.ToString();
            if (!tool.WindowTitles.Any(t => title == t)) return true;
            found = hwnd;
            return false;
        }, 0);
        return found;
    }

    /// <summary>
    /// 运行工具：已经开着就切过去并记住它的位置；没开但记得位置就启动；都没有就打开下载页。
    /// 返回给界面看的说明。
    /// </summary>
    public string RunTool(ExternalTool tool, bool quiet = false)
    {
        var hwnd = FindToolWindow(tool);
        if (hwnd != 0)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var path = GameService.GetProcessPath((int)pid);
            if (path.Length > 0 && path != GetToolPath(tool)) Settings.Set("外部", tool.Name, path);
            if (!quiet) SetForegroundWindow(hwnd);
            return "已经在运行。";
        }
        var saved = GetToolPath(tool);
        if (saved.Length > 0 && File.Exists(saved))
        {
            Shell.Run("explorer.exe", $"\"{saved}\"", wait: false); // 经资源管理器启动，不让它继承管理员权限
            Log.Write(Lang.C(83) + tool.Name, 1);
            return "已启动。";
        }
        if (quiet) return "";
        Shell.Open(tool.DownloadUrl);
        return "还不知道它装在哪，已打开下载页。下载并运行一次之后，再点这里就会记住它的位置。";
    }
}
