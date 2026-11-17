using static QuellGTA.Native.NativeMethods;

namespace QuellGTA.Services;

/// <summary>
/// QuellCore 的第三部分：虚拟手柄（后台挂机、手柄偷速）、进程守护、命令行参数。
/// </summary>
public sealed partial class QuellCore
{
    public GamepadService Gamepad { get; private set; } = null!;
    public GuardService Guard { get; private set; } = null!;

    private void InitializeGamepad()
    {
        Gamepad = new GamepadService(Settings, Log);
        Guard = new GuardService(Settings, Log);
        SyncGamepad();
    }

    private void DisposeGamepad()
    {
        if (_runActive && GamepadRunToggle && GetHotkey("手柄跑键").Vk != 0x10) InputSim.KeyUp(0x10);
        Gamepad.Dispose();
        Guard.Dispose();
    }

    // ───────── 虚拟手柄：设置 ─────────

    /// <summary>模拟手柄挂机（真后台）：自动挂机防踢超时后不按键盘，而是推一下虚拟手柄的左摇杆。游戏在后台也有效。</summary>
    public bool GamepadAfk
    {
        get => Settings.GetBool("扩展", "推摇杆挂机", false);
        set { Settings.SetBool("扩展", "推摇杆挂机", value); SyncGamepad(); }
    }
    /// <summary>挂机时推的方向，GamepadService.Directions 的下标，原版默认「下」。</summary>
    public int GamepadAfkDirection
    {
        get => Math.Max(0, Array.IndexOf(GamepadService.Directions, Settings.Get("扩展", "推摇杆方向", "down")));
        set => Settings.Set("扩展", "推摇杆方向", GamepadService.Directions[Math.Clamp(value, 0, GamepadService.Directions.Length - 1)]);
    }
    /// <summary>每次推多久（毫秒），原版默认 1000。</summary>
    public int GamepadAfkDuration
    {
        get => Settings.GetInt("扩展", "推摇杆时间", 1000) is var v and >= 10 and <= 60000 ? v : 1000;
        set => Settings.SetInt("扩展", "推摇杆时间", Math.Clamp(value, 10, 60000));
    }
    /// <summary>推多深（0–255），原版默认 255。</summary>
    public int GamepadAfkStroke
    {
        get => Settings.GetInt("扩展", "推摇杆行程", 255) is var v and >= 0 and <= 255 ? v : 255;
        set => Settings.SetInt("扩展", "推摇杆行程", Math.Clamp(value, 0, 255));
    }
    /// <summary>手柄偷速：按触发键时把左摇杆往前推到约 88%，配合奔跑键实现偷速跑。</summary>
    public bool GamepadRun
    {
        get => Settings.GetBool("扩展", "推摇杆跑启用", false);
        set
        {
            Settings.SetBool("扩展", "推摇杆跑启用", value);
            if (!value) StopGamepadRun();
            SyncGamepad();
        }
    }
    /// <summary>偷速时摇杆的行程（0–255），原版默认 224。</summary>
    public int GamepadRunStroke
    {
        get => Math.Clamp(Settings.GetInt("扩展", "推摇杆跑行程", 224), 0, 255);
        set => Settings.SetInt("扩展", "推摇杆跑行程", Math.Clamp(value, 0, 255));
    }
    /// <summary>真 = 按一下开、再按一下关（原版默认）；假 = 按住时生效。</summary>
    public bool GamepadRunToggle
    {
        get => Settings.Get("扩展", "推摇杆跑模式", "2") == "2";
        set
        {
            StopGamepadRun();
            Settings.Set("扩展", "推摇杆跑模式", value ? "2" : "1");
        }
    }
    public bool GamepadRunSound
    {
        get => Settings.GetBool("扩展", "推摇杆跑声音", true);
        set => Settings.SetBool("扩展", "推摇杆跑声音", value);
    }
    /// <summary>
    /// 按需创建：只在要推摇杆的那一刻插上虚拟手柄，推完就拔掉。
    /// 虚拟手柄一直插着时，游戏会把按键提示换成手柄图标；偷速需要随按随有，所以开了偷速时原版默认不按需创建。
    /// </summary>
    public bool GamepadOnDemand
    {
        get => Settings.GetBool("扩展", "推摇杆按需建", !GamepadRun);
        set { Settings.SetBool("扩展", "推摇杆按需建", value); SyncGamepad(); }
    }

    // ───────── 虚拟手柄：操作 ─────────

    private readonly object _padGate = new();
    private Task _padQueue = Task.CompletedTask;
    private int _padAfkBusy;
    private bool _runKeyDown;
    private volatile bool _runActive;

    public bool GamepadRunActive => _runActive;

    /// <summary>手柄操作排队执行（原版的「手柄待操作」队列），保证「推」和「归中」不会乱序。</summary>
    private void PadEnqueue(Action action)
    {
        lock (_padGate)
            _padQueue = _padQueue.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) { Log.Write("模拟手柄：" + ex.Message, 2); }
            }, TaskScheduler.Default);
    }

    /// <summary>让虚拟手柄的插拔状态和设置一致：不是按需创建、且有功能在用时一直插着，否则拔掉。</summary>
    private void SyncGamepad()
    {
        var keep = !GamepadOnDemand && (GamepadRun || GamepadAfk);
        PadEnqueue(() =>
        {
            if (keep) Gamepad.Connect();
            else if (!_runActive) Gamepad.Disconnect();
        });
    }

    /// <summary>「尝试修复手柄无效」：关掉两个手柄功能并立即拔掉虚拟手柄，重启游戏后它就能重新认出真手柄。</summary>
    public void RemoveGamepad()
    {
        StopGamepadRun();
        Settings.SetBool("扩展", "推摇杆挂机", false);
        Settings.SetBool("扩展", "推摇杆跑启用", false);
        PadEnqueue(Gamepad.Disconnect);
    }

    /// <summary>自动挂机防踢超时后调用：推一下摇杆再归中（原版「JoySim_手柄挂机」）。</summary>
    private void GamepadAfkPush()
    {
        if (Interlocked.Exchange(ref _padAfkBusy, 1) == 1) return;
        var direction = GamepadAfkDirection;
        Log.Write(Lang.C(196) + GamepadService.DirectionArrows[direction]);
        PadEnqueue(() =>
        {
            try
            {
                if (!Gamepad.Push(GamepadService.Directions[direction], GamepadAfkStroke))
                {
                    // 驱动没装或手柄创建失败：关掉这个选项，下次超时改回按键盘
                    Settings.SetBool("扩展", "推摇杆挂机", false);
                    Log.Write("模拟手柄挂机已关闭，之后改为按键盘按键。需要先安装 ViGEmBus 驱动。", 2);
                    return;
                }
                Thread.Sleep(GamepadAfkDuration);
                if (_runActive) return; // 偷速正推着摇杆，别给它归中
                Gamepad.Center();
                if (GamepadOnDemand) Gamepad.Disconnect();
            }
            finally { Volatile.Write(ref _padAfkBusy, 0); }
        });
    }

    /// <summary>手柄偷速的触发键按下 / 松开。</summary>
    private void OnGamepadRunKey(bool down)
    {
        if (!GamepadRun || _runKeyDown == down) return;
        _runKeyDown = down;
        if (GamepadRunToggle)
        {
            if (down) SetGamepadRun(!_runActive);
        }
        else SetGamepadRun(down);
    }

    /// <summary>原版「JoySim_手柄偷速」。</summary>
    private void SetGamepadRun(bool start)
    {
        // 切换模式下替你按住 Shift（奔跑键）；触发键本身就是 Shift 时不能这么做，会干扰热键识别
        var holdShift = GamepadRunToggle && GetHotkey("手柄跑键").Vk != 0x10;
        if (start && MacroAllowed())
        {
            _runActive = true;
            if (GamepadRunSound) Sound.Play(22);
            if (holdShift) InputSim.KeyDown(0x10);
            var stroke = GamepadRunStroke;
            PadEnqueue(() =>
            {
                if (Gamepad.Push("up", stroke)) return;
                _runActive = false;
                if (holdShift) InputSim.KeyUp(0x10);
            });
        }
        else
        {
            if (!_runActive && start) return; // 游戏不在前台，没启动成功，也就不用收尾
            _runActive = false;
            if (GamepadRunSound) Sound.Play(23);
            if (holdShift) InputSim.KeyUp(0x10);
            PadEnqueue(() =>
            {
                Gamepad.Center();
                if (GamepadOnDemand) Gamepad.Disconnect();
            });
        }
        Notify();
    }

    private void StopGamepadRun()
    {
        if (_runActive) SetGamepadRun(false);
        _runKeyDown = false;
    }

    /// <summary>每秒一次：游戏退出了就把偷速停掉。</summary>
    private void GamepadTick(bool running)
    {
        if (_runActive && !running) StopGamepadRun();
    }

    private bool GameMinimized() => Game.GetWindowHandle() is var hwnd and not 0 && IsIconic(hwnd);

    // ───────── 命令行参数 / 意外退出后的恢复 ─────────

    /// <summary>
    /// 处理启动参数（原版「处理命令行」）：server = 启动后创建房间，connect = 启动后连接上次的房间，
    /// recover / recovered = 恢复上次的状态。show 由主窗口处理。
    /// 数据目录下的 commandline.txt 每行一条，效果相同；里面写 delcmdtxt 表示用完删除这个文件。
    /// </summary>
    public void HandleCommandLine(IEnumerable<string> arguments)
    {
        var commands = arguments.Select(a => a.Trim().ToLowerInvariant()).ToList();
        try
        {
            var file = Path.Combine(Settings.DataDirectory, "commandline.txt");
            if (File.Exists(file))
            {
                var lines = File.ReadAllLines(file).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).ToList();
                commands.AddRange(lines.Where(l => l is "show" or "server" or "connect"));
                if (lines.Contains("delcmdtxt")) File.Delete(file);
            }
        }
        catch (Exception) { }

        ShowRequested = commands.Contains("show");
        var recovered = commands.Contains(GuardService.RecoveredArgument);
        if (recovered)
        {
            // 是守护进程把我们重新拉起来的：上一次是崩溃或被别的软件结束
            if (Guard.ShowWarning) Sound.Play(10);
            Log.Write(Lang.C(472), Guard.ShowWarning ? 2 : 0);
        }
        var recover = commands.Contains("recover") || (recovered && Guard.AutoRecover);
        var savedMode = (TeamMode)Settings.GetInt("状态", "联机状态", 0);
        if (recover) Log.Write(Lang.C(539), Guard.ShowWarning ? 2 : 0);

        // 断网 / 禁卡状态在启动时总是会接着显示（见构造函数），这里只需要处理联机
        var host = commands.Contains("server") || (recover && savedMode == TeamMode.Host);
        var join = !host && (commands.Contains("connect") || (recover && savedMode is TeamMode.Client or TeamMode.Connecting));
        if (!host && !join) return;
        Task.Run(() =>
        {
            Thread.Sleep(1000);
            if (host) Team.StartHost();
            else if (Team.Address.Length > 0 && Team.Join(Team.Address) is { } error) Log.Write(error, 1);
        });
    }

    /// <summary>启动参数里带了 show：即使设置了「启动之后最小化」也把窗口显示出来。</summary>
    public bool ShowRequested { get; private set; }
}
