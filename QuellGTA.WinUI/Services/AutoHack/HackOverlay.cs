using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace QuellGTA.Services.AutoHack;

// 识别叠加层：原版每个小游戏各有一个用颜色键透明的置顶窗口（OverlayWindowProc / PublishOverlay），
// 把识别结果（框、标签、光标、标记）画在游戏画面上，方便看清程序认出了什么。
// 这里统一成一个分层窗口：模块只描述「画什么」（HackScene，屏幕坐标），绘制和窗口管理在 HackOverlayWindow 里。

public enum HackShapeKind { Rect, FillRect, Ellipse, FillEllipse, Line, Text }

/// <summary>
/// 一个图元，坐标都是屏幕像素。
/// Rect / FillRect / Ellipse / FillEllipse：(X, Y) 左上角，(W, H) 宽高。
/// Line：从 (X, Y) 到 (X + W, Y + H)。
/// Text：以 (X, Y) 为中心；FontPx 是字高（像素），Thickness &gt; 0 时文字带深色描边。
/// Argb 是 0xAARRGGBB。
/// </summary>
public readonly record struct HackShape(HackShapeKind Kind, float X, float Y, float W, float H, uint Argb,
    float Thickness = 2, string? Text = null, float FontPx = 16);

/// <summary>一帧叠加层的内容。</summary>
public sealed class HackScene
{
    public List<HackShape> Shapes { get; } = [];
    public bool IsEmpty => Shapes.Count == 0;

    public HackScene Rect(float x, float y, float w, float h, uint argb, float thickness = 2)
    { Shapes.Add(new(HackShapeKind.Rect, x, y, w, h, argb, thickness)); return this; }

    public HackScene FillRect(float x, float y, float w, float h, uint argb)
    { Shapes.Add(new(HackShapeKind.FillRect, x, y, w, h, argb, 0)); return this; }

    public HackScene Ellipse(float cx, float cy, float rx, float ry, uint argb, float thickness = 2)
    { Shapes.Add(new(HackShapeKind.Ellipse, cx - rx, cy - ry, rx * 2, ry * 2, argb, thickness)); return this; }

    public HackScene FillEllipse(float cx, float cy, float rx, float ry, uint argb)
    { Shapes.Add(new(HackShapeKind.FillEllipse, cx - rx, cy - ry, rx * 2, ry * 2, argb, 0)); return this; }

    public HackScene Line(float x1, float y1, float x2, float y2, uint argb, float thickness = 2)
    { Shapes.Add(new(HackShapeKind.Line, x1, y1, x2 - x1, y2 - y1, argb, thickness)); return this; }

    public HackScene Text(float cx, float cy, string text, uint argb, float fontPx = 16, bool outline = true)
    { Shapes.Add(new(HackShapeKind.Text, cx, cy, 0, 0, argb, outline ? 1 : 0, text, fontPx)); return this; }
}

/// <summary>模块看到的叠加层。Enabled 为 false 时模块可以跳过准备图元的工作。</summary>
public interface IHackOverlay
{
    bool Enabled { get; }
    /// <summary>换成新的一帧内容（空内容等于 Clear）。可以从任何线程调用，很快返回。</summary>
    void Show(HackScene scene);
    void Clear();
}

/// <summary>什么都不画的叠加层（测试和关闭时用）。</summary>
public sealed class NullHackOverlay : IHackOverlay
{
    public static readonly NullHackOverlay Instance = new();
    public bool Enabled => false;
    public void Show(HackScene scene) { }
    public void Clear() { }
}

public static class HackFrameExtensions
{
    /// <summary>分析坐标（frame 像素）换成屏幕坐标。</summary>
    public static PointF ToScreen(this HackFrame frame, double x, double y) =>
        new((float)(frame.ScreenX + x * frame.ToScreenX), (float)(frame.ScreenY + y * frame.ToScreenY));

    /// <summary>分析坐标下的长度换成屏幕像素（按横向比例）。</summary>
    public static float ScaleToScreen(this HackFrame frame, double length) => (float)(length * frame.ToScreenX);
}

/// <summary>
/// 叠加层窗口：独立线程上的一个鼠标穿透置顶分层窗口，按最新的 HackScene 重画。
/// 窗口总是从截屏中排除，免得程序自己截到自己画的框，干扰识别。
/// </summary>
public sealed class HackOverlayWindow(Func<bool> enabled) : IHackOverlay, IDisposable
{
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _gate = new();
    private HackScene? _pending;
    private bool _hasPending, _stop;
    private Thread? _thread;

    public bool Enabled => enabled();

    public void Show(HackScene scene)
    {
        if (!Enabled && !scene.IsEmpty) scene = new HackScene();
        lock (_gate)
        {
            _pending = scene;
            _hasPending = true;
            if (_thread is null && !scene.IsEmpty)
            {
                _stop = false;
                _thread = new Thread(Loop) { IsBackground = true, Name = "QuellGTA.HackOverlay" };
                _thread.Start();
            }
        }
        _signal.Set();
    }

    public void Clear() => Show(new HackScene());

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            _stop = true;
            thread = _thread;
            _thread = null;
        }
        _signal.Set();
        thread?.Join(1000);
    }

    private void Loop()
    {
        using var window = new LayeredWindow();
        window.SetCaptureHidden(true);
        while (true)
        {
            _signal.WaitOne(50);
            while (PeekMessageW(out var msg, 0, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
            HackScene? scene;
            lock (_gate)
            {
                if (_stop) return;
                if (!_hasPending) continue;
                scene = _pending;
                _hasPending = false;
            }
            try
            {
                if (scene is null || scene.IsEmpty) window.Hide();
                else Render(window, scene);
            }
            catch (Exception) { window.Hide(); }
        }
    }

    private static void Render(LayeredWindow window, HackScene scene)
    {
        // 先算出所有图元的外框，窗口只开这么大
        using var family = new FontFamily("Segoe UI");
        var paths = new Dictionary<int, GraphicsPath>();
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        void Include(RectangleF r, float pad)
        {
            left = Math.Min(left, r.Left - pad);
            top = Math.Min(top, r.Top - pad);
            right = Math.Max(right, r.Right + pad);
            bottom = Math.Max(bottom, r.Bottom + pad);
        }
        try
        {
            for (var i = 0; i < scene.Shapes.Count; i++)
            {
                var s = scene.Shapes[i];
                if (s.Kind == HackShapeKind.Text)
                {
                    if (string.IsNullOrEmpty(s.Text)) continue;
                    var path = new GraphicsPath();
                    path.AddString(s.Text, family, (int)FontStyle.Bold, Math.Max(6, s.FontPx), new PointF(0, 0), StringFormat.GenericTypographic);
                    var b = path.GetBounds();
                    using var move = new Matrix();
                    move.Translate(s.X - b.X - b.Width / 2, s.Y - b.Y - b.Height / 2);
                    path.Transform(move);
                    paths[i] = path;
                    Include(path.GetBounds(), 4);
                }
                else
                {
                    var x = Math.Min(s.X, s.X + s.W);
                    var y = Math.Min(s.Y, s.Y + s.H);
                    Include(new RectangleF(x, y, Math.Abs(s.W), Math.Abs(s.H)), s.Thickness + 2);
                }
            }
            if (left >= right || top >= bottom) { window.Hide(); return; }
            var originX = (int)Math.Floor(left);
            var originY = (int)Math.Floor(top);
            var width = Math.Clamp((int)Math.Ceiling(right) - originX, 1, 8192);
            var height = Math.Clamp((int)Math.Ceiling(bottom) - originY, 1, 8192);

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.TranslateTransform(-originX, -originY);
                for (var i = 0; i < scene.Shapes.Count; i++)
                {
                    var s = scene.Shapes[i];
                    var color = Color.FromArgb(unchecked((int)s.Argb));
                    switch (s.Kind)
                    {
                        case HackShapeKind.Rect:
                        {
                            using var pen = new Pen(color, Math.Max(1, s.Thickness));
                            g.DrawRectangle(pen, Math.Min(s.X, s.X + s.W), Math.Min(s.Y, s.Y + s.H), Math.Abs(s.W), Math.Abs(s.H));
                            break;
                        }
                        case HackShapeKind.FillRect:
                        {
                            using var brush = new SolidBrush(color);
                            g.FillRectangle(brush, Math.Min(s.X, s.X + s.W), Math.Min(s.Y, s.Y + s.H), Math.Abs(s.W), Math.Abs(s.H));
                            break;
                        }
                        case HackShapeKind.Ellipse:
                        {
                            using var pen = new Pen(color, Math.Max(1, s.Thickness));
                            g.DrawEllipse(pen, s.X, s.Y, s.W, s.H);
                            break;
                        }
                        case HackShapeKind.FillEllipse:
                        {
                            using var brush = new SolidBrush(color);
                            g.FillEllipse(brush, s.X, s.Y, s.W, s.H);
                            break;
                        }
                        case HackShapeKind.Line:
                        {
                            using var pen = new Pen(color, Math.Max(1, s.Thickness)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLine(pen, s.X, s.Y, s.X + s.W, s.Y + s.H);
                            break;
                        }
                        case HackShapeKind.Text when paths.TryGetValue(i, out var path):
                        {
                            if (s.Thickness > 0)
                            {
                                using var outline = new Pen(Color.FromArgb(color.A * 4 / 5, 0, 0, 0), Math.Max(2.5f, s.FontPx / 7)) { LineJoin = LineJoin.Round };
                                g.DrawPath(outline, path);
                            }
                            using var brush = new SolidBrush(color);
                            g.FillPath(brush, path);
                            break;
                        }
                    }
                }
            }
            window.Render(bitmap, originX, originY);
        }
        finally
        {
            foreach (var path in paths.Values) path.Dispose();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Native.NativeMethods.MSG msg, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Native.NativeMethods.MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Native.NativeMethods.MSG msg);
}
