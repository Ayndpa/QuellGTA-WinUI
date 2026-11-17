using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using static QuellGTA.Native.OverlayNative;

namespace QuellGTA.Services;

public enum CrosshairShape { Cross = 0, Dot = 1, Circle = 2, CrossDot = 3, CrossGap = 4, Text = 5 }

public enum TimerState { Idle, Running, Stopped, Paused }

/// <summary>
/// 外置准星和简易计时。原版是两个易语言窗口（准星窗口 / 计时窗口），这里用两个鼠标穿透的置顶分层窗口实现，
/// 在独立线程上绘制，不占用界面线程。游戏需要使用「无边框窗口」模式，独占全屏下系统不会显示任何置顶窗口。
/// </summary>
public sealed class OverlayService : IDisposable
{
    private readonly Settings _settings;
    private readonly GameService _game;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _dirty = true;

    private long _timerStart;      // Environment.TickCount64，计时开始的时刻
    private long _timerElapsed;    // 停止后冻结的毫秒数
    private TimerState _timerState = TimerState.Idle;

    public event Action? TimerChanged;

    /// <summary>改用 RTSS 的 OSD 显示时，内置的浮窗隐藏（计时和准星本身照常工作）。</summary>
    public volatile bool SuppressTimer, SuppressCrosshair;

    public OverlayService(Settings settings, GameService game)
    {
        _settings = settings;
        _game = game;
    }

    // ───────── 准星设置 ─────────
    public bool CrosshairEnabled { get => B("外置准星", false); set => Set("外置准星", value); }
    public CrosshairShape CrosshairShape { get => (CrosshairShape)I("准星形状", 0, 0, 5); set => Set("准星形状", (int)value); }
    /// <summary>改变准星字符：形状选「字符」时，用这段文字当准星。原版默认「*」。</summary>
    public string CrosshairText { get => S("准星字符", "+"); set => Set("准星字符", value.Length > 4 ? value[..4] : value); }
    /// <summary>准星半径（像素，未乘 DPI）。原版默认 20。</summary>
    public int CrosshairSize { get => I("准星大小", 20, 2, 200); set => Set("准星大小", value); }
    public int CrosshairThickness { get => I("准星线宽", 2, 1, 20); set => Set("准星线宽", value); }
    /// <summary>颜色，RRGGBB。</summary>
    public string CrosshairColor { get => S("准星颜色RGB", "00FFFF"); set => Set("准星颜色RGB", value); }
    public int CrosshairOpacity { get => I("准星不透明度", 100, 10, 100); set => Set("准星不透明度", value); }
    public bool CrosshairOutline { get => B("准星描边", true); set => Set("准星描边", value); }
    /// <summary>跟随游戏窗口位置：以游戏窗口中心为准，否则以主屏幕中心为准。</summary>
    public bool CrosshairFollowGame { get => B("准星跟游戏", true); set => Set("准星跟游戏", value); }
    public int CrosshairOffsetX { get => I("准星X位置", 0, -4000, 4000); set => Set("准星X位置", value); }
    public int CrosshairOffsetY { get => I("准星Y位置", 0, -4000, 4000); set => Set("准星Y位置", value); }
    public bool CrosshairOnlyForeground { get => B("准星仅前台", true); set => Set("准星仅前台", value); }
    public bool CrosshairHideFromCapture { get => B("准星防截屏", false); set => Set("准星防截屏", value); }

    // ───────── 计时设置 ─────────
    public bool TimerEnabled { get => B("计时使能", false); set => Set("计时使能", value); }
    /// <summary>字号（磅）。原版默认 14。</summary>
    public int TimerFontSize { get => I("计时大小", 14, 8, 96); set => Set("计时大小", value); }
    public string TimerColor { get => S("计时颜色RGB", "FFC4C4"); set => Set("计时颜色RGB", value); }
    public int TimerOpacity { get => I("计时不透明度", 100, 10, 100); set => Set("计时不透明度", value); }
    /// <summary>相对游戏窗口（没有则为主屏幕）顶部中央的偏移。原版默认 Y = 80。</summary>
    public int TimerOffsetX { get => I("计时X位置", 0, -4000, 4000); set => Set("计时X位置", value); }
    public int TimerOffsetY { get => I("计时Y位置", 80, -4000, 4000); set => Set("计时Y位置", value); }
    public bool TimerHideHours { get => B("计时无小时", false); set => Set("计时无小时", value); }
    public bool TimerHideWhenIdle { get => B("计时闲时隐藏", false); set => Set("计时闲时隐藏", value); }
    public bool TimerHideFromCapture { get => B("计时防截屏", false); set => Set("计时防截屏", value); }
    /// <summary>改变显示字体。原版默认黑体，这里默认等宽的 Consolas，数字跳动时宽度不变。</summary>
    public string TimerFont { get => S("计时字体名", "Consolas"); set => Set("计时字体名", value); }
    /// <summary>自定义个性签名：显示在计时器下面的一行小字，留空不显示。</summary>
    public string TimerSignature { get => S("计时签名", ""); set => Set("计时签名", value.Length > 40 ? value[..40] : value); }
    /// <summary>记忆当前时间：保存下来的计时器读数（毫秒）。</summary>
    public long TimerMemory
    {
        get => long.TryParse(S("计时记毫秒", "0"), out var v) ? Math.Max(0, v) : 0;
        private set => _settings.Set("扩展", "计时记毫秒", value.ToString());
    }

    private bool B(string key, bool fallback) => _settings.GetBool("扩展", key, fallback);
    private int I(string key, int fallback, int min, int max) => Math.Clamp(_settings.GetInt("扩展", key, fallback), min, max);
    private string S(string key, string fallback) => _settings.Get("扩展", key, fallback);
    private void Set(string key, bool value) { _settings.SetBool("扩展", key, value); Refresh(); }
    private void Set(string key, int value) { _settings.SetInt("扩展", key, value); Refresh(); }
    private void Set(string key, string value) { _settings.Set("扩展", key, value); Refresh(); }

    /// <summary>设置变化后调用：需要时启动绘制线程，并让浮窗重画。</summary>
    public void Refresh()
    {
        _dirty = true;
        if ((CrosshairEnabled || TimerEnabled) && _thread is null) Start();
    }

    // ───────── 计时器 ─────────
    public TimerState TimerState => _timerState;

    public long TimerMilliseconds => _timerState switch
    {
        TimerState.Running => Environment.TickCount64 - _timerStart,
        TimerState.Stopped or TimerState.Paused => _timerElapsed,
        _ => 0,
    };

    private void Changed()
    {
        _dirty = true;
        TimerChanged?.Invoke();
    }

    /// <summary>开始计时：从零开始。</summary>
    public void StartTimer()
    {
        _timerStart = Environment.TickCount64;
        _timerState = TimerState.Running;
        Changed();
    }

    /// <summary>结束计时：停在当前读数上。</summary>
    public void StopTimer()
    {
        if (_timerState is TimerState.Running) _timerElapsed = Environment.TickCount64 - _timerStart;
        if (_timerState is not TimerState.Idle) _timerState = TimerState.Stopped;
        Changed();
    }

    /// <summary>重置计时：回到零。</summary>
    public void ResetTimer()
    {
        _timerElapsed = 0;
        _timerState = TimerState.Idle;
        Changed();
    }

    /// <summary>暂停计时：停住读数，之后可以「恢复计时」接着走。</summary>
    public void PauseTimer()
    {
        if (_timerState != TimerState.Running) return;
        _timerElapsed = Environment.TickCount64 - _timerStart;
        _timerState = TimerState.Paused;
        Changed();
    }

    /// <summary>恢复计时：从当前读数接着走（暂停、结束之后都可以）。</summary>
    public void ResumeTimer()
    {
        if (_timerState is not (TimerState.Paused or TimerState.Stopped)) return;
        _timerStart = Environment.TickCount64 - _timerElapsed;
        _timerState = TimerState.Running;
        Changed();
    }

    /// <summary>记忆当前时间。</summary>
    public void SaveTimerMemory() => TimerMemory = TimerMilliseconds;

    /// <summary>读取记忆时间 / 手动调整时间：把读数设为指定值，保持原来的走停状态（空闲时变为已停止）。</summary>
    public void SetTimer(long milliseconds)
    {
        milliseconds = Math.Max(0, milliseconds);
        if (_timerState == TimerState.Running)
        {
            _timerStart = Environment.TickCount64 - milliseconds;
        }
        else
        {
            _timerElapsed = milliseconds;
            if (_timerState == TimerState.Idle) _timerState = TimerState.Stopped;
        }
        Changed();
    }

    /// <summary>解析「时:分:秒.百分秒」「分:秒」这类写法，对应原版计时窗口的「恢复时间」。无法识别返回 null。</summary>
    public static long? ParseTime(string text)
    {
        text = text.Trim().Replace('：', ':');
        if (text.Length == 0) return null;
        var fraction = 0L;
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            var digits = text[(dot + 1)..];
            if (digits.Length is 0 or > 3 || !digits.All(char.IsAsciiDigit)) return null;
            fraction = long.Parse(digits.PadRight(3, '0'));
            text = text[..dot];
        }
        var parts = text.Split(':');
        if (parts.Length is 0 or > 3) return null;
        long seconds = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit) || !long.TryParse(part, out var value)) return null;
            seconds = seconds * 60 + value;
        }
        return seconds * 1000 + fraction;
    }

    /// <summary>_计时按钮_被单击：开始计时 → 结束计时 → 重置计时，三态循环。</summary>
    public TimerState AdvanceTimer()
    {
        switch (_timerState)
        {
            case TimerState.Idle: StartTimer(); break;
            case TimerState.Running: StopTimer(); break;
            default: ResetTimer(); break;
        }
        return _timerState;
    }

    /// <summary>时间转换：把毫秒格式化成 00:00:00.00（或不带小时）。</summary>
    public string FormatTimer(long ms)
    {
        var total = ms / 1000;
        var centis = ms % 1000 / 10;
        return TimerHideHours
            ? $"{total / 60:00}:{total % 60:00}.{centis:00}"
            : $"{total / 3600:00}:{total % 3600 / 60:00}:{total % 60:00}.{centis:00}";
    }

    // ───────── 绘制线程 ─────────

    private void Start()
    {
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "QuellGTA.Overlay" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
        _thread = null;
    }

    private void Loop()
    {
        using var crosshair = new LayeredWindow();
        using var timer = new LayeredWindow();
        string crosshairKey = "", timerKey = "";
        var tick = 0;

        while (!_stop)
        {
            Thread.Sleep(_timerState == TimerState.Running && TimerEnabled ? 33 : 100);
            Native.NativeMethods.MSG msg;
            while (PeekMessageW(out msg, 0, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }

            try
            {
                // 游戏窗口的位置每 100 毫秒左右查一次就够了
                var forceLayout = ++tick % 3 == 0 || _dirty;
                _dirty = false;
                var anchor = forceLayout ? GetAnchor() : _lastAnchor;
                _lastAnchor = anchor;

                UpdateCrosshair(crosshair, anchor, ref crosshairKey);
                UpdateTimer(timer, anchor, ref timerKey);
            }
            catch (Exception) { }
        }
    }

    private (RECT area, bool isGame, bool foreground) _lastAnchor;

    /// <summary>浮窗定位用的参照区域：游戏窗口的客户区，找不到时用主屏幕。</summary>
    private (RECT area, bool isGame, bool foreground) GetAnchor()
    {
        var hwnd = _game.GetWindowHandle();
        if (hwnd != 0 && !Native.NativeMethods.IsIconic(hwnd) && GetClientRect(hwnd, out var client) && client.Right > 0)
        {
            var origin = new POINT();
            ClientToScreen(hwnd, ref origin);
            var area = new RECT
            {
                Left = origin.X, Top = origin.Y, Right = origin.X + client.Right, Bottom = origin.Y + client.Bottom,
            };
            return (area, true, _game.IsForeground());
        }
        return (new RECT { Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) }, false, false);
    }

    private static RECT Screen => new() { Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };

    private static Color ParseColor(string rgb, int opacityPercent)
    {
        var value = 0xFFFFFF;
        try { value = Convert.ToInt32(rgb.TrimStart('#'), 16); } catch (Exception) { }
        return Color.FromArgb(255 * Math.Clamp(opacityPercent, 0, 100) / 100, (value >> 16) & 255, (value >> 8) & 255, value & 255);
    }

    private void UpdateCrosshair(LayeredWindow window, (RECT area, bool isGame, bool foreground) anchor, ref string key)
    {
        if (!CrosshairEnabled || SuppressCrosshair || (CrosshairOnlyForeground && !anchor.foreground))
        {
            window.Hide();
            key = "";
            return;
        }

        var scale = GetDpiForSystem() / 96f;
        var size = CrosshairSize * scale;
        var thickness = Math.Max(1f, CrosshairThickness * scale);
        var area = CrosshairFollowGame ? anchor.area : Screen;
        var cx = (area.Left + area.Right) / 2 + CrosshairOffsetX;
        var cy = (area.Top + area.Bottom) / 2 + CrosshairOffsetY;
        var newKey = $"{CrosshairShape}|{size}|{thickness}|{CrosshairColor}|{CrosshairOpacity}|{CrosshairOutline}|{CrosshairHideFromCapture}|{CrosshairText}";

        var extent = (int)Math.Ceiling(size + thickness + 3);
        var side = extent * 2 + 1;
        if (newKey != key)
        {
            key = newKey;
            using var bitmap = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                var color = ParseColor(CrosshairColor, CrosshairOpacity);
                var c = extent + 0.5f;
                if (CrosshairShape == CrosshairShape.Text)
                {
                    // 字符准星：把文字画在正中间，字高约等于准星直径
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    using var family = new FontFamily("Segoe UI");
                    using var glyphs = new GraphicsPath();
                    glyphs.AddString(CrosshairText.Length == 0 ? "+" : CrosshairText, family, (int)FontStyle.Bold, size * 2,
                        new PointF(0, 0), StringFormat.GenericTypographic);
                    var box = glyphs.GetBounds();
                    g.TranslateTransform(c - box.X - box.Width / 2, c - box.Y - box.Height / 2);
                    if (CrosshairOutline)
                    {
                        using var edge = new Pen(Color.FromArgb(color.A * 3 / 4, 0, 0, 0), 2 * scale) { LineJoin = LineJoin.Round };
                        g.DrawPath(edge, glyphs);
                    }
                    using var ink = new SolidBrush(color);
                    g.FillPath(ink, glyphs);
                }
                else
                {
                    if (CrosshairOutline)
                    {
                        using var shadow = new Pen(Color.FromArgb(color.A * 3 / 4, 0, 0, 0), thickness + 2 * scale);
                        DrawCrosshair(g, shadow, CrosshairShape, c, size, thickness + 2 * scale);
                    }
                    using var pen = new Pen(color, thickness);
                    DrawCrosshair(g, pen, CrosshairShape, c, size, thickness);
                }
            }
            window.SetCaptureHidden(CrosshairHideFromCapture);
            window.Render(bitmap, cx - extent, cy - extent);
        }
        else
        {
            window.MoveTo(cx - extent, cy - extent);
        }
    }

    private static void DrawCrosshair(Graphics g, Pen pen, CrosshairShape shape, float c, float size, float thickness)
    {
        using var brush = new SolidBrush(pen.Color);
        void Dot(float radius) => g.FillEllipse(brush, c - radius, c - radius, radius * 2, radius * 2);
        void Cross(float gap)
        {
            g.DrawLine(pen, c - size, c, c - gap, c);
            g.DrawLine(pen, c + gap, c, c + size, c);
            g.DrawLine(pen, c, c - size, c, c - gap);
            g.DrawLine(pen, c, c + gap, c, c + size);
        }

        switch (shape)
        {
            case CrosshairShape.Dot:
                Dot(Math.Max(thickness, size / 4));
                break;
            case CrosshairShape.Circle:
                g.DrawEllipse(pen, c - size, c - size, size * 2, size * 2);
                Dot(thickness);
                break;
            case CrosshairShape.CrossDot:
                Cross(size / 3);
                Dot(thickness);
                break;
            case CrosshairShape.CrossGap:
                Cross(size / 3);
                break;
            default:
                Cross(0);
                break;
        }
    }

    private void UpdateTimer(LayeredWindow window, (RECT area, bool isGame, bool foreground) anchor, ref string key)
    {
        if (!TimerEnabled || SuppressTimer || (TimerHideWhenIdle && _timerState == TimerState.Idle))
        {
            window.Hide();
            key = "";
            return;
        }

        var text = FormatTimer(TimerMilliseconds);
        var scale = GetDpiForSystem() / 96f;
        var fontPx = TimerFontSize * 96f / 72f * scale;
        var signature = TimerSignature;
        var newKey = $"{text}|{fontPx}|{TimerColor}|{TimerOpacity}|{TimerHideFromCapture}|{_timerState}|{TimerFont}|{signature}";
        var area = anchor.area;
        var centerX = (area.Left + area.Right) / 2 + TimerOffsetX;
        var top = area.Top + TimerOffsetY;

        if (newKey == key)
        {
            window.MoveCentered(centerX, top);
            return;
        }
        key = newKey;

        FontFamily family;
        try { family = new FontFamily(TimerFont); }
        catch (ArgumentException) { family = new FontFamily("Consolas"); } // 字体不存在时退回默认
        using var _ = family;
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, fontPx, new PointF(0, 0), StringFormat.GenericTypographic);
        if (signature.Length > 0)
        {
            // 个性签名：计时器下面居中的一行小字
            var timeBounds = path.GetBounds();
            using var line = new GraphicsPath();
            line.AddString(signature, family, (int)FontStyle.Regular, fontPx * 0.5f, new PointF(0, 0), StringFormat.GenericTypographic);
            var lineBounds = line.GetBounds();
            using var shift = new Matrix();
            shift.Translate(timeBounds.X + (timeBounds.Width - lineBounds.Width) / 2 - lineBounds.X,
                timeBounds.Bottom + fontPx * 0.25f - lineBounds.Y);
            line.Transform(shift);
            path.AddPath(line, false);
        }
        var bounds = path.GetBounds();
        var pad = (int)Math.Ceiling(3 * scale) + 2;
        var width = (int)Math.Ceiling(bounds.Width) + pad * 2;
        var height = (int)Math.Ceiling(bounds.Height) + pad * 2;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.TranslateTransform(pad - bounds.X, pad - bounds.Y);
            var color = ParseColor(TimerColor, TimerOpacity);
            // 停止 / 暂停后的时间用白色显示，和正在走的时间区分开
            if (_timerState is TimerState.Stopped or TimerState.Paused) color = Color.FromArgb(color.A, 255, 255, 255);
            using var outline = new Pen(Color.FromArgb(color.A * 4 / 5, 0, 0, 0), Math.Max(2f, 2.5f * scale)) { LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(color);
            g.DrawPath(outline, path);
            g.FillPath(fill, path);
        }
        window.SetCaptureHidden(TimerHideFromCapture);
        window.Render(bitmap, centerX - width / 2, top);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Native.NativeMethods.MSG msg, nint hwnd, uint min, uint max, uint remove);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Native.NativeMethods.MSG msg);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Native.NativeMethods.MSG msg);

    /// <summary>一个鼠标穿透、不抢焦点的置顶分层窗口，内容由 32 位带透明度的位图直接提供。</summary>
    private sealed class LayeredWindow : IDisposable
    {
        private readonly nint _hwnd;
        private bool _visible;
        private bool? _captureHidden;
        private int _x = int.MinValue, _y = int.MinValue, _width, _height;

        public LayeredWindow()
        {
            _hwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "Static", null, WS_POPUP, 0, 0, 1, 1, 0, 0, 0, 0);
        }

        public void SetCaptureHidden(bool hidden)
        {
            if (_captureHidden == hidden) return;
            _captureHidden = hidden;
            SetWindowDisplayAffinity(_hwnd, hidden ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        }

        public void Render(Bitmap bitmap, int x, int y)
        {
            var screenDc = GetDC(0);
            var memoryDc = CreateCompatibleDC(screenDc);
            var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(memoryDc, hBitmap);
            try
            {
                var destination = new POINT { X = x, Y = y };
                var size = new SIZE { Cx = bitmap.Width, Cy = bitmap.Height };
                var source = new POINT();
                var blend = new BLENDFUNCTION { Op = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(_hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
                (_x, _y, _width, _height) = (x, y, bitmap.Width, bitmap.Height);
            }
            finally
            {
                SelectObject(memoryDc, old);
                DeleteObject(hBitmap);
                DeleteDC(memoryDc);
                ReleaseDC(0, screenDc);
            }
            if (!_visible)
            {
                ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                _visible = true;
            }
            // 其他置顶窗口可能盖上来，重新声明一次置顶
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public void MoveTo(int x, int y)
        {
            if (!_visible || (x == _x && y == _y)) return;
            (_x, _y) = (x, y);
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public void MoveCentered(int centerX, int top) => MoveTo(centerX - _width / 2, top);

        public void Hide()
        {
            if (!_visible) return;
            ShowWindow(_hwnd, SW_HIDE);
            _visible = false;
        }

        public void Dispose()
        {
            if (_hwnd != 0) DestroyWindow(_hwnd);
        }
    }
}
