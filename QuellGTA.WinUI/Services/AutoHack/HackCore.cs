using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuellGTA.Services.AutoHack;

// 自动骇入的公共部分，移植自 https://github.com/mageangela/AutoHack_for_QuellGTA
// （capture/game_window.cpp、input/key_input.cpp）。各小游戏的识别代码在同目录的其他文件里。

/// <summary>一帧游戏画面（capture::GameFrame）。高度超过 1080 的画面会缩到 1080 再分析。</summary>
public sealed class HackFrame
{
    /// <summary>截取区域在屏幕上的位置和大小（屏幕像素）。</summary>
    public int ScreenX, ScreenY, ScreenW, ScreenH;
    /// <summary>分析用的宽高（像素数组的尺寸）。</summary>
    public int Width, Height;
    public int ClientWidth, ClientHeight;
    /// <summary>游戏窗口的位置或大小每变一次加一，用来判断缓存的坐标还能不能用。</summary>
    public ulong WindowGeneration;
    /// <summary>截图时刻（Stopwatch 计数）。</summary>
    public long CaptureTicks;
    public double ToScreenX = 1, ToScreenY = 1;
    /// <summary>每个像素 0x00RRGGBB，逐行存放。</summary>
    public uint[] Bgra = [];
}

public readonly record struct HackRect(int Left, int Top, int Right, int Bottom);

public enum HackJobStatus { Invalid, Pending, Succeeded, Failed, Canceled }

/// <summary>一次排队的按键任务（input::Job）。</summary>
public sealed class HackJob
{
    internal volatile int StatusValue = (int)HackJobStatus.Pending;
    internal long StartedTicksValue;

    public HackJobStatus Status => (HackJobStatus)StatusValue;
    public bool Pending => Status == HackJobStatus.Pending;
    public bool Succeeded => Status == HackJobStatus.Succeeded;
    /// <summary>第一个键按下的时刻（Stopwatch 计数），还没按时为 0。</summary>
    public long StartedTicks => Interlocked.Read(ref StartedTicksValue);

    public static HackJob Done(HackJobStatus status) => new() { StatusValue = (int)status };
}

/// <summary>一个按键动作：点按，或长按 2 秒。</summary>
public readonly record struct HackKey(int VirtualKey, bool LongPress = false);

/// <summary>
/// 小游戏模块和外界打交道的全部接口：截图、判断前台窗口、发按键。
/// 正式运行时由 HackHost 实现；测试时换成喂合成画面、记录按键的替身。
/// </summary>
public interface IHackHost
{
    /// <summary>截一帧游戏画面；region 是屏幕坐标，只截它和游戏客户区相交的部分。失败返回 null。</summary>
    HackFrame? Capture(HackRect? screenRegion = null);
    /// <summary>当前前台窗口正好是这帧画面所属的游戏窗口时返回它的句柄，否则返回 0。</summary>
    nint ForegroundGameWindow(HackFrame frame);
    /// <summary>按顺序点按一串键。expectedForeground 不为 0 时，前台窗口一变就中止。</summary>
    HackJob QueueSequence(IReadOnlyList<HackKey> keys, nint expectedForeground);
    /// <summary>在指定时刻（Stopwatch 计数）按一下键，固定按住 42 毫秒，不受按键时长设置影响。</summary>
    HackJob QueueImmediate(int virtualKey, long executeAtTicks, nint expectedForeground);
    /// <summary>丢掉排队中的按键，松开正按着的键。</summary>
    void CancelAll();
}

/// <summary>各模块共用的小工具。</summary>
public static class HackUtil
{
    public static long Now => Stopwatch.GetTimestamp();
    public static long Ms(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);
    public static double ElapsedMs(long sinceTicks) => (Stopwatch.GetTimestamp() - sinceTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>C++ 的 std::lround：四舍五入，.5 远离零。</summary>
    public static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    /// <summary>
    /// 把本进程的系统计时精度临时调到 1 毫秒，Dispose 时恢复。默认精度是 15.6 毫秒，
    /// Sleep 和等待都可能晚到一整个时钟周期，对要卡时机按键的小游戏（弗利萨、滑块）影响很大。原版没有这一步。
    /// </summary>
    public static IDisposable HighResolutionTimer()
    {
        var ok = timeBeginPeriod(1) == 0;
        return new Restore(ok);
    }

    private sealed class Restore(bool active) : IDisposable
    {
        private bool _active = active;
        public void Dispose()
        {
            if (!_active) return;
            _active = false;
            timeEndPeriod(1);
        }
    }

    /// <summary>等到这一帧开始后满 16 毫秒（各模块的 WaitForFrame）。</summary>
    public static void WaitForFrame(Func<bool> stopRequested, long frameStartedTicks, double frameMs = 16)
    {
        var deadline = frameStartedTicks + Ms(frameMs);
        while (!stopRequested() && Now < deadline)
        {
            if (deadline - Now > Ms(2)) Thread.Sleep(1);
            else Thread.Yield();
        }
    }
}

/// <summary>正式运行用的宿主：GDI 截屏 + SendInput 发扫描码。</summary>
public sealed class HackHost : IHackHost, IDisposable
{
    private readonly GameService _game;
    private readonly object _windowGate = new();
    private nint _window;
    private HackRect _clientRect;
    private bool _hasClientRect;
    private ulong _generation = 1;

    /// <summary>点按的按住时长和两次按键的间隔（毫秒），原版默认都是 20。</summary>
    public int TapHoldMs = 20, TapGapMs = 20;

    public HackHost(GameService game)
    {
        _game = game;
        _worker = new Thread(Run) { IsBackground = true, Name = "QuellGTA.AutoHack.Input", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    // ───────── 游戏窗口 ─────────

    private static bool ReadClientRect(nint window, out HackRect rect)
    {
        rect = default;
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)) return false;
        if (!GetClientRect(window, out var client)) return false;
        POINT topLeft = new() { X = client.Left, Y = client.Top }, bottomRight = new() { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(window, ref topLeft) || !ClientToScreen(window, ref bottomRight)) return false;
        rect = new HackRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    /// <summary>找游戏窗口（capture::FindGameWindow）。</summary>
    public bool FindGameWindow()
    {
        var window = _game.GetWindowHandle();
        lock (_windowGate)
        {
            var found = ReadClientRect(window, out var rect);
            if (!found || window != _window || !_hasClientRect || rect != _clientRect) _generation++;
            _window = found ? window : 0;
            _clientRect = found ? rect : default;
            _hasClientRect = found;
            return found;
        }
    }

    public void ClearGameWindow()
    {
        lock (_windowGate)
        {
            if (_window != 0 || _hasClientRect) _generation++;
            _window = 0;
            _clientRect = default;
            _hasClientRect = false;
        }
    }

    public nint GameWindow { get { lock (_windowGate) return _window; } }

    private bool RefreshWindowLocked()
    {
        if (_window == 0 || !ReadClientRect(_window, out var current))
        {
            if (_window != 0 || _hasClientRect) _generation++;
            _window = 0;
            _clientRect = default;
            _hasClientRect = false;
            return false;
        }
        if (!_hasClientRect || current != _clientRect)
        {
            _clientRect = current;
            _hasClientRect = true;
            _generation++;
        }
        return true;
    }

    // ───────── 截图 ─────────

    [ThreadStatic] private static CaptureResources? _capture;

    private sealed class CaptureResources
    {
        public nint Memory, Bitmap, Previous, Bits;
        public int Width, Height;

        public bool Resize(nint screen, int width, int height)
        {
            if (Memory != 0 && Bitmap != 0 && Width == width && Height == height) return true;
            if (Memory != 0 && Previous != 0) SelectObject(Memory, Previous);
            if (Bitmap != 0) DeleteObject(Bitmap);
            Bitmap = Previous = Bits = 0;
            if (Memory == 0) Memory = CreateCompatibleDC(screen);
            if (Memory == 0) return false;
            var info = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
            };
            Bitmap = CreateDIBSection(screen, ref info, 0, out Bits, 0, 0);
            if (Bitmap == 0 || Bits == 0) return false;
            Previous = SelectObject(Memory, Bitmap);
            Width = width;
            Height = height;
            return true;
        }
    }

    public HackFrame? Capture(HackRect? screenRegion = null)
    {
        HackRect client;
        ulong generation;
        lock (_windowGate)
        {
            if (!RefreshWindowLocked()) return null;
            client = _clientRect;
            generation = _generation;
        }
        var source = client;
        if (screenRegion is { } region)
        {
            source = new HackRect(Math.Max(client.Left, region.Left), Math.Max(client.Top, region.Top),
                Math.Min(client.Right, region.Right), Math.Min(client.Bottom, region.Bottom));
            if (source.Right <= source.Left || source.Bottom <= source.Top) return null;
        }
        int clientH = client.Bottom - client.Top, sourceW = source.Right - source.Left, sourceH = source.Bottom - source.Top;
        if (clientH <= 0 || sourceW <= 0 || sourceH <= 0) return null;
        var scale = clientH > 1080 ? 1080.0 / clientH : 1.0;
        var width = Math.Max(1, HackUtil.Round(sourceW * scale));
        var height = Math.Max(1, HackUtil.Round(sourceH * scale));

        var screen = GetDC(0);
        if (screen == 0) return null;
        var capture = _capture ??= new CaptureResources();
        bool copied;
        long capturedAt;
        try
        {
            if (!capture.Resize(screen, width, height)) return null;
            if (width == sourceW && height == sourceH)
                copied = BitBlt(capture.Memory, 0, 0, width, height, screen, source.Left, source.Top, SRCCOPY);
            else
            {
                SetStretchBltMode(capture.Memory, COLORONCOLOR);
                copied = StretchBlt(capture.Memory, 0, 0, width, height, screen, source.Left, source.Top, sourceW, sourceH, SRCCOPY);
            }
            capturedAt = HackUtil.Now;
        }
        finally { ReleaseDC(0, screen); }
        if (!copied) return null;

        var frame = new HackFrame
        {
            ScreenX = source.Left, ScreenY = source.Top, ScreenW = sourceW, ScreenH = sourceH,
            Width = width, Height = height, ClientHeight = clientH, ClientWidth = client.Right - client.Left,
            WindowGeneration = generation, CaptureTicks = capturedAt,
            ToScreenX = sourceW / (double)width, ToScreenY = sourceH / (double)height,
            Bgra = new uint[width * height],
        };
        unsafe
        {
            fixed (uint* target = frame.Bgra)
                Buffer.MemoryCopy((void*)capture.Bits, target, (long)frame.Bgra.Length * 4, (long)frame.Bgra.Length * 4);
        }
        return frame;
    }

    public nint ForegroundGameWindow(HackFrame frame)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || !GetClientRect(foreground, out var client)) return 0;
        var origin = new POINT { X = client.Left, Y = client.Top };
        if (!ClientToScreen(foreground, ref origin)) return 0;
        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        return origin.X == frame.ScreenX && origin.Y == frame.ScreenY && width == frame.ScreenW && height == frame.ScreenH ? foreground : 0;
    }

    // ───────── 按键队列（input::Dispatcher） ─────────

    private readonly record struct Stroke(ushort ScanCode, bool Extended, int HoldMs, int GapMs);
    private sealed record Request(List<Stroke> Strokes, long StartAt, nint ExpectedForeground, ulong Generation, HackJob Job);

    private readonly object _queueGate = new();
    private readonly Queue<Request> _queue = new();
    private readonly Thread _worker;
    private ulong _inputGeneration = 1;
    private bool _stopping;

    private static (ushort scan, bool extended) ToScan(int virtualKey) => virtualKey switch
    {
        0x26 => (0x48, true), 0x28 => (0x50, true), 0x25 => (0x4B, true), 0x27 => (0x4D, true), 0x0D => (0x1C, false),
        _ => ((ushort)MapVirtualKeyW((uint)virtualKey, 0), false),
    };

    private static bool SendKey(Stroke stroke, bool up)
    {
        var input = new INPUT
        {
            type = 1,
            ki = new KEYBDINPUT
            {
                wScan = stroke.ScanCode,
                dwFlags = 0x0008 /* SCANCODE */ | (stroke.Extended ? 0x0001u : 0) | (up ? 0x0002u : 0),
                dwExtraInfo = (nint)QuellGTA.Native.NativeMethods.InjectedTag, // 让本程序自己的热键钩子认出这是模拟按键
            },
        };
        return SendInput(1, ref input, Marshal.SizeOf<INPUT>()) == 1;
    }

    private HackJob Enqueue(List<Stroke> strokes, long startAt, nint expectedForeground)
    {
        if (strokes.Count == 0) return HackJob.Done(HackJobStatus.Succeeded);
        if (strokes.Any(s => s.ScanCode == 0)) return HackJob.Done(HackJobStatus.Failed);
        var job = new HackJob();
        lock (_queueGate)
        {
            _queue.Enqueue(new Request(strokes, startAt, expectedForeground, _inputGeneration, job));
            Monitor.PulseAll(_queueGate);
        }
        return job;
    }

    public HackJob QueueSequence(IReadOnlyList<HackKey> keys, nint expectedForeground)
    {
        var strokes = new List<Stroke>(keys.Count);
        foreach (var key in keys)
        {
            var (scan, extended) = ToScan(key.VirtualKey);
            strokes.Add(key.LongPress ? new Stroke(scan, extended, 2000, 0) : new Stroke(scan, extended, Math.Max(0, TapHoldMs), Math.Max(0, TapGapMs)));
        }
        return Enqueue(strokes, HackUtil.Now, expectedForeground);
    }

    public HackJob QueueImmediate(int virtualKey, long executeAtTicks, nint expectedForeground)
    {
        var (scan, extended) = ToScan(virtualKey);
        return Enqueue([new Stroke(scan, extended, 42, 0)], executeAtTicks, expectedForeground);
    }

    public void CancelAll()
    {
        lock (_queueGate)
        {
            _inputGeneration++;
            CancelPendingLocked();
            Monitor.PulseAll(_queueGate);
        }
    }

    private void CancelPendingLocked()
    {
        foreach (var request in _queue) request.Job.StatusValue = (int)HackJobStatus.Canceled;
        _queue.Clear();
    }

    /// <summary>等到指定时刻；期间被取消或程序退出则返回 false。最后 2 毫秒改用让出时间片的方式，保证时刻精确。</summary>
    private bool WaitUntil(long deadline, ulong generation)
    {
        while (true)
        {
            lock (_queueGate)
            {
                if (_stopping || generation != _inputGeneration) return false;
                var remaining = (deadline - HackUtil.Now) * 1000.0 / Stopwatch.Frequency;
                if (remaining <= 0) return true;
                if (remaining > 3) { Monitor.Wait(_queueGate, (int)(remaining - 2)); continue; }
            }
            Thread.Yield();
        }
    }

    private bool Active(ulong generation)
    {
        lock (_queueGate) return !_stopping && generation == _inputGeneration;
    }

    private void Run()
    {
        while (true)
        {
            Request request;
            lock (_queueGate)
            {
                while (!_stopping && _queue.Count == 0) Monitor.Wait(_queueGate);
                if (_stopping) return;
                request = _queue.Dequeue();
            }
            if (!WaitUntil(request.StartAt, request.Generation))
            {
                request.Job.StatusValue = (int)HackJobStatus.Canceled;
                continue;
            }
            var result = HackJobStatus.Succeeded;
            foreach (var stroke in request.Strokes)
            {
                if (!Active(request.Generation)) { result = HackJobStatus.Canceled; break; }
                if (request.ExpectedForeground != 0 && GetForegroundWindow() != request.ExpectedForeground) { result = HackJobStatus.Failed; break; }
                if (!SendKey(stroke, false)) { result = HackJobStatus.Failed; break; }
                Interlocked.CompareExchange(ref request.Job.StartedTicksValue, HackUtil.Now, 0);
                var stillActive = WaitUntil(HackUtil.Now + HackUtil.Ms(stroke.HoldMs), request.Generation);
                var released = SendKey(stroke, true); // 无论如何都要松开
                if (!stillActive) { result = HackJobStatus.Canceled; break; }
                if (!released) { result = HackJobStatus.Failed; break; }
                if (!WaitUntil(HackUtil.Now + HackUtil.Ms(stroke.GapMs), request.Generation)) { result = HackJobStatus.Canceled; break; }
            }
            request.Job.StatusValue = (int)result;
        }
    }

    public void Dispose()
    {
        lock (_queueGate)
        {
            _stopping = true;
            CancelPendingLocked();
            Monitor.PulseAll(_queueGate);
        }
        _worker.Join(3000);
    }

    // ───────── Win32 ─────────

    private const uint SRCCOPY = 0x00CC0020;
    private const int COLORONCOLOR = 3;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, ref INPUT input, int size);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
}

/// <summary>一种骇入小游戏的识别与操作。</summary>
public interface IHackGame
{
    /// <summary>给人看的名字。</summary>
    string Name { get; }
    /// <summary>当前画面是不是这个小游戏。</summary>
    bool DetectInGame();
    void ResetInGameCache();
    /// <summary>运行到完成（true）、小游戏消失或被要求停止（false）。status 收到的是原版的英文状态文字。</summary>
    bool RunSession(Func<bool> stopRequested, Action<string> status);
}
