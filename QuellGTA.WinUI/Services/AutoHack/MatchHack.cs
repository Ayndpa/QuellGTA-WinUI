using static QuellGTA.Services.AutoHack.HackUtil;

namespace QuellGTA.Services.AutoHack;

/// <summary>
/// 「连线配对」骇入（games/match_module.cpp）：上方是三位数的目标值，左边三个数码管数字，右边三个倍率图标（×1 / ×2 / ×10）。
/// 把每个数字连到一个倍率上，使「数字 × 倍率」之和等于目标值。
/// 算出解法后在叠加层上标出每个倍率图标认成了几（×1 / ×2 / ×10），并在目标值右边显示整条算式；
/// 同时通过 Solved 事件把算式交给日志。
/// </summary>
public sealed class MatchHack(IHackHost host) : IHackGame
{
    public string Name => "连线配对";

    /// <summary>算出解法时触发，参数形如「3x2 + 5x10 + 4x1 = 60」。</summary>
    public event Action<string>? Solved;

    /// <summary>最近一次从画面上读到的题目，无解时用来排查是哪里读错了。</summary>
    public string LastReading { get; private set; } = "";

    private readonly record struct RectI(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int CenterX => (Left + Right) / 2;
        public int CenterY => (Top + Bottom) / 2;
    }

    private sealed class Geometry
    {
        public double Scale;
        public readonly RectI[] TargetDigits = new RectI[3], LeftDigits = new RectI[3], MultiplierIcons = new RectI[3];
        public ulong WindowGeneration;
        public int FrameWidth, FrameHeight;
    }

    private readonly record struct VisualState(int LeftCurrent, int RightCurrent, int CompletedMask, int UsedMask);
    private enum InputPhase { None, EnteringRight, ConfirmingChoice }

    private Geometry? _detected;

    // ───────── 叠加层（原版的 OverlaySnapshot / PublishOverlay / SyncOverlayWindow / DrawOverlay） ─────────

    /// <summary>一个标签（OverlayText）：左边缘 x、竖直中心 y（屏幕坐标），字高，限制范围（游戏客户区），文字。</summary>
    private readonly record struct OverlayText(int ScreenX, int ScreenCenterY, int FontHeight, HackRect ScreenBounds, string Text);

    /// <summary>三个倍率标签 + 一条算式（OverlaySnapshot）。为 null 表示没有要显示的内容。</summary>
    private sealed record OverlaySnapshot(OverlayText[] Multipliers, OverlayText Equation);

    private OverlaySnapshot? _overlaySnapshot;
    /// <summary>上一次交给叠加层的图元；内容没变就不再重画，null 表示当前没显示。</summary>
    private List<HackShape>? _overlayShown;

    private static int AnalysisToScreenX(HackFrame frame, int x) => frame.ScreenX + Round(x * frame.ToScreenX);
    private static int AnalysisToScreenY(HackFrame frame, int y) => frame.ScreenY + Round(y * frame.ToScreenY);

    /// <summary>算式文字，例如「3x2 + 5x10 + 4x1 = 60」。</summary>
    private static string EquationText(int[] values, int[] multipliers, int[] solution, int target) =>
        string.Join(" + ", Enumerable.Range(0, 3).Select(i => $"{values[i]}x{multipliers[solution[i]]}")) + " = " + target;

    /// <summary>
    /// PublishOverlay：记下要画的标签。字号按目标值格子的屏幕高度定；倍率标签放在图标圆环右边，算式放在目标值第三格右边。
    /// 只是几次加减乘除，叠加层关着也照样记下，开关中途打开时能立刻显示（和原版一样）。
    /// </summary>
    private void PublishOverlay(HackFrame frame, Geometry geometry, int[] values, int[] multipliers, int[] solution, int target)
    {
        var targetScreenHeight = Math.Max(1, AnalysisToScreenY(frame, geometry.TargetDigits[0].Bottom) - AnalysisToScreenY(frame, geometry.TargetDigits[0].Top));
        var labelFont = Math.Clamp(Round(targetScreenHeight * .27), 15, 44);
        var equationFont = Math.Clamp(Round(targetScreenHeight * .25), 15, 40);
        var labelGap = Math.Max(8, Round(frame.ScreenH * .012));
        var equationGap = Math.Max(10, Round(frame.ScreenH * .022));
        var screenBounds = new HackRect(frame.ScreenX, frame.ScreenY, frame.ScreenX + frame.ScreenW, frame.ScreenY + frame.ScreenH);

        var labels = new OverlayText[3];
        for (var row = 0; row < 3; row++)
        {
            var icon = geometry.MultiplierIcons[row];
            var radius = Round(icon.Width / (2 * .78)); // 图标框是圆环半径的 0.78 倍，这里还原成圆环半径
            labels[row] = new OverlayText(AnalysisToScreenX(frame, icon.CenterX + radius) + labelGap,
                AnalysisToScreenY(frame, icon.CenterY), labelFont, screenBounds, "x" + multipliers[row]);
        }
        var lastTarget = geometry.TargetDigits[2];
        var equation = new OverlayText(AnalysisToScreenX(frame, lastTarget.Right) + equationGap,
            AnalysisToScreenY(frame, (lastTarget.Top + lastTarget.Bottom) / 2), equationFont, screenBounds,
            EquationText(values, multipliers, solution, target));
        _overlaySnapshot = new OverlaySnapshot(labels, equation);
    }

    /// <summary>ClearOverlayState：丢掉标签，正在显示的话收起来。</summary>
    private void ClearOverlayState()
    {
        _overlaySnapshot = null;
        HideOverlay();
    }

    private void HideOverlay()
    {
        if (_overlayShown is null) return;
        _overlayShown = null;
        host.Overlay.Clear();
    }

    /// <summary>
    /// SyncOverlayWindow：有标签且叠加层开着就显示，否则收起。每帧调用一次；
    /// 内容和上一次一样时什么也不做，所以叠加层窗口不会每帧重画。
    /// </summary>
    private void SyncOverlay()
    {
        var overlay = host.Overlay;
        if (_overlaySnapshot is not { } snapshot || !overlay.Enabled) { HideOverlay(); return; }
        var scene = new HackScene();
        foreach (var item in snapshot.Multipliers) DrawTag(scene, item, false);
        DrawTag(scene, snapshot.Equation, true);
        if (_overlayShown is not null && _overlayShown.SequenceEqual(scene.Shapes)) return;
        overlay.Show(scene);
        _overlayShown = scene.Shapes;
    }

    /// <summary>
    /// Segoe UI 粗体的大致字宽（以字高为单位），用来代替原版 DrawTextW(DT_CALCRECT) 量出的文字宽度。
    /// 标签只有数字、x、空格、+、=，按字符估算就够了。
    /// </summary>
    private static int EstimateTextWidth(string text, int fontHeight)
    {
        double em = 0;
        foreach (var c in text) em += c switch { ' ' => .27, 'x' => .52, '+' or '=' => .68, _ => .56 };
        return Math.Max(1, Round(em * fontHeight));
    }

    /// <summary>
    /// DrawOverlay 里的 drawTag：深色底的小牌子，外面一圈暗色光晕加一圈亮色细边，里面是带投影的文字。
    /// 倍率标签是青绿色，算式是红粉色。原版是圆角矩形，这里用直角矩形近似；原版背景的纯黑是颜色键（透明），不画。
    /// </summary>
    private static void DrawTag(HackScene scene, OverlayText item, bool equation)
    {
        var fontHeight = Math.Max(12, item.FontHeight);
        var textW = EstimateTextWidth(item.Text, fontHeight);
        var textH = Round(fontHeight * 1.33); // Segoe UI 的行高约为字高的 1.33 倍
        var padX = Math.Max(7, fontHeight / 2);
        var padY = Math.Max(4, fontHeight / 4);
        var left = item.ScreenX;
        var top = item.ScreenCenterY - (textH + padY * 2) / 2;
        var right = left + textW + padX * 2;
        var bottom = top + textH + padY * 2;

        // 标签不出游戏客户区：平时都在锚点右边，只有窗口化、空间不够时才挪进来
        var safeMargin = Math.Max(4, fontHeight / 4);
        int minLeft = item.ScreenBounds.Left + safeMargin, maxRight = item.ScreenBounds.Right - safeMargin;
        int minTop = item.ScreenBounds.Top + safeMargin, maxBottom = item.ScreenBounds.Bottom - safeMargin;
        void Offset(int dx, int dy) { left += dx; right += dx; top += dy; bottom += dy; }
        if (right > maxRight) Offset(maxRight - right, 0);
        if (left < minLeft) Offset(minLeft - left, 0);
        if (bottom > maxBottom) Offset(0, maxBottom - bottom);
        if (top < minTop) Offset(0, minTop - top);

        uint background = equation ? 0xFF0C1117u : 0xFF081616u;  // RGB(12,17,23) / RGB(8,22,22)
        uint glow = equation ? 0xFF3F121Eu : 0xFF003734u;        // RGB(63,18,30) / RGB(0,55,52)
        uint border = equation ? 0xFFFF4A66u : 0xFF3AEECFu;      // RGB(255,74,102) / RGB(58,238,207)
        uint shadow = equation ? 0xFF5A121Fu : 0xFF004E48u;      // RGB(90,18,31) / RGB(0,78,72)
        uint text = equation ? 0xFFFFEEF1u : 0xFF74FFE2u;        // RGB(255,238,241) / RGB(116,255,226)
        float w = right - left, h = bottom - top;
        scene.FillRect(left, top, w, h, background);
        scene.Rect(left, top, w, h, glow, Math.Max(3, fontHeight / 5));
        scene.Rect(left, top, w, h, border, Math.Max(1, fontHeight / 14));

        float cx = left + w / 2, cy = top + h / 2;
        float shadowOffset = Math.Max(1, fontHeight / 12);
        scene.Text(cx + shadowOffset, cy + shadowOffset, item.Text, shadow, fontHeight, outline: false);
        scene.Text(cx, cy, item.Text, text, fontHeight, outline: false);
    }

    // ───────── 像素工具 ─────────

    private static uint Pixel(HackFrame frame, int x, int y) =>
        x < 0 || y < 0 || x >= frame.Width || y >= frame.Height ? 0 : frame.Bgra[y * frame.Width + x];

    private static (int r, int g, int b) Rgb(uint pixel) => ((int)((pixel >> 16) & 255), (int)((pixel >> 8) & 255), (int)(pixel & 255));

    private static int Max3(HackFrame frame, int x, int y)
    {
        var (r, g, b) = Rgb(Pixel(frame, x, y));
        return Math.Max(r, Math.Max(g, b));
    }

    /// <summary>三个通道里最小的那个：白色线条高，彩色和暗色都低。</summary>
    private static double NeutralBrightness(HackFrame frame, int x, int y)
    {
        var (r, g, b) = Rgb(Pixel(frame, x, y));
        return Math.Min(r, Math.Min(g, b));
    }

    private static double MeanNeutralHorizontal(HackFrame frame, int y, int left, int right)
    {
        left = Math.Clamp(left, 0, frame.Width);
        right = Math.Clamp(right, 0, frame.Width);
        if (y < 0 || y >= frame.Height || right <= left) return 0;
        double sum = 0;
        for (var x = left; x < right; x++) sum += NeutralBrightness(frame, x, y);
        return sum / (right - left);
    }

    private static double MeanNeutralVertical(HackFrame frame, int x, int top, int bottom)
    {
        top = Math.Clamp(top, 0, frame.Height);
        bottom = Math.Clamp(bottom, 0, frame.Height);
        if (x < 0 || x >= frame.Width || bottom <= top) return 0;
        double sum = 0;
        for (var y = top; y < bottom; y++) sum += NeutralBrightness(frame, x, y);
        return sum / (bottom - top);
    }

    // ───────── 定位 ─────────

    /// <summary>目标值那个三格边框还在不在原位（比线条两侧亮得多）。</summary>
    private static bool AnchorsPresent(HackFrame frame, Geometry geometry)
    {
        if (geometry.TargetDigits[0].Height <= 0) return false;
        var delta = Math.Max(2, Round(frame.Height * .004));
        int top = geometry.TargetDigits[0].Top, bottom = geometry.TargetDigits[0].Bottom;
        int left = geometry.TargetDigits[0].Left - 1, right = geometry.TargetDigits[2].Right + 1;
        var inset = Math.Max(2, delta);
        var topContrast = MeanNeutralHorizontal(frame, top, left + inset, right - inset) -
            (MeanNeutralHorizontal(frame, top - delta, left + inset, right - inset) +
             MeanNeutralHorizontal(frame, top + delta, left + inset, right - inset)) * .5;
        var bottomContrast = MeanNeutralHorizontal(frame, bottom, left + inset, right - inset) -
            (MeanNeutralHorizontal(frame, bottom - delta, left + inset, right - inset) +
             MeanNeutralHorizontal(frame, bottom + delta, left + inset, right - inset)) * .5;
        if (topContrast + bottomContrast < 30) return false;
        double verticalContrast = 0;
        for (var i = 0; i < 4; i++)
        {
            var x = i == 0 ? left : geometry.TargetDigits[i - 1].Right + 1;
            verticalContrast += MeanNeutralVertical(frame, x, top + inset, bottom - inset) -
                (MeanNeutralVertical(frame, x - delta, top + inset, bottom - inset) +
                 MeanNeutralVertical(frame, x + delta, top + inset, bottom - inset)) * .5;
        }
        return verticalContrast >= 45;
    }

    private static Geometry? LocateGeometry(HackFrame frame)
    {
        if (frame.Width < 640 || frame.Height < 360 || frame.Bgra.Length != frame.Width * frame.Height) return null;
        var centerX = frame.Width / 2;
        var delta = Math.Max(2, Round(frame.Height * .004));

        // 1. 目标值边框的上下两条横线：在画面上部找一对间距合适的亮线
        var hx1 = Math.Max(0, Round(centerX - frame.Height * .16));
        var hx2 = Math.Min(frame.Width, Round(centerX + frame.Height * .16));
        var hy1 = Math.Max(delta, Round(frame.Height * .05));
        var hy2 = Math.Min(frame.Height - delta - 1, Round(frame.Height * .30));
        double[] horizontal = new double[frame.Height], contrast = new double[frame.Height];
        for (var y = hy1 - delta; y <= hy2 + delta; y++) horizontal[y] = MeanNeutralHorizontal(frame, y, hx1, hx2);
        for (var y = hy1; y <= hy2; y++) contrast[y] = horizontal[y] - (horizontal[y - delta] + horizontal[y + delta]) * .5;
        int top = -1, bottom = -1;
        double bestHorizontal = 0;
        for (var y1 = hy1 + 1; y1 < hy2; y1++)
        {
            if (contrast[y1] < contrast[y1 - 1] || contrast[y1] <= contrast[y1 + 1]) continue;
            for (var y2 = y1 + 1; y2 < hy2; y2++)
            {
                var separation = (y2 - y1) / (double)frame.Height;
                if (separation < .085 || separation > .13 || contrast[y2] < contrast[y2 - 1] || contrast[y2] <= contrast[y2 + 1]) continue;
                var score = contrast[y1] + contrast[y2];
                if (score > bestHorizontal) { bestHorizontal = score; top = y1; bottom = y2; }
            }
        }
        if (top < 0 || bestHorizontal < 35) return null;

        // 2. 四条竖线：等间距、居中
        var vx1 = Math.Max(delta, Round(centerX - frame.Height * .22));
        var vx2 = Math.Min(frame.Width - delta - 1, Round(centerX + frame.Height * .22));
        double[] vertical = new double[frame.Width], vContrast = new double[frame.Width];
        for (var x = vx1 - delta; x <= vx2 + delta; x++) vertical[x] = MeanNeutralVertical(frame, x, top, bottom + 1);
        var peaks = new List<int>();
        for (var x = vx1; x <= vx2; x++) vContrast[x] = vertical[x] - (vertical[x - delta] + vertical[x + delta]) * .5;
        for (var x = vx1 + 1; x < vx2; x++)
        {
            if (vContrast[x] <= 15 || vContrast[x] < vContrast[x - 1] || vContrast[x] <= vContrast[x + 1]) continue;
            if (peaks.Count > 0 && x - peaks[^1] <= 2)
            {
                if (vContrast[x] > vContrast[peaks[^1]]) peaks[^1] = x;
            }
            else peaks.Add(x);
        }
        var edges = new int[4];
        double bestVertical = 0;
        foreach (var first in peaks)
            foreach (var last in peaks)
            {
                if (last <= first) continue;
                var pitch = (last - first) / 3.0;
                if (pitch / frame.Height < .055 || pitch / frame.Height > .105) continue;
                var candidate = new int[4];
                double error = 0;
                for (var i = 0; i < 4; i++)
                {
                    var expected = first + pitch * i;
                    // std::min_element：距离相同时取靠前的
                    var nearest = peaks[0];
                    foreach (var peak in peaks)
                        if (Math.Abs(peak - expected) < Math.Abs(nearest - expected)) nearest = peak;
                    candidate[i] = nearest;
                    error += Math.Abs(candidate[i] - expected) / pitch;
                }
                if (error > .15 || candidate[0] == candidate[1] || candidate[1] == candidate[2] || candidate[2] == candidate[3]) continue;
                var score = -20 * error;
                foreach (var edge in candidate) score += vContrast[edge];
                score -= Math.Abs((first + last) * .5 - centerX) * .05;
                if (score > bestVertical) { bestVertical = score; edges = candidate; }
            }
        if (bestVertical < 60) return null;

        var geometry = new Geometry { Scale = ((bottom - top) / 116.0 + (edges[3] - edges[0]) / (3.0 * 89.0)) * .5 };
        if (geometry.Scale <= .25) return null;
        for (var i = 0; i < 3; i++) geometry.TargetDigits[i] = new RectI(edges[i] + 1, top, edges[i + 1] - 1, bottom);
        var frameH = bottom - top;
        var frameW = Round((edges[3] - edges[0]) / 3.0);
        const double targetCellAspectExpected = 89.0 / 116.0;
        var targetCellAspect = frameW / (double)frameH;
        if (targetCellAspect < targetCellAspectExpected * .90 || targetCellAspect > targetCellAspectExpected * 1.10) return null;

        // 竖线要一直连到上下横线，排除碰巧出现的几条亮线
        var connectionInset = Math.Max(delta, Round(frameH * .06));
        var connectionSpan = Math.Max(connectionInset + 1, frameH / 3);
        double VerticalSegmentContrast(int x, int y1, int y2) =>
            MeanNeutralVertical(frame, x, y1, y2) -
            (MeanNeutralVertical(frame, x - delta, y1, y2) + MeanNeutralVertical(frame, x + delta, y1, y2)) * .5;
        foreach (var edge in edges)
        {
            var upper = VerticalSegmentContrast(edge, top + connectionInset, top + connectionSpan);
            var lower = VerticalSegmentContrast(edge, bottom - connectionSpan, bottom - connectionInset);
            if (upper < 15 || lower < 15) return null;
        }

        // 3. 左边三个数字的边框：在预期位置附近找「边线亮、两侧暗」的矩形
        var edgeOffset = Math.Max(3, Round(frameH * .05));
        double VerticalLine(int x, int y, int height)
        {
            if (x < 1 || x + 1 >= frame.Width || y < 0 || y + height > frame.Height) return 0.0;
            double sum = 0;
            for (var py = y; py < y + height; py++)
                sum += Math.Max(Max3(frame, x - 1, py), Math.Max(Max3(frame, x, py), Max3(frame, x + 1, py)));
            return sum / height;
        }
        double HorizontalLine(int x, int y, int width)
        {
            if (y < 1 || y + 1 >= frame.Height || x < 0 || x + width > frame.Width) return 0.0;
            double sum = 0;
            for (var px = x; px < x + width; px++)
                sum += Math.Max(Max3(frame, px, y - 1), Math.Max(Max3(frame, px, y), Max3(frame, px, y + 1)));
            return sum / width;
        }
        double RectangleScore(int x, int y)
        {
            if (x - edgeOffset < 1 || x + frameW + edgeOffset >= frame.Width ||
                y - edgeOffset < 1 || y + frameH + edgeOffset >= frame.Height) return -1.0;
            var edge = VerticalLine(x, y, frameH) + VerticalLine(x + frameW, y, frameH) +
                       HorizontalLine(x, y, frameW) + HorizontalLine(x, y + frameH, frameW);
            var nearby = (VerticalLine(x - edgeOffset, y, frameH) + VerticalLine(x + edgeOffset, y, frameH) +
                          VerticalLine(x + frameW - edgeOffset, y, frameH) + VerticalLine(x + frameW + edgeOffset, y, frameH) +
                          HorizontalLine(x, y - edgeOffset, frameW) + HorizontalLine(x, y + edgeOffset, frameW) +
                          HorizontalLine(x, y + frameH - edgeOffset, frameW) + HorizontalLine(x, y + frameH + edgeOffset, frameW)) * .5;
            return edge - nearby;
        }
        for (var row = 0; row < 3; row++)
        {
            var expectedX = Round(edges[0] - frameH * 3.04);
            var expectedY = Round(top + frameH * (1.27 + row * 2.01));
            var radiusX = Round(frameH * .45);
            var radiusY = Round(frameH * .25);
            var coarse = Math.Max(1, Round(geometry.Scale * 2));
            double bestScore = -1;
            int bestX = 0, bestY = 0;
            for (var y = expectedY - radiusY; y <= expectedY + radiusY; y += coarse)
                for (var x = expectedX - radiusX; x <= expectedX + radiusX; x += coarse)
                {
                    var score = RectangleScore(x, y);
                    if (score > bestScore) { bestScore = score; bestX = x; bestY = y; }
                }
            // 注意：循环的边界跟着 bestX / bestY 走（和原版一样），找到更好的位置后搜索范围会随之移动
            for (var y = bestY - coarse; y <= bestY + coarse; y++)
                for (var x = bestX - coarse; x <= bestX + coarse; x++)
                {
                    var score = RectangleScore(x, y);
                    if (score > bestScore) { bestScore = score; bestX = x; bestY = y; }
                }
            if (bestScore < 80) return null;
            geometry.LeftDigits[row] = new RectI(bestX, bestY, bestX + frameW, bestY + frameH);
        }

        // 4. 右边三个倍率图标外面的圆环
        double RingScore(int cx, int cy, int radius)
        {
            double edge = 0, inside = 0, outside = 0;
            var radialOffset = Math.Max(3, Round(frameH * .05));
            for (var i = 0; i < 32; i++)
            {
                var angle = 2 * Math.PI * i / 32;
                double cs = Math.Cos(angle), sn = Math.Sin(angle);
                edge += Max3(frame, Round(cx + radius * cs), Round(cy + radius * sn));
                inside += Max3(frame, Round(cx + (radius - radialOffset) * cs), Round(cy + (radius - radialOffset) * sn));
                outside += Max3(frame, Round(cx + (radius + radialOffset) * cs), Round(cy + (radius + radialOffset) * sn));
            }
            return (edge - (inside + outside) * .5) / 32;
        }
        for (var row = 0; row < 3; row++)
        {
            var expectedX = Round(edges[3] + frameH * 2.55);
            var expectedY = geometry.LeftDigits[row].CenterY;
            var searchX = Round(frameH * .70);
            var searchY = Round(frameH * .18);
            var minRadius = Round(frameH * .43);
            var maxRadius = Round(frameH * .58);
            var step = Math.Max(1, Round(geometry.Scale * 2));
            double bestScore = -1;
            int bestX = 0, bestY = 0, bestRadius = 0;
            for (var cy = expectedY - searchY; cy <= expectedY + searchY; cy += step)
                for (var cx = expectedX - searchX; cx <= expectedX + searchX; cx += step)
                    for (var radius = minRadius; radius <= maxRadius; radius += step)
                    {
                        var score = RingScore(cx, cy, radius);
                        if (score > bestScore) { bestScore = score; bestX = cx; bestY = cy; bestRadius = radius; }
                    }
            if (bestScore < 12) return null;
            var halfW = Round(bestRadius * .78);
            var halfH = Round(bestRadius * .82);
            geometry.MultiplierIcons[row] = new RectI(bestX - halfW, bestY - halfH, bestX + halfW, bestY + halfH);
        }
        geometry.WindowGeneration = frame.WindowGeneration;
        geometry.FrameWidth = frame.Width;
        geometry.FrameHeight = frame.Height;
        return geometry;
    }

    // ───────── 读数 ─────────

    private static double BrightRatio(HackFrame frame, RectI roi)
    {
        int left = Math.Clamp(roi.Left, 0, frame.Width), right = Math.Clamp(roi.Right, 0, frame.Width);
        int top = Math.Clamp(roi.Top, 0, frame.Height), bottom = Math.Clamp(roi.Bottom, 0, frame.Height);
        int bright = 0, total = 0;
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var (r, g, b) = Rgb(Pixel(frame, x, y));
                if (Math.Max(r, Math.Max(g, b)) >= 145 && r + g + b >= 300) bright++;
                total++;
            }
        return total > 0 ? bright / (double)total : 0;
    }

    // 七段数码管各段在格子里的位置（左、上、右、下，占格子的比例），顺序 a b c d e f g
    private static readonly double[][] Strips =
    [
        [.36, .17, .64, .24], [.66, .27, .77, .43], [.66, .58, .77, .74],
        [.36, .77, .64, .84], [.25, .58, .36, .74], [.25, .27, .36, .43],
        [.36, .47, .64, .55],
    ];
    private static readonly int[] SegmentMasks =
        [0b0111111, 0b0000110, 0b1011011, 0b1001111, 0b1100110, 0b1101101, 0b1111101, 0b0000111, 0b1111111, 0b1101111];

    /// <summary>读一个数码管数字；允许错一段，再多就返回 -1。</summary>
    private static int ReadSevenSegment(HackFrame frame, RectI box)
    {
        var mask = 0;
        for (var i = 0; i < 7; i++)
        {
            var s = Strips[i];
            var roi = new RectI(Round(box.Left + s[0] * box.Width), Round(box.Top + s[1] * box.Height),
                Round(box.Left + s[2] * box.Width), Round(box.Top + s[3] * box.Height));
            if (BrightRatio(frame, roi) > .12) mask |= 1 << i;
        }
        int best = -1, bestDistance = 8;
        for (var digit = 0; digit < 10; digit++)
        {
            var distance = System.Numerics.BitOperations.PopCount((uint)(mask ^ SegmentMasks[digit]));
            if (distance < bestDistance) { bestDistance = distance; best = digit; }
        }
        return bestDistance <= 1 ? best : -1;
    }

    /// <summary>倍率图标里白色文字的外接框：矮的是 ×2，窄的是 ×1，否则是 ×10。</summary>
    private static int ReadMultiplier(HackFrame frame, Geometry geometry, int row)
    {
        var roi = geometry.MultiplierIcons[row];
        int left = Math.Clamp(roi.Left, 0, frame.Width), right = Math.Clamp(roi.Right, 0, frame.Width);
        int top = Math.Clamp(roi.Top, 0, frame.Height), bottom = Math.Clamp(roi.Bottom, 0, frame.Height);
        int minX = right, minY = bottom, maxX = -1, maxY = -1, count = 0;
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var (r, g, b) = Rgb(Pixel(frame, x, y));
                if (Math.Min(r, Math.Min(g, b)) < 155 || r + g + b < 570) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); count++;
            }
        if (count < Math.Max(12, (int)(180 * geometry.Scale * geometry.Scale))) return -1;
        var width = (maxX - minX + 1) / geometry.Scale;
        var height = (maxY - minY + 1) / geometry.Scale;
        if (height < 52) return 2;
        if (width < 57) return 1;
        return 10;
    }

    private static bool ReadPuzzle(HackFrame frame, Geometry geometry, int[] values, int[] multipliers, out int target)
    {
        target = 0;
        for (var row = 0; row < 3; row++)
        {
            values[row] = ReadSevenSegment(frame, geometry.LeftDigits[row]);
            multipliers[row] = ReadMultiplier(frame, geometry, row);
            if (values[row] < 0 || multipliers[row] < 0) return false;
        }
        int d0 = ReadSevenSegment(frame, geometry.TargetDigits[0]), d1 = ReadSevenSegment(frame, geometry.TargetDigits[1]),
            d2 = ReadSevenSegment(frame, geometry.TargetDigits[2]);
        if (d0 < 0 || d1 < 0 || d2 < 0) return false;
        target = d0 * 100 + d1 * 10 + d2;
        return true;
    }

    /// <summary>区域里「有颜色」的程度：按小块取平均色，算色差，取最鲜艳的五分之一的平均。</summary>
    private static double MosaicChromaScore(HackFrame frame, RectI roi, int block)
    {
        int left = Math.Clamp(roi.Left, 0, frame.Width), right = Math.Clamp(roi.Right, 0, frame.Width);
        int top = Math.Clamp(roi.Top, 0, frame.Height), bottom = Math.Clamp(roi.Bottom, 0, frame.Height);
        block = Math.Max(1, block);
        var scores = new List<double>();
        for (var by = top; by + block <= bottom; by += block)
            for (var bx = left; bx + block <= right; bx += block)
            {
                double r = 0, g = 0, b = 0;
                for (var y = by; y < by + block; y++)
                    for (var x = bx; x < bx + block; x++)
                    {
                        var (pr, pg, pb) = Rgb(Pixel(frame, x, y));
                        r += pr; g += pg; b += pb;
                    }
                double count = block * block;
                r /= count; g /= count; b /= count;
                double high = Math.Max(r, Math.Max(g, b)), low = Math.Min(r, Math.Min(g, b));
                if (high > 25) scores.Add((high - low) / (high + 20));
            }
        if (scores.Count == 0) return 0;
        scores.Sort((a, b) => b.CompareTo(a));
        var take = Math.Max(1, scores.Count / 5);
        return scores.Take(take).Sum() / take;
    }

    /// <summary>三行里唯一明显带颜色的那一行（当前光标所在）；没有或不唯一返回 -1。</summary>
    private static int UniqueColoredRow(double[] scores)
    {
        var best = 0;
        for (var row = 1; row < 3; row++) if (scores[row] > scores[best]) best = row;
        double second = 0;
        for (var row = 0; row < 3; row++) if (row != best) second = Math.Max(second, scores[row]);
        return scores[best] > .18 && scores[best] - second > .12 ? best : -1;
    }

    private static VisualState ReadVisualState(HackFrame frame, Geometry geometry)
    {
        double[] leftScores = new double[3], rightScores = new double[3];
        int completedMask = 0, usedMask = 0;
        var block = Math.Max(1, Round(geometry.Scale * 4));
        var wireBlock = Math.Max(1, Round(geometry.Scale * 2));
        for (var row = 0; row < 3; row++)
        {
            var number = geometry.LeftDigits[row];
            var h = number.Height;
            var margin = Math.Max(2, Round(h * .06));
            leftScores[row] = MosaicChromaScore(frame,
                new RectI(number.Left - margin, number.Top - margin, number.Right + margin, number.Bottom + margin), block);
            var halfWire = Math.Max(2, Round(h * .06));
            // 数字右侧伸出的连线：有颜色说明这一行已经连好了
            var leftWire = MosaicChromaScore(frame,
                new RectI(number.Right + Round(h * .37), number.CenterY - halfWire, number.Right + Round(h * .67), number.CenterY + halfWire), wireBlock);
            var icon = geometry.MultiplierIcons[row];
            var circleRadius = Round(icon.Width / (2 * .78));
            var circleLeft = icon.CenterX - circleRadius;
            var circleMargin = Math.Max(2, Round(circleRadius * .08));
            rightScores[row] = MosaicChromaScore(frame,
                new RectI(icon.CenterX - circleRadius - circleMargin, icon.CenterY - circleRadius - circleMargin,
                    icon.CenterX + circleRadius + circleMargin, icon.CenterY + circleRadius + circleMargin), block);
            var rightWire = MosaicChromaScore(frame,
                new RectI(circleLeft - Round(h * .69), icon.CenterY - halfWire, circleLeft - Round(h * .34), icon.CenterY + halfWire), wireBlock);
            if (leftWire > .18) completedMask |= 1 << row;
            if (rightWire > .18) usedMask |= 1 << row;
        }
        return new VisualState(UniqueColoredRow(leftScores), UniqueColoredRow(rightScores), completedMask, usedMask);
    }

    // ───────── 求解 ─────────

    private static int BitCount3(int value) => (value & 1) + ((value >> 1) & 1) + ((value >> 2) & 1);

    private static int NextAvailableRow(int row, int usedMask)
    {
        for (var step = 1; step <= 3; step++)
        {
            var candidate = (row + step) % 3;
            if ((usedMask & (1 << candidate)) == 0) return candidate;
        }
        return -1;
    }

    private static readonly int[][] Permutations = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];

    /// <summary>返回每个左边数字该连到右边第几行；无解返回 null。</summary>
    public static int[]? MakeSolution(int[] values, int[] multipliers, int target)
    {
        foreach (var rows in Permutations)
        {
            var sum = 0;
            for (var i = 0; i < 3; i++) sum += values[i] * multipliers[rows[i]];
            if (sum == target) return rows;
        }
        return null;
    }

    private static bool SameLayout(Geometry a, Geometry b)
    {
        RectI ar = a.TargetDigits[0], br = b.TargetDigits[0];
        return Math.Abs(ar.Left - br.Left) <= 2 && Math.Abs(ar.Top - br.Top) <= 2 &&
               Math.Abs(a.TargetDigits[2].Right - b.TargetDigits[2].Right) <= 2 && Math.Abs(ar.Bottom - br.Bottom) <= 2;
    }

    private static bool MatchesFrame(Geometry geometry, HackFrame frame) =>
        geometry.WindowGeneration == frame.WindowGeneration && geometry.FrameWidth == frame.Width && geometry.FrameHeight == frame.Height;

    public bool DetectInGame()
    {
        var frame = host.Capture();
        _detected = frame is null ? null : LocateGeometry(frame);
        if (_detected is null) ClearOverlayState();
        return _detected is not null;
    }

    public void ResetInGameCache()
    {
        _detected = null;
        ClearOverlayState();
    }

    public bool RunSession(Func<bool> stopRequested, Action<string> status)
    {
        var geometry = _detected;
        int[]? solution = null;
        HackJob? inputJob = null;
        var inputPhase = InputPhase.None;
        VisualState state, previous = default;
        int absentFrames = 0, stableFrames = 0;
        bool havePrevious = false, awaitingRightSelection = false, awaitingTransition = false, stateReady = false;
        int oldCompleted = 0, oldUsed = 0, connectingLeft = -1, connectingRight = -1;
        int enterAttempts = 0, rightStableFrames = 0, previousRight = -1;
        long rightSelectionDeadline = 0, transitionDeadline = 0;
        var lastStatus = "";
        void SetStatus(string value)
        {
            if (lastStatus == value) return;
            lastStatus = value;
            status(value);
        }
        void ResetPlan()
        {
            solution = null; inputPhase = InputPhase.None;
            awaitingRightSelection = false; awaitingTransition = false; stateReady = false;
            enterAttempts = 0; rightStableFrames = 0; previousRight = -1;
            stableFrames = 0; havePrevious = false;
            ClearOverlayState();
        }
        void Cleanup()
        {
            host.CancelAll();
            ResetInGameCache(); // 里面会收起叠加层（原版 cleanup 里的 ClearOverlay）
        }
        void Wait(long started) => WaitForFrame(stopRequested, started, 50);

        _overlayShown = null; // 上一次会话结束后宿主已经清过叠加层
        ClearOverlayState();
        SetStatus("match: locating");
        while (!stopRequested())
        {
            var started = Now;
            SyncOverlay();
            var frame = host.Capture();
            if (frame is null)
            {
                geometry = null; _detected = null; ResetPlan();
                if (++absentFrames >= 3) break;
                Wait(started); continue;
            }
            var needsRelocation = geometry is null || !MatchesFrame(geometry, frame) || !AnchorsPresent(frame, geometry);
            if (needsRelocation)
            {
                var relocated = LocateGeometry(frame);
                if (relocated is null)
                {
                    geometry = null; _detected = null;
                    if (++absentFrames >= 3)
                    {
                        // 最后一对连上之后小游戏画面会直接消失，这种情况算完成
                        if (inputJob is not null && inputPhase == InputPhase.ConfirmingChoice && BitCount3(oldCompleted) == 2)
                        {
                            if (inputJob.Pending) { Wait(started); continue; }
                            if (inputJob.Succeeded)
                            {
                                SetStatus("match: completed"); Cleanup(); return true;
                            }
                        }
                        if (awaitingTransition && BitCount3(oldCompleted) == 2)
                        {
                            SetStatus("match: completed"); Cleanup(); return true;
                        }
                        SetStatus("match: minigame exited"); break;
                    }
                    Wait(started); continue;
                }
                if (geometry is null || !SameLayout(geometry, relocated) || !MatchesFrame(geometry, frame))
                {
                    host.CancelAll(); inputJob = null; ResetPlan();
                }
                geometry = relocated; _detected = relocated;
            }
            absentFrames = 0;

            if (inputJob is not null)
            {
                if (inputJob.Pending) { Wait(started); continue; }
                if (!inputJob.Succeeded)
                {
                    inputJob = null; ResetPlan(); SetStatus("match: analyzing");
                    Wait(started); continue;
                }
                inputJob = null;
                if (inputPhase == InputPhase.EnteringRight)
                {
                    inputPhase = InputPhase.None;
                    awaitingRightSelection = true;
                    rightSelectionDeadline = Now + Ms(200);
                    rightStableFrames = 0;
                    previousRight = -1;
                }
                else if (inputPhase == InputPhase.ConfirmingChoice)
                {
                    inputPhase = InputPhase.None;
                    awaitingTransition = true;
                    transitionDeadline = Now + Ms(6000);
                    stableFrames = 0; havePrevious = false;
                    SetStatus("match: verifying connection");
                }
                else ResetPlan();
            }

            state = ReadVisualState(frame, geometry!);
            if (awaitingRightSelection)
            {
                if (state.RightCurrent >= 0 && state.RightCurrent == previousRight) rightStableFrames++;
                else rightStableFrames = state.RightCurrent >= 0 ? 1 : 0;
                previousRight = state.RightCurrent;
                if (rightStableFrames >= 2)
                {
                    // 右侧光标的起始位置：和左边同一行，那一行已被占用时是它后面第一个空行
                    var selectedRight = (oldUsed & (1 << connectingLeft)) != 0 ? NextAvailableRow(connectingLeft, oldUsed) : connectingLeft;
                    var keys = new List<HackKey>();
                    for (var moves = 0; selectedRight != connectingRight && moves < 3; moves++)
                    {
                        keys.Add(new HackKey(0x28));
                        selectedRight = NextAvailableRow(selectedRight, oldUsed);
                    }
                    if (selectedRight != connectingRight)
                    {
                        ResetPlan(); SetStatus("match: analyzing");
                        Wait(started); continue;
                    }
                    keys.Add(new HackKey(0x0D));
                    var fg = host.ForegroundGameWindow(frame);
                    if (fg == 0) { Wait(started); continue; }
                    awaitingRightSelection = false;
                    inputPhase = InputPhase.ConfirmingChoice;
                    inputJob = host.QueueSequence(keys, fg);
                    Wait(started); continue;
                }
                if (Now >= rightSelectionDeadline)
                {
                    if (state.LeftCurrent == connectingLeft && enterAttempts < 3)
                    {
                        var fg = host.ForegroundGameWindow(frame);
                        if (fg == 0) { Wait(started); continue; }
                        awaitingRightSelection = false;
                        enterAttempts++;
                        inputPhase = InputPhase.EnteringRight;
                        inputJob = host.QueueSequence([new HackKey(0x0D)], fg);
                    }
                    else
                    {
                        // 没看到右侧光标就不发方向键：否则漏掉的回车会让方向键作用到左边去
                        Cleanup();
                        return false;
                    }
                }
                Wait(started); continue;
            }
            if (awaitingTransition)
            {
                var finalPair = BitCount3(oldCompleted) == 2;
                var connected = (state.CompletedMask & (1 << connectingLeft)) != 0 && (state.UsedMask & (1 << connectingRight)) != 0 &&
                                state.CompletedMask != oldCompleted && state.UsedMask != oldUsed;
                var advanced = finalPair || (state.LeftCurrent >= 0 && state.LeftCurrent != connectingLeft);
                if (connected && advanced && havePrevious && state == previous) stableFrames++;
                else stableFrames = connected && advanced ? 1 : 0;
                previous = state; havePrevious = true;
                if (stableFrames >= 3)
                {
                    awaitingTransition = false; stableFrames = 0; havePrevious = false;
                    stateReady = true;
                    if (BitCount3(state.CompletedMask) == 3)
                    {
                        SetStatus("match: completed"); Cleanup(); return true;
                    }
                    SetStatus("match: connecting");
                }
                else if (Now >= transitionDeadline)
                {
                    ResetPlan(); SetStatus("match: analyzing");
                }
                else
                {
                    Wait(started); continue;
                }
            }
            if (solution is null)
            {
                SetStatus("match: reading puzzle");
                int[] values = new int[3], multipliers = new int[3];
                if (!ReadPuzzle(frame, geometry!, values, multipliers, out var target))
                {
                    Wait(started); continue;
                }
                LastReading = $"数字 {string.Join(" ", values)}，倍率 {string.Join(" ", multipliers)}，目标 {target}";
                solution = MakeSolution(values, multipliers, target);
                if (solution is null)
                {
                    SetStatus("match: no solution"); Cleanup(); return false;
                }
                Solved?.Invoke(EquationText(values, multipliers, solution, target));
                PublishOverlay(frame, geometry!, values, multipliers, solution, target);
                SyncOverlay();
                SetStatus("match: connecting");
            }
            if (!stateReady)
            {
                if (havePrevious && state == previous) stableFrames++;
                else stableFrames = 1;
                previous = state; havePrevious = true;
                if (stableFrames < 3) { Wait(started); continue; }
                stateReady = true; stableFrames = 0; havePrevious = false;
            }
            if (BitCount3(state.CompletedMask) == 3)
            {
                SetStatus("match: completed"); Cleanup(); return true;
            }
            var leftRow = state.LeftCurrent;
            if (leftRow < 0)
                for (var row = 0; row < 3; row++)
                    if ((state.CompletedMask & (1 << row)) == 0) { leftRow = row; break; }
            if (leftRow < 0 || (state.CompletedMask & (1 << leftRow)) != 0)
            {
                Wait(started); continue;
            }
            var targetRight = solution[leftRow];
            var foreground = host.ForegroundGameWindow(frame);
            if (foreground == 0) { Wait(started); continue; }
            oldCompleted = state.CompletedMask; oldUsed = state.UsedMask;
            connectingLeft = leftRow; connectingRight = targetRight;
            enterAttempts = 1;
            inputPhase = InputPhase.EnteringRight;
            // 「进入右侧」和「在右侧移动」分成两步，各自确认：
            // 否则游戏漏掉回车时，后面的方向键会变成在左边移动，整个顺序就乱了
            inputJob = host.QueueSequence([new HackKey(0x0D)], foreground);
            stateReady = false;
            SetStatus("match: connecting");
            Wait(started);
        }
        Cleanup();
        return false;
    }
}
