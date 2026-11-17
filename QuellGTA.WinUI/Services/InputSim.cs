using System.Runtime.InteropServices;
using static QuellGTA.Native.NativeMethods;

namespace QuellGTA.Services;

/// <summary>对应原版的「键盘_按键 / 鼠标_按键」：用 SendInput 模拟输入，并打上标记让热键钩子忽略。</summary>
public static class InputSim
{
    private static readonly int Size = Marshal.SizeOf<INPUT>();

    // 这些键需要扩展键标志，否则会被当成小键盘上的对应键。
    private static bool IsExtended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5 or 0x6F or 0x90;

    private static void Key(int vk, bool up)
    {
        var flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0, // 游戏读的是扫描码
                    wScan = (ushort)MapVirtualKeyW((uint)vk, 0),
                    dwFlags = flags,
                    dwExtraInfo = InjectedTag,
                },
            },
        };
        SendInput(1, [input], Size);
    }

    public static void KeyDown(int vk) => Key(vk, false);
    public static void KeyUp(int vk) => Key(vk, true);

    public static void KeyPress(int vk, int holdMs = 50)
    {
        KeyDown(vk);
        Thread.Sleep(holdMs);
        KeyUp(vk);
    }

    private static void Mouse(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = InjectedTag } },
        };
        SendInput(1, [input], Size);
    }

    public static void LeftClick()
    {
        Mouse(MOUSEEVENTF_LEFTDOWN);
        Mouse(MOUSEEVENTF_LEFTUP);
    }

    public static void RightDown() => Mouse(MOUSEEVENTF_RIGHTDOWN);
    public static void RightUp() => Mouse(MOUSEEVENTF_RIGHTUP);

    /// <summary>宏脚本里的鼠标操作代码：1 左键、2 右键、3 中键。</summary>
    public static void MouseButton(int button, bool up)
    {
        var flags = button switch
        {
            1 => up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN,
            2 => up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN,
            3 => up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN,
            _ => 0u,
        };
        if (flags != 0) Mouse(flags);
    }

    /// <summary>
    /// 物理模拟输入文本：逐个字符换算成按键（需要时带 Shift）打出去。只能打键盘上有的字符，中文等会被跳过。
    /// 返回实际打出的字符数。
    /// </summary>
    public static int TypeText(string text, CancellationToken token = default)
    {
        const int VkShift = 0x10;
        var typed = 0;
        foreach (var ch in text)
        {
            if (token.IsCancellationRequested) break;
            if (ch is < ' ' or > '~') continue;
            var scan = VkKeyScanW(ch);
            if (scan == -1) continue;
            var vk = scan & 0xFF;
            var modifiers = (scan >> 8) & 0xFF;
            if ((modifiers & ~1) != 0) continue; // 需要 Ctrl / Alt 才能打出的字符不处理
            var shift = (modifiers & 1) != 0;
            if (shift) { KeyDown(VkShift); Thread.Sleep(1); }
            KeyDown(vk);
            Thread.Sleep(1);
            KeyUp(vk);
            Thread.Sleep(1);
            if (shift) { KeyUp(VkShift); Thread.Sleep(1); }
            typed++;
        }
        return typed;
    }

    /// <summary>文本_投递W：直接向窗口投递字符消息，可以输入中文（传承版的聊天框接受这种方式）。</summary>
    public static void PostText(nint hwnd, string text)
    {
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n') continue;
            PostMessageW(hwnd, WM_CHAR, ch, 0);
        }
    }

    /// <summary>读剪贴板里的文本，可以在任意线程调用。</summary>
    public static string GetClipboardText()
    {
        const uint CfUnicodeText = 13;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(0)) { Thread.Sleep(20); continue; }
            try
            {
                var handle = GetClipboardData(CfUnicodeText);
                if (handle == 0) return "";
                var pointer = GlobalLock(handle);
                if (pointer == 0) return "";
                try { return Marshal.PtrToStringUni(pointer) ?? ""; }
                finally { GlobalUnlock(handle); }
            }
            finally { CloseClipboard(); }
        }
        return "";
    }

    /// <summary>系统_取空闲时间：距离上一次键鼠输入过去了多少毫秒。</summary>
    public static long IdleMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.dwTime) : 0;
    }

    /// <summary>检测按键状态：物理或模拟的按键当前是否处于按下。</summary>
    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
