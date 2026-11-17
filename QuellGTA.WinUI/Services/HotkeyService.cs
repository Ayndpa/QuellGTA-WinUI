using System.Runtime.InteropServices;
using static QuellGTA.Native.NativeMethods;

namespace QuellGTA.Services;

[Flags]
public enum HotkeyMods { None = 0, Ctrl = 1, Alt = 2, Shift = 4 }

/// <summary>一个热键：组合键 + 键代码。键代码 -1 表示停用（与原版一致）。</summary>
public readonly record struct Hotkey(HotkeyMods Mods, int Vk)
{
    public static readonly Hotkey None = new(HotkeyMods.None, -1);
    public bool IsSet => Vk > 0;

    public static Hotkey Parse(string text)
    {
        var parts = text.Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var v) && v > 0
            ? new Hotkey((HotkeyMods)m, v)
            : None;
    }

    public string Serialize() => $"{(int)Mods},{Vk}";

    public override string ToString()
    {
        if (!IsSet) return "未设置";
        var s = "";
        if (Mods.HasFlag(HotkeyMods.Ctrl)) s += "Ctrl + ";
        if (Mods.HasFlag(HotkeyMods.Alt)) s += "Alt + ";
        if (Mods.HasFlag(HotkeyMods.Shift)) s += "Shift + ";
        return s + KeyName(Vk);
    }

    public static string KeyName(int vk) => vk switch
    {
        0x01 => "鼠标左键",
        0x02 => "鼠标右键",
        HotkeyService.WheelUp => "鼠标滚轮上",
        HotkeyService.WheelDown => "鼠标滚轮下",
        0x04 => "鼠标中键",
        0x05 => "鼠标侧键下",
        0x06 => "鼠标侧键上",
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "CapsLock",
        0x1B => "Esc", 0x20 => "Space", 0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓", 0x2C => "PrintScreen", 0x2D => "Insert", 0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "小键盘 " + (vk - 0x60),
        0x6A => "小键盘 *", 0x6B => "小键盘 +", 0x6D => "小键盘 -", 0x6E => "小键盘 .", 0x6F => "小键盘 /",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x90 => "NumLock", 0x91 => "ScrollLock",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => $"键 0x{vk:X2}",
    };
}

/// <summary>
/// 热键引擎。原版在 2024 冬季 DLC 之后改用底层钩子做「热键穿透」（游戏屏蔽了 RegisterHotKey），
/// 这里同样使用 WH_KEYBOARD_LL / WH_MOUSE_LL，并放在独立线程的消息循环里，界面卡顿不会让钩子超时失效。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>鼠标滚轮没有虚拟键码，这里用两个 255 以外的数代表。</summary>
    public const int WheelUp = 0x100, WheelDown = 0x101;

    private readonly Dictionary<string, Hotkey> _hotkeys = [];
    private readonly HashSet<string> _held = [];
    private readonly object _gate = new();

    private HookProc? _keyboardProc, _mouseProc; // 保持委托存活，防止被 GC
    private nint _keyboardHook, _mouseHook;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _polling, _stopPolling;
    private HotkeyMods _mods;
    private Action<Hotkey>? _capture;
    private int _swallowUp;

    /// <summary>热键被触发（名称, 按下状态）。在线程池线程上回调。</summary>
    public event Action<string, bool>? Triggered;

    /// <summary>判断热键时忽略哪些修饰键（原版「禁用 Alt / Ctrl / Shift 检测」）。</summary>
    public HotkeyMods IgnoreMods { get; set; }

    /// <summary>轮询模式：不装钩子，定时查询按键状态。下次 Start 时生效。</summary>
    public bool UsePolling { get; set; }

    /// <summary>钩子最近一次收到键鼠事件的时刻（Environment.TickCount64），自检用。</summary>
    public long LastEventTick { get; private set; } = Environment.TickCount64;

    public bool IsRunning => _polling || _keyboardHook != 0;

    public void Set(string name, Hotkey hotkey)
    {
        lock (_gate)
        {
            if (hotkey.IsSet) _hotkeys[name] = hotkey; else _hotkeys.Remove(name);
            _held.Remove(name);
        }
    }

    /// <summary>改键：下一次按下的非修饰键（连同当时的组合键）会交给回调，而不触发热键。</summary>
    public void BeginCapture(Action<Hotkey> callback) => _capture = callback;
    public void CancelCapture() => _capture = null;

    public void Start()
    {
        if (_thread is not null) return;
        LastEventTick = Environment.TickCount64;
        if (UsePolling)
        {
            _stopPolling = false;
            _polling = true;
            _thread = new Thread(PollLoop) { IsBackground = true, Name = "QuellGTA.HotkeyPoll", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            return;
        }
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _keyboardProc = KeyboardProc;
            _mouseProc = MouseProc;
            var module = GetModuleHandleW(null);
            _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, module, 0);
            ready.Set();
            while (GetMessageW(out _, 0, 0, 0) > 0) { }
            if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = _mouseHook = 0;
        })
        { IsBackground = true, Name = "QuellGTA.Hotkeys", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Stop()
    {
        if (_thread is null) return;
        if (_polling)
        {
            _stopPolling = true;
            _thread.Join(1000);
            _polling = false;
        }
        else
        {
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            _thread.Join(1000);
        }
        _thread = null;
        _mods = HotkeyMods.None;
        lock (_gate) _held.Clear();
    }

    /// <summary>「重启热键引擎」。</summary>
    public void Restart() { Stop(); Start(); }

    public void Dispose() => Stop();

    // ───────── 钩子模式 ─────────

    private nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            LastEventTick = Environment.TickCount64;
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != InjectedTag) // 忽略自己模拟出的按键
            {
                var msg = (uint)wParam;
                if (OnKey((int)info.vkCode, msg is WM_KEYDOWN or WM_SYSKEYDOWN, allowCapture: true)) return 1;
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            LastEventTick = Environment.TickCount64;
            var msg = (uint)wParam;
            if (msg is WM_MBUTTONDOWN or WM_MBUTTONUP or WM_XBUTTONDOWN or WM_XBUTTONUP
                or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_RBUTTONUP or WM_MOUSEWHEEL)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (info.dwExtraInfo != InjectedTag)
                {
                    switch (msg)
                    {
                        case WM_MOUSEWHEEL:
                            // 滚轮只有「滚了一格」，没有按下和松开，所以连着触发一次按下和一次松开
                            var wheel = (short)(info.mouseData >> 16) > 0 ? WheelUp : WheelDown;
                            OnKey(wheel, true, allowCapture: false);
                            OnKey(wheel, false, allowCapture: false);
                            break;
                        case WM_LBUTTONDOWN or WM_LBUTTONUP:
                            OnKey(0x01, msg == WM_LBUTTONDOWN, allowCapture: false); // 左右键不参与改键，否则没法点界面
                            break;
                        case WM_RBUTTONDOWN or WM_RBUTTONUP:
                            OnKey(0x02, msg == WM_RBUTTONDOWN, allowCapture: false);
                            break;
                        default:
                            var vk = msg is WM_MBUTTONDOWN or WM_MBUTTONUP ? 0x04 : (info.mouseData >> 16) == 1 ? 0x05 : 0x06;
                            if (OnKey(vk, msg is WM_MBUTTONDOWN or WM_XBUTTONDOWN, allowCapture: true)) return 1;
                            break;
                    }
                }
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    // ───────── 轮询模式 ─────────

    /// <summary>
    /// 每 10 毫秒查询一次按键状态。只查已注册的热键用到的键；改键时则把所有键扫一遍。
    /// 查不到滚轮，也分不清按键是不是本程序自己模拟的，所以宏里用到的键不要同时设成热键。
    /// </summary>
    private void PollLoop()
    {
        var down = new HashSet<int>();
        while (!_stopPolling)
        {
            Thread.Sleep(10);
            _mods = (InputSim.IsDown(0x10) ? HotkeyMods.Shift : 0) | (InputSim.IsDown(0x11) ? HotkeyMods.Ctrl : 0)
                    | (InputSim.IsDown(0x12) ? HotkeyMods.Alt : 0);

            IEnumerable<int> watch;
            if (_capture is not null) watch = Enumerable.Range(3, 252); // 改键：除鼠标左右键外全部
            else lock (_gate) watch = _hotkeys.Values.Select(h => h.Vk).Where(v => v <= 255).Distinct().ToArray();

            foreach (var vk in watch)
            {
                if (ModifierOf(vk) != HotkeyMods.None || vk is 0x5B or 0x5C) continue;
                var pressed = InputSim.IsDown(vk);
                if (pressed == down.Contains(vk)) continue;
                if (pressed) down.Add(vk); else down.Remove(vk);
                OnKey(vk, pressed, allowCapture: true);
            }
        }
    }

    private static HotkeyMods ModifierOf(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => HotkeyMods.Shift,
        0x11 or 0xA2 or 0xA3 => HotkeyMods.Ctrl,
        0x12 or 0xA4 or 0xA5 => HotkeyMods.Alt,
        _ => HotkeyMods.None,
    };

    /// <summary>返回 true 表示这次按键要被吃掉，不再传给其他程序（只在改键时发生）。</summary>
    private bool OnKey(int vk, bool down, bool allowCapture)
    {
        if (vk == 255) return false;
        var modifier = ModifierOf(vk);
        if (modifier != HotkeyMods.None)
        {
            if (!_polling) _mods = down ? _mods | modifier : _mods & ~modifier;
            return false;
        }
        if (vk is 0x5B or 0x5C) return false; // Win 键不参与

        if (allowCapture && down && _capture is { } capture)
        {
            // 改键时把这次按键（连同随后的松开）吃掉，否则空格 / 回车会再点一次按钮，字母会打进输入框
            _capture = null;
            _swallowUp = vk;
            var captured = new Hotkey(_mods, vk);
            ThreadPool.QueueUserWorkItem(_ => capture(captured));
            return true;
        }
        if (vk == _swallowUp)
        {
            if (!down) _swallowUp = 0;
            return true;
        }

        var ignore = IgnoreMods;
        var current = _mods & ~ignore;
        List<(string name, bool state)>? fired = null;
        lock (_gate)
        {
            foreach (var (name, hotkey) in _hotkeys)
            {
                if (hotkey.Vk != vk) continue;
                if (down)
                {
                    // 组合键必须一致（被忽略的修饰键除外）；按住不放的自动重复只算一次。
                    if ((hotkey.Mods & ~ignore) != current || !_held.Add(name)) continue;
                    (fired ??= []).Add((name, true));
                }
                else if (_held.Remove(name))
                {
                    (fired ??= []).Add((name, false));
                }
            }
        }
        if (fired is null) return false;
        // 钩子回调必须尽快返回，实际动作丢到线程池执行。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var (name, state) in fired) Triggered?.Invoke(name, state);
        });
        return false;
    }
}
