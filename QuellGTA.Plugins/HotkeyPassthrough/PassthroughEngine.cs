using System.Runtime.InteropServices;

namespace QuellGTA.Plugin.HotkeyPassthrough;

/// <summary>
/// 热键穿透的执行部分：一个底层键盘钩子 + 一个用来抢前台的小窗口，放在同一个独立线程的消息循环里。
///
/// 为什么自己装钩子而不用主程序的 Hotkeys.Register：
/// - Register 的按键由用户在「全部快捷键」里设，插件只能给一个固定的名字、不能指定按键，
///   而这里的列表是用户随时增删的任意多个组合键；为每一项注册一个快捷键再让用户重复设一遍按键，既别扭也做不到和 HKsavior.ini 对应。
/// - 主程序的快捷键会做冲突检查（同一组合键只能给一项），而要穿透的恰恰常常是别的程序已经占用的组合键；
///   还受「仅游戏前台」等全局开关影响，主程序切到轮询 / 原始输入引擎时也分不清模拟按键。
/// - 底层钩子只是旁听（不吃掉按键），和主程序的钩子并存没有问题；放在独立线程上，界面卡顿不会让钩子超时被系统摘掉。
///
/// 重发的按键带 dwExtraInfo = 78（主程序的 InjectedTag）：主程序自己的热键钩子会忽略它，
/// 本钩子也忽略它，所以重发出去的组合键既不会触发主程序的快捷键，也不会再次触发穿透。
/// </summary>
internal sealed class PassthroughEngine : IDisposable
{
    /// <summary>和主程序 NativeMethods.InjectedTag 相同。</summary>
    private const int InjectedTag = 78;

    private const int WM_APP_ACTIVATE = 0x8001, WM_APP_RESTORE = 0x8002, WM_APP_REHOOK = 0x8003;

    private readonly ComboMatcher _matcher = new();
    private readonly Func<bool> _isGameForeground;
    private readonly Func<nint> _gameWindow;
    private readonly Action<string, bool> _log;

    private Thread? _thread;
    private uint _threadId;
    private nint _hook, _window;
    private string? _className;
    private HookProc? _hookProc;   // 委托要一直活着，否则被 GC 回收后系统回调会崩
    private WndProc? _wndProc;
    private int _busy;
    private long _lastEvent = Environment.TickCount64, _lastRehook;

    /// <param name="log">写日志，第二个参数为 true 表示警告。</param>
    public PassthroughEngine(Func<bool> isGameForeground, Func<nint> gameWindow, Action<string, bool> log)
    {
        _isGameForeground = isGameForeground;
        _gameWindow = gameWindow;
        _log = log;
    }

    /// <summary>切到自己的窗口后等多久再重发（原版固定 300 毫秒）。</summary>
    public int SwitchDelay { get; set; } = 300;

    public bool IsRunning => _thread is not null;

    public void SetCombos(IEnumerable<Combo> combos) => _matcher.Combos = combos.ToArray();

    public void Start()
    {
        if (_thread is not null) return;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "QuellGTA.HotkeyPassthrough", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Stop()
    {
        if (_thread is null) return;
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(2000);
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// 钩子自检，对应原版的「时钟1」：原版每秒模拟一个 255 键看钩子还收不收得到，收不到就重装。
    /// 这里不再往系统里塞假按键，而是比较「最近一次真实输入」和「钩子最近一次收到事件」：
    /// 用户明明在操作、钩子却很久没动静，就重装一次。只动鼠标时也会误判，所以至少隔 30 秒才重装一次，重装本身没有副作用。
    /// </summary>
    public void CheckHook(long idleMilliseconds)
    {
        if (_thread is null) return;
        var now = Environment.TickCount64;
        if (idleMilliseconds < 3000 && now - Interlocked.Read(ref _lastEvent) > 10000 && now - _lastRehook > 30000) Rehook();
    }

    /// <summary>重装钩子（游戏启动时也调一次：有些游戏 / 反作弊启动时会让别人的底层钩子失效）。</summary>
    public void Rehook()
    {
        if (_thread is null) return;
        _lastRehook = Environment.TickCount64;
        PostThreadMessageW(_threadId, WM_APP_REHOOK, 0, 0);
    }

    // ───────── 线程：钩子 + 小窗口 ─────────

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandleW(null);
        try
        {
            _hookProc = HookCallback;
            _wndProc = WindowProc;
            CreateHelperWindow(module);
            InstallHook(module);
            ready.Set();
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.hwnd == 0 && msg.message == WM_APP_REHOOK)
                {
                    InstallHook(module);
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        finally
        {
            if (_hook != 0) UnhookWindowsHookEx(_hook);
            _hook = 0;
            if (_window != 0) DestroyWindow(_window);
            _window = 0;
            // 插件可能被卸载后重新加载，类名不注销的话下次会拿到指向旧委托的窗口过程
            if (_className is not null) UnregisterClassW(_className, module);
            _className = null;
            ready.Set();
        }
    }

    private void InstallHook(nint module)
    {
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        _matcher.Reset();
        Interlocked.Exchange(ref _lastEvent, Environment.TickCount64);
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc!, module, 0);
        if (_hook == 0) _log($"安装键盘钩子失败（错误 {Marshal.GetLastWin32Error()}）", true);
    }

    /// <summary>
    /// 对应原版的「窗口1」：一个 0×0、不显示的窗口，触发时临时置顶并激活。
    /// 这里用 1×1 的工具窗口（不出现在任务栏和 Alt+Tab 里），并设成几乎全透明，免得闪一个点。
    /// </summary>
    private void CreateHelperWindow(nint module)
    {
        _className = "QuellGTA.HotkeyPassthrough." + Guid.NewGuid().ToString("N");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc!),
            hInstance = module,
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            _log($"注册窗口类失败（错误 {Marshal.GetLastWin32Error()}）", true);
            _className = null;
            return;
        }
        _window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED, _className, "HotKeySavior",
            WS_POPUP, 0, 0, 1, 1, 0, 0, module, 0);
        if (_window == 0) _log($"创建辅助窗口失败（错误 {Marshal.GetLastWin32Error()}）", true);
        else SetLayeredWindowAttributes(_window, 0, 1, LWA_ALPHA);
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            Interlocked.Exchange(ref _lastEvent, Environment.TickCount64);
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // 自己（以及主程序）模拟出来的按键不算，否则重发的组合键会再触发一次
            if (info.dwExtraInfo != InjectedTag)
            {
                var msg = (int)wParam;
                var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                if (down || msg is WM_KEYUP or WM_SYSKEYUP)
                {
                    var hit = _matcher.OnKey((int)info.vkCode, down);
                    // 原版的「正在进行」：上一次穿透还没做完时不再接新的
                    if (hit is { } combo && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
                    {
                        var foreground = GetForegroundWindow();
                        ThreadPool.QueueUserWorkItem(_ => Passthrough(combo, foreground));
                    }
                }
            }
        }
        // 只旁听，不吃掉按键：游戏照样收到这次按键（和原版一样）
        return CallNextHookEx(0, code, wParam, lParam);
    }

    // ───────── 穿透 ─────────

    /// <summary>
    /// 对应原版「热键模拟」：激活自己的窗口 → 等一会儿 → 重发组合键 → 切回游戏。
    /// 游戏（GTA5.exe / GTA5_Enhanced.exe / 自定义进程，由主程序的设置决定）不在前台时什么也不做，
    /// 这时别的程序的全局热键本来就能正常触发。
    /// </summary>
    private void Passthrough(Combo combo, nint gameWindow)
    {
        try
        {
            if (_window == 0 || !_isGameForeground()) return;

            SendMessageTimeoutW(_window, WM_APP_ACTIVATE, 0, 0, SMTO_ABORTIFHUNG, 1000, out _);
            // 等系统把前台真正交过来（SwitchToThisWindow 是异步的），也给其他程序反应时间
            var deadline = Environment.TickCount64 + Math.Max(SwitchDelay, 50);
            while (Environment.TickCount64 < deadline) Thread.Sleep(10);

            if (GetForegroundWindow() != _window)
            {
                _log($"没能把焦点从游戏切出来，{combo} 未穿透", true);
            }
            else
            {
                foreach (var step in InjectionPlan.Build(combo))
                {
                    SendKey(step.Vk, step.Down);
                    if (step.DelayAfter > 0) Thread.Sleep(step.DelayAfter);
                }
                _log($"已穿透 {combo}", false);
            }

            // 切回按下组合键时的那个游戏窗口；它没了就找游戏当前的主窗口
            var target = IsWindow(gameWindow) ? gameWindow : _gameWindow();
            SendMessageTimeoutW(_window, WM_APP_RESTORE, target, 0, SMTO_ABORTIFHUNG, 1000, out _);
        }
        catch (Exception ex)
        {
            _log("热键穿透出错：" + ex.Message, true);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// 对应原版「键盘_按键」：SendInput，同时给出虚拟键码和扫描码（RegisterHotKey 按虚拟键码匹配）。
    /// 比原版多了扩展键标志，否则方向键、Home/End 等会被当成小键盘上的键。
    /// </summary>
    private static void SendKey(int vk, bool down)
    {
        var flags = (down ? 0u : KEYEVENTF_KEYUP) | (InjectionPlan.IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0u);
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = (ushort)MapVirtualKeyW((uint)vk, 0),
                dwFlags = flags,
                dwExtraInfo = InjectedTag,
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    // 下面两个消息在小窗口自己的线程上处理：SetForegroundWindow / SetFocus 只对调用线程有效。
    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_APP_ACTIVATE:
                Activate(hwnd);
                return 0;
            case WM_APP_RESTORE:
                Restore(hwnd, wParam);
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 对应原版「窗口_强制显示（置顶）+ 窗口_将焦点切换到指定的窗口（SwitchToThisWindow）」。
    /// 后台进程直接 SetForegroundWindow 会被系统拒绝，所以先把本线程的输入挂到前台（游戏）线程上，
    /// 这样系统认为是同一个输入队列内的切换；还不行再退回原版用的 SwitchToThisWindow。
    /// </summary>
    private static void Activate(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        try
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 1, 1, SWP_SHOWWINDOW);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(self, foregroundThread, false);
        }
        if (GetForegroundWindow() != hwnd) SwitchToThisWindow(hwnd, true);
    }

    /// <summary>
    /// 切回游戏，再藏起小窗口。此时前台是自己，SetForegroundWindow 一般直接成功；
    /// 先切再藏，免得小窗口一藏系统把前台随手交给别的窗口。全屏独占模式下游戏被切走时会最小化，这里顺便还原。
    /// </summary>
    private static void Restore(nint hwnd, nint target)
    {
        if (target != 0 && IsWindow(target))
        {
            if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
            SetForegroundWindow(target);
            if (GetForegroundWindow() != target) SwitchToThisWindow(target, true);
        }
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ───────── Win32 ─────────

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80;
    private static readonly nint HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
    private const int SW_RESTORE = 9;
    private const uint SMTO_ABORTIFHUNG = 0x2;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

    private delegate nint HookProc(int code, nint wParam, nint lParam);
    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    /// <summary>INPUT 里的联合体按最大的 MOUSEINPUT 算，这里只用键盘，补齐到同样大小（x64 上 40 字节）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, HookProc proc, nint hMod, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string className, nint instance);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint hwnd, bool altTab);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
