using System.Diagnostics;
using static QuellGTA.Services.AutoHack.HackUtil;

namespace QuellGTA.Services.AutoHack;

/// <summary>
/// 「滑块」骇入（games/slider_module.cpp）：一条横贯的红线，8 根白色竖条，每根中间有个缺口。
/// 当前那根的缺口带黄色框并上下移动，要在缺口经过红线时按回车，然后轮到下一根。
/// 做法：先找红线和 8 根竖条，之后只截当前竖条附近的一小块，以约 60 次 / 秒追踪黄色缺口的位置和速度，
/// 算出缺口到达红线边缘的时刻，扣掉输入延迟后在那一刻按下回车；每按一次，用「下一根出现黄框的时刻」校准一次延迟。
/// 叠加层（原版的 CursorProc / MarksProc）：当前竖条左边一个跟着缺口上下移动的绿色箭头，缺口快到红线时再画一条指向红线边缘的引线；
/// 红线左端一个小信息框，显示分析频率、预计还有多久到达、端到端延迟。
/// </summary>
public sealed class SliderHack(IHackHost host) : IHackGame
{
    public string Name => "滑块";

    private const double BaselineScreenHeightPx = 1080.0;
    private const double AnalysisIntervalSeconds = 1.0 / 60.0;
    private const double DefaultEndToEndLatencySeconds = 0.060;
    private const double MaximumCalibratedLatencySeconds = 0.500;
    private const double EdgeTriggerZonePx = 80.0;
    private const double MinUsableVelocityPxPerSec = 25.0;
    private const double InvalidPredictionSeconds = 999.0;
    private const double YellowCenterStripHalfWidthPx = 8.0;
    private const double YellowHalfOverlapPx = 72.0;
    private const int CursorSize = 64;
    private const int CursorArrowTopOffset = 14;
    private const int CursorArrowBottomOffset = 14;
    private const int CursorArrowRightOffset = 14;
    private const uint OverlayGreen = 0xFF46FF78;          // RGB(70,255,120)
    private const uint OverlayTextGreen = 0xFF91FFAF;      // RGB(145,255,175)
    private const uint OverlayWarningOrange = 0xFFFFAE48;  // RGB(255,174,72)
    private const uint OverlayBlack = 0xFF0A0E14;          // RGB(10,14,20)

    private static int ScaledPx(double value, double scale) => Math.Max(1, Round(value * Math.Clamp(scale, 0.45, 2.25)));

    private readonly record struct RectI(int X, int Y, int W, int H);

    private struct RedBar
    {
        public bool Ok;
        public int X1, X2, Y1, Y2, CenterY, Height;
    }

    private struct BarMeasure
    {
        public bool Ok;
        public int X1, X2, TopY1, TopY2, BottomY1, BottomY2;
        public double GapCenterY;
    }

    private sealed class FrameAnalysis
    {
        public bool Ok, InMinigame;
        public RedBar Red;
        public List<BarMeasure> Bars = [];
    }

    private struct YellowMeasure
    {
        public bool Ok;
        public int Index, Score, TopBottomY, BottomTopY;
        public double GapCenterY;
    }

    private sealed class SearchCells
    {
        public bool Ok;
        public List<(int first, int second)> XRanges = [];
    }

    /// <summary>截到的一帧，坐标换算信息加上按游戏高度算出的缩放比例。</summary>
    private sealed class CaptureFrame
    {
        public int X, Y, W, H;
        public double ToScreenX = 1, ToScreenY = 1, AnalysisGeometryScale = 1, ScreenGeometryScale = 1;
        public ulong WindowGeneration;
        public uint[] Bgra = [];
    }

    private readonly record struct TrackSample(double Center, long Time, int TopBottomY, int BottomTopY);

    private sealed class TrackSlot
    {
        public bool Valid;
        public double LastCenter, Velocity;
        public List<TrackSample> History = [];
    }

    private sealed class PostPressCalibration
    {
        public bool Active;
        public int BarIndex = -1;
        public HackJob? InputJob;
        public long ScheduledAt;
    }

    private sealed class InGameCache
    {
        public bool Valid;
        public ulong WindowGeneration;
        public RedBar Red;
        public List<BarMeasure> Bars = [];
        public SearchCells Cells = new();
        public double GeometryScale = 1.0;
    }

    private InGameCache _cache = new();

    // ───────── 叠加层（原版的 g_cursor* / g_hud* / g_preview 和 CursorProc / MarksProc） ─────────

    /// <summary>叠加层要显示的状态，坐标都是屏幕坐标。原版放在一堆原子变量里，由两个窗口的定时器去读。</summary>
    private struct OverlayState
    {
        // 光标（g_cursorVisible / g_cursorX / g_cursorY / g_cursorTargetY / g_cursorInZone）
        public bool CursorVisible, CursorInZone;
        public int CursorX, CursorY, CursorTargetY;
        // 信息框（g_preview 的 bars / red / scale，以及 g_hudAnalysisHz10 / g_hudTtcMs / g_hudEndToEndMs）
        public bool PreviewHasBars;
        public RedBar PreviewRed;
        public double PreviewScale;
        public int AnalysisHz10, TtcMs, EndToEndMs;
    }

    /// <summary>上一次交给叠加层的图元；内容没变就不再重画，null 表示当前没显示。</summary>
    private List<HackShape>? _overlayShown;

    /// <summary>按状态画一帧：MarksProc 的信息框 + CursorProc 的箭头和引线。原版两个窗口会被夹在虚拟桌面以内，这里不夹。</summary>
    private static HackScene BuildOverlayScene(in OverlayState state)
    {
        var scene = new HackScene();
        if (state.PreviewHasBars)
        {
            // 信息框：红线左端外侧，深色底绿边，三行小字（原版是 16 像素高的 Consolas，不随画面缩放）
            var infoW = ScaledPx(104, state.PreviewScale);
            var infoH = ScaledPx(62, state.PreviewScale) / 2 * 2;
            var gap = ScaledPx(12, state.PreviewScale);
            var infoRight = state.PreviewRed.X1 - gap;
            var infoTop = state.PreviewRed.CenterY - infoH / 2;
            var infoLeft = infoRight - infoW;
            scene.FillRect(infoLeft, infoTop, infoW, infoH, OverlayBlack);
            scene.Rect(infoLeft, infoTop, infoW, infoH, OverlayGreen, 2);
            var textX = infoLeft + ScaledPx(8, state.PreviewScale);
            var hzText = "hz " + state.AnalysisHz10 / 10 + "." + Math.Abs(state.AnalysisHz10 % 10);
            var ttcText = "ttc " + state.TtcMs + "ms";
            var endToEndText = state.EndToEndMs >= 0 ? "e2e " + state.EndToEndMs + "ms" : "e2e --";
            // 分析频率低于 30 次 / 秒时用橙色提醒
            var hzColor = state.AnalysisHz10 is > 0 and < 300 ? OverlayWarningOrange : OverlayTextGreen;
            InfoLine(scene, textX, infoTop + ScaledPx(5, state.PreviewScale), hzText, hzColor);
            InfoLine(scene, textX, infoTop + ScaledPx(23, state.PreviewScale), ttcText, OverlayTextGreen);
            InfoLine(scene, textX, infoTop + ScaledPx(41, state.PreviewScale), endToEndText, OverlayTextGreen);
        }
        if (state.CursorVisible)
        {
            // 光标窗口 64 像素宽，左边缘在 cursorX - 32；里面的坐标照搬原版（不随画面缩放）
            var wx = state.CursorX - CursorSize / 2;
            int cy = state.CursorY, ty = state.CursorTargetY;
            var arrowTop = cy - CursorArrowTopOffset;
            var arrowBottom = cy + CursorArrowBottomOffset;
            var edgeY = ty < cy ? arrowTop : arrowBottom;
            var docked = Math.Abs(ty - edgeY) <= 2;
            if (state.CursorInZone && !docked)
            {
                // 缺口快到红线：从箭头上沿或下沿引一条竖线到红线迎面那条边，末端一道短横
                const int lx = 50;
                scene.Line(wx + lx, edgeY, wx + lx, ty, OverlayBlack, 5);
                scene.Line(wx + 40, ty, wx + 62, ty, OverlayBlack, 5);
                scene.Line(wx + lx, edgeY, wx + lx, ty, OverlayGreen, 2);
                scene.Line(wx + 40, ty, wx + 62, ty, OverlayGreen, 2);
            }
            // 指向右边竖条的空心箭头（Polyline），先画粗的深色描边再画绿色
            (int x, int y)[] arrow = [(35, arrowTop), (18, arrowTop), (29, cy), (18, arrowBottom), (35, arrowBottom), (46, cy), (35, arrowTop)];
            for (var i = 1; i < arrow.Length; i++)
                scene.Line(wx + arrow[i - 1].x, arrow[i - 1].y, wx + arrow[i].x, arrow[i].y, OverlayBlack, 8);
            for (var i = 1; i < arrow.Length; i++)
                scene.Line(wx + arrow[i - 1].x, arrow[i - 1].y, wx + arrow[i].x, arrow[i].y, OverlayGreen, 5);
        }
        return scene;
    }

    /// <summary>信息框里的一行字，左上角在 (left, top)。按每字约 7.3 像素估算宽度，换成渲染需要的中心点。</summary>
    private static void InfoLine(HackScene scene, int left, int top, string text, uint color) =>
        scene.Text(left + text.Length * 7.3f / 2, top + 8, text, color, 13, outline: false);

    /// <summary>交一帧新内容（原版两个窗口的定时器各自 InvalidateRect）；叠加层关着时不准备内容，正在显示的收起来。</summary>
    private void PublishOverlay(in OverlayState state)
    {
        var overlay = host.Overlay;
        if (!overlay.Enabled) { HideOverlay(); return; }
        var scene = BuildOverlayScene(state);
        if (scene.IsEmpty) { HideOverlay(); return; }
        if (_overlayShown is not null && _overlayShown.SequenceEqual(scene.Shapes)) return;
        overlay.Show(scene);
        _overlayShown = scene.Shapes;
    }

    /// <summary>ClearOverlayState / HideTransientOverlays：收起光标和信息框。</summary>
    private void HideOverlay()
    {
        if (_overlayShown is null) return;
        _overlayShown = null;
        host.Overlay.Clear();
    }

    // ───────── 像素 ─────────

    private static bool IsRed(uint px)
    {
        double r = (px >> 16) & 0xff, g = (px >> 8) & 0xff, b = px & 0xff;
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var chroma = maximum - minimum;
        return maximum >= 40 && chroma >= 20 && chroma / maximum >= .25 && r == maximum && r - Math.Max(g, b) >= Math.Max(10.0, chroma * .35);
    }

    private static bool IsWhite(uint px)
    {
        int r = (int)((px >> 16) & 0xff), g = (int)((px >> 8) & 0xff), b = (int)(px & 0xff);
        return r >= 210 && g >= 210 && b >= 210 && Math.Abs(r - g) <= 38 && Math.Abs(r - b) <= 38;
    }

    private static bool IsYellow(uint px)
    {
        int r = (int)((px >> 16) & 0xff), g = (int)((px >> 8) & 0xff), b = (int)(px & 0xff);
        return r >= 185 && g >= 145 && b <= 95 && r >= b + 90 && g >= b + 55 && Math.Abs(r - g) <= 95;
    }

    private bool CaptureScreenRegion(CaptureFrame frame, RectI? region)
    {
        var captured = host.Capture(region is { } r ? new HackRect(r.X, r.Y, r.X + r.W, r.Y + r.H) : null);
        if (captured is null) return false;
        frame.X = captured.ScreenX;
        frame.Y = captured.ScreenY;
        frame.W = captured.Width;
        frame.H = captured.Height;
        frame.ToScreenX = captured.ToScreenX;
        frame.ToScreenY = captured.ToScreenY;
        frame.WindowGeneration = captured.WindowGeneration;
        var gameHeight = captured.ClientHeight;
        frame.AnalysisGeometryScale = Math.Clamp(Math.Min(gameHeight, 1080) / BaselineScreenHeightPx, 0.45, 2.25);
        frame.ScreenGeometryScale = Math.Clamp(gameHeight / BaselineScreenHeightPx, 0.45, 2.25);
        frame.Bgra = captured.Bgra;
        return true;
    }

    private static uint Pixel(CaptureFrame f, int x, int y) => f.Bgra[y * f.W + x];
    private static int ToScreenX(CaptureFrame f, int x) => f.X + Round(x * f.ToScreenX);
    private static int ToScreenY(CaptureFrame f, int y) => f.Y + Round(y * f.ToScreenY);
    private static int ToFrameX(CaptureFrame f, int x) => Round((x - f.X) / Math.Max(0.0001, f.ToScreenX));
    private static int ToFrameY(CaptureFrame f, int y) => Round((y - f.Y) / Math.Max(0.0001, f.ToScreenY));

    /// <summary>values[begin..end) 里连续不低于 threshold、且至少 minWidth 长的区间。</summary>
    private static List<(int first, int second)> GroupRuns(int[] values, int begin, int end, int threshold, int minWidth)
    {
        var runs = new List<(int, int)>();
        var i = begin;
        while (i < end)
        {
            while (i < end && values[i] < threshold) i++;
            var s = i;
            while (i < end && values[i] >= threshold) i++;
            var e = i - 1;
            if (e >= s && e - s + 1 >= minWidth) runs.Add((s, e));
        }
        return runs;
    }

    // ───────── 红线与竖条 ─────────

    private static RedBar LocateRedBar(CaptureFrame f)
    {
        var red = new RedBar();
        int cx1 = Math.Max(0, f.W / 32), cx2 = Math.Min(f.W, f.W - f.W / 32);
        int cy1 = Math.Max(0, f.H / 12), cy2 = Math.Min(f.H, f.H - f.H / 12);
        var rowCount = new int[f.H];
        for (var y = cy1; y < cy2; y++)
        {
            var count = 0;
            for (var x = cx1; x < cx2; x++) if (IsRed(Pixel(f, x, y))) count++;
            rowCount[y] = count;
        }
        int bestY = cy1, bestCount = 0;
        for (var y = cy1; y < cy2; y++)
            if (rowCount[y] > bestCount) { bestCount = rowCount[y]; bestY = y; }
        var minRowCount = Math.Max(120, bestCount / 2);
        if (bestCount < minRowCount) return red;
        var redRuns = GroupRuns(rowCount, cy1, cy2, minRowCount, 3);
        if (redRuns.Count == 0) return red;
        // 离画面中线最近的一段（std::min_element：并列取靠前的）
        var screenMid = f.H / 2;
        var chosen = redRuns[0];
        foreach (var run in redRuns)
            if (Math.Abs((run.first + run.second) / 2 - screenMid) < Math.Abs((chosen.first + chosen.second) / 2 - screenMid)) chosen = run;
        int y1 = chosen.first, y2 = chosen.second, x1 = f.W, x2 = 0;
        for (var y = y1; y <= y2; y++)
            for (var x = cx1; x < cx2; x++)
                if (IsRed(Pixel(f, x, y))) { x1 = Math.Min(x1, x); x2 = Math.Max(x2, x); }
        if (x2 - x1 < 240 || y2 - y1 < 4) return red;
        // 按列重新量一下红线的上下边，取最长的一段
        var sampleInset = Math.Max(3, Round(f.H * .025));
        var sampleStep = Math.Max(3, Round(f.H * .025));
        var searchMargin = Math.Max(4, Round(f.H * .06));
        int sampleLeft = x1 + sampleInset, sampleRight = x2 - sampleInset;
        int searchTop = Math.Max(cy1, y1 - searchMargin), searchBottom = Math.Min(cy2 - 1, y2 + searchMargin);
        int refinedY1 = y1, refinedY2 = y2, longestSpan = 0;
        for (var x = sampleLeft; x <= sampleRight; x += sampleStep)
        {
            var runStart = -1;
            for (var y = searchTop; y <= searchBottom + 1; y++)
            {
                var redPixel = y <= searchBottom && IsRed(Pixel(f, x, y));
                if (redPixel)
                {
                    if (runStart < 0) runStart = y;
                    continue;
                }
                if (runStart < 0) continue;
                var runEnd = y - 1;
                if (runStart <= y2 && runEnd >= y1)
                {
                    var span = runEnd - runStart + 1;
                    if (span > longestSpan) { longestSpan = span; refinedY1 = runStart; refinedY2 = runEnd; }
                }
                runStart = -1;
            }
        }
        if (longestSpan > 0) { y1 = refinedY1; y2 = refinedY2; }
        red.Ok = true;
        red.X1 = x1; red.X2 = x2; red.Y1 = y1; red.Y2 = y2;
        red.CenterY = (y1 + y2) / 2;
        red.Height = y2 - y1 + 1;
        return red;
    }

    /// <summary>从一段行计数里挑出「上段 + 缺口 + 下段」最像的一对。返回 false 表示没找到。</summary>
    private static bool PickTopBottom(List<(int first, int second)> yRuns, double scale, int? redCenterY, out (int first, int second) top, out (int first, int second) bottom)
    {
        top = bottom = default;
        var bestScore = -1000000;
        for (var i = 0; i + 1 < yRuns.Count; i++)
            for (var j = i + 1; j < yRuns.Count; j++)
            {
                var verticalGap = yRuns[j].first - yRuns[i].second - 1;
                var topHeight = yRuns[i].second - yRuns[i].first + 1;
                var bottomHeight = yRuns[j].second - yRuns[j].first + 1;
                if (verticalGap < ScaledPx(6, scale) || verticalGap > ScaledPx(70, scale)) continue;
                if (topHeight < ScaledPx(28, scale) || bottomHeight < ScaledPx(28, scale)) continue;
                var heightBalancePenalty = Math.Abs(topHeight - bottomHeight);
                var centerPenalty = redCenterY is { } c ? Math.Abs((yRuns[i].second + yRuns[j].first) / 2 - c) / 3 : 0;
                var score = topHeight + bottomHeight - heightBalancePenalty - centerPenalty;
                if (score > bestScore) { bestScore = score; top = yRuns[i]; bottom = yRuns[j]; }
            }
        return bestScore >= 0;
    }

    private static List<BarMeasure> LocateWhiteBars(CaptureFrame f, RedBar red, double scale)
    {
        var bars = new List<BarMeasure>();
        if (!red.Ok) return bars;
        var roiX = Math.Max(0, red.X1 - ScaledPx(25, scale));
        var roiW = Math.Min(f.W - 1, red.X2 + ScaledPx(25, scale)) - roiX + 1;
        var roiY = Math.Max(0, red.CenterY - ScaledPx(360, scale));
        var roiH = Math.Min(f.H - 1, red.CenterY + ScaledPx(360, scale)) - roiY + 1;
        var colCount = new int[f.W];
        for (var x = roiX; x < roiX + roiW; x++)
        {
            var count = 0;
            for (var y = roiY; y < roiY + roiH; y++) if (IsWhite(Pixel(f, x, y))) count++;
            colCount[x] = count;
        }
        var colThreshold = Math.Max(ScaledPx(28, scale), roiH / 13);
        foreach (var (x1, x2) in GroupRuns(colCount, roiX, roiX + roiW, colThreshold, ScaledPx(5, scale)))
        {
            if (x2 - x1 + 1 > ScaledPx(80, scale)) continue;
            var rowCount = new int[f.H];
            var width = x2 - x1 + 1;
            for (var y = roiY; y < roiY + roiH; y++)
            {
                var count = 0;
                for (var x = x1; x <= x2; x++) if (IsWhite(Pixel(f, x, y))) count++;
                rowCount[y] = count;
            }
            var yRuns = GroupRuns(rowCount, roiY, roiY + roiH, Math.Max(ScaledPx(3, scale), width / 3), ScaledPx(12, scale));
            if (yRuns.Count < 2) continue;
            if (!PickTopBottom(yRuns, scale, red.CenterY, out var top, out var bottom)) continue;
            bars.Add(new BarMeasure
            {
                Ok = true, X1 = x1, X2 = x2, TopY1 = top.first, TopY2 = top.second, BottomY1 = bottom.first, BottomY2 = bottom.second,
                GapCenterY = (top.second + bottom.first) / 2.0,
            });
        }
        bars.Sort((a, b) => (a.X1 + a.X2).CompareTo(b.X1 + b.X2));
        if (bars.Count > 8) bars = bars.Take(8).ToList();
        return bars;
    }

    private static bool MeasureBarAtX(CaptureFrame f, int x1, int x2, int y1, int y2, double scale, out BarMeasure result)
    {
        result = default;
        x1 = Math.Clamp(x1, 0, f.W - 1); x2 = Math.Clamp(x2, 0, f.W - 1);
        y1 = Math.Clamp(y1, 0, f.H - 1); y2 = Math.Clamp(y2, 0, f.H - 1);
        if (x2 <= x1 || y2 <= y1) return false;
        var rowCount = new int[f.H];
        var width = x2 - x1 + 1;
        for (var y = y1; y <= y2; y++)
        {
            var count = 0;
            for (var x = x1; x <= x2; x++) if (IsWhite(Pixel(f, x, y))) count++;
            rowCount[y] = count;
        }
        var yRuns = GroupRuns(rowCount, y1, y2 + 1, Math.Max(ScaledPx(3, scale), width / 3), ScaledPx(12, scale));
        if (yRuns.Count < 2) return false;
        if (!PickTopBottom(yRuns, scale, null, out var top, out var bottom)) return false;
        result = new BarMeasure
        {
            Ok = true, X1 = x1, X2 = x2, TopY1 = top.first, TopY2 = top.second, BottomY1 = bottom.first, BottomY2 = bottom.second,
            GapCenterY = (top.second + bottom.first) / 2.0,
        };
        return true;
    }

    private static List<BarMeasure> LocateWhiteBarsAtKnownX(CaptureFrame f, RedBar red, List<(int first, int second)> knownXRuns, double scale)
    {
        var bars = new List<BarMeasure>();
        if (!red.Ok || knownXRuns.Count != 8) return bars;
        var scanY1 = Math.Max(0, red.CenterY - ScaledPx(290, scale));
        var scanY2 = Math.Min(f.H - 1, red.CenterY + ScaledPx(290, scale));
        foreach (var (kx1, kx2) in knownXRuns)
            if (MeasureBarAtX(f, kx1 - ScaledPx(3, scale), kx2 + ScaledPx(3, scale), scanY1, scanY2, scale, out var m)) bars.Add(m);
        return bars;
    }

    /// <summary>由第 2–8 根竖条推出 8 个等距的搜索格（第 1 根常被游戏界面遮挡，不参与）。</summary>
    private static SearchCells BuildSearchCellsFromWhiteBars(List<BarMeasure> bars, double scale)
    {
        var cells = new SearchCells();
        if (bars.Count < 8) return cells;
        var centers = new List<double>();
        var widths = new List<double>();
        for (var i = 1; i < 8; i++)
        {
            centers.Add((bars[i].X1 + bars[i].X2) / 2.0);
            widths.Add(bars[i].X2 - bars[i].X1 + 1);
        }
        widths.Sort();
        var medianWidth = widths[widths.Count / 2];
        var gaps = new List<double>();
        for (var i = 1; i < centers.Count; i++) gaps.Add(centers[i] - centers[i - 1]);
        gaps.Sort();
        var medianGap = gaps.Count == 0 ? medianWidth * 2.0 : gaps[gaps.Count / 2];
        var firstCenter = centers[0] - medianGap;
        // std::clamp 的写法：下限比上限大时也不报错
        double lo = medianWidth, hi = medianGap - ScaledPx(4, scale), v = medianWidth + ScaledPx(16, scale);
        var searchWidth = v < lo ? lo : hi < v ? hi : v;
        for (var i = 0; i < 8; i++)
        {
            var c = firstCenter + medianGap * i;
            cells.XRanges.Add((Round(c - searchWidth / 2.0), Round(c + searchWidth / 2.0)));
        }
        cells.Ok = true;
        return cells;
    }

    // ───────── 黄色缺口 ─────────

    /// <summary>在第 index 个格子的中线附近找黄框的上下两条边。verticalHalf：-1 只看红线以上，1 只看以下，0 都看。</summary>
    private static bool MeasureYellowGapForBar(CaptureFrame f, RedBar red, int index, int cellLeftScreen, int cellRightScreen, double scale, int verticalHalf, out YellowMeasure result)
    {
        result = default;
        var localScale = scale / Math.Max(0.0001, (f.ToScreenX + f.ToScreenY) * 0.5);
        var cellLeft = ToFrameX(f, cellLeftScreen);
        var cellRight = ToFrameX(f, cellRightScreen);
        var centerY = ToFrameY(f, red.CenterY);
        var y1 = Math.Max(0, centerY - ScaledPx(320, localScale));
        var y2 = Math.Min(f.H - 1, centerY + ScaledPx(320, localScale));
        var halfOverlap = ScaledPx(YellowHalfOverlapPx, localScale);
        if (verticalHalf < 0) y2 = Math.Min(y2, centerY + halfOverlap);
        if (verticalHalf > 0) y1 = Math.Max(y1, centerY - halfOverlap);
        if (cellRight < 0 || cellLeft >= f.W || y2 <= y1) return false;
        var rowCount = new int[f.H];
        var cellCenter = (cellLeft + cellRight) / 2;
        var stripHalfWidth = ScaledPx(YellowCenterStripHalfWidthPx, localScale);
        var scanX1 = Math.Clamp(cellCenter - stripHalfWidth, 0, f.W - 1);
        var scanX2 = Math.Clamp(cellCenter + stripHalfWidth, 0, f.W - 1);
        for (var y = y1; y <= y2; y++)
        {
            var count = 0;
            for (var x = scanX1; x <= scanX2; x++) if (IsYellow(Pixel(f, x, y))) count++;
            rowCount[y] = count;
        }
        var runs = GroupRuns(rowCount, y1, y2 + 1, ScaledPx(2, localScale), ScaledPx(1, localScale));
        if (runs.Count < 2) return false;
        (int first, int second) topRun = default, bottomRun = default;
        var bestScore = -1000000;
        var foundPair = false;
        for (var i = 0; i + 1 < runs.Count; i++)
            for (var j = i + 1; j < runs.Count; j++)
            {
                var verticalGap = runs[j].first - runs[i].second - 1;
                var topHeight = runs[i].second - runs[i].first + 1;
                var bottomHeight = runs[j].second - runs[j].first + 1;
                if (verticalGap < ScaledPx(8, localScale) || verticalGap > ScaledPx(95, localScale)) continue;
                var yellowPixels = 0;
                for (var y = runs[i].first; y <= runs[i].second; y++) yellowPixels += rowCount[y];
                for (var y = runs[j].first; y <= runs[j].second; y++) yellowPixels += rowCount[y];
                var balancePenalty = Math.Abs(topHeight - bottomHeight) * 2;
                var gapPenalty = Math.Abs(verticalGap - ScaledPx(24, localScale));
                var score = yellowPixels + topHeight + bottomHeight - balancePenalty - gapPenalty;
                if (score > bestScore) { bestScore = score; topRun = runs[i]; bottomRun = runs[j]; foundPair = true; }
            }
        if (!foundPair) return false;
        var total = 0;
        for (var y = topRun.first; y <= topRun.second; y++) total += rowCount[y];
        for (var y = bottomRun.first; y <= bottomRun.second; y++) total += rowCount[y];
        result = new YellowMeasure
        {
            Ok = true, Index = index, TopBottomY = ToScreenY(f, topRun.second), BottomTopY = ToScreenY(f, bottomRun.first), Score = total,
        };
        result.GapCenterY = (result.TopBottomY + result.BottomTopY) / 2.0;
        return true;
    }

    private static YellowMeasure FindActiveYellowMeasure(CaptureFrame f, RedBar red, SearchCells cells, double scale, int preferredVerticalHalf, bool[]? skip = null)
    {
        var best = new YellowMeasure();
        if (!red.Ok || !cells.Ok || cells.XRanges.Count != 8) return best;
        var localScale = scale / Math.Max(0.0001, (f.ToScreenX + f.ToScreenY) * 0.5);
        var minimumScore = ScaledPx(20, localScale);
        var passCount = preferredVerticalHalf == 0 ? 1 : 2;
        for (var pass = 0; pass < passCount && !best.Ok; pass++)
        {
            var verticalHalf = pass == 0 ? preferredVerticalHalf : -preferredVerticalHalf;
            for (var i = 0; i < 8; i++)
            {
                if (skip is not null && skip[i]) continue;
                if (MeasureYellowGapForBar(f, red, i, cells.XRanges[i].first, cells.XRanges[i].second, scale, verticalHalf, out var m) &&
                    m.Score >= minimumScore && (!best.Ok || m.Score > best.Score)) best = m;
            }
        }
        return best;
    }

    private static YellowMeasure FindActiveYellowMeasureCandidates(CaptureFrame f, RedBar red, SearchCells cells, int[] candidates, double scale, int preferredVerticalHalf)
    {
        var scanned = new bool[8];
        foreach (var raw in candidates) scanned[Math.Clamp(raw, 0, 7)] = true;
        var skip = scanned.Select(s => !s).ToArray();
        return FindActiveYellowMeasure(f, red, cells, scale, preferredVerticalHalf, skip);
    }

    private static int[] YellowCandidateIndices(int lastYellowIndex, int expectedIndex) =>
        lastYellowIndex >= 0 ? [lastYellowIndex, lastYellowIndex - 1, lastYellowIndex + 1] : [expectedIndex, expectedIndex + 1, expectedIndex - 1];

    /// <summary>只截候选的几个格子：缺口在红线上方就只截上半，反之下半（多留一点重叠）。</summary>
    private static RectI BuildYellowCaptureRegion(RedBar red, SearchCells cells, int[] candidates, double scale, int preferredVerticalHalf)
    {
        var left = cells.XRanges[Math.Clamp(candidates[0], 0, 7)].first;
        var right = cells.XRanges[Math.Clamp(candidates[0], 0, 7)].second;
        foreach (var raw in candidates)
        {
            var index = Math.Clamp(raw, 0, 7);
            left = Math.Min(left, cells.XRanges[index].first);
            right = Math.Max(right, cells.XRanges[index].second);
        }
        var horizontalMargin = ScaledPx(6, scale);
        var fullExtent = ScaledPx(320, scale);
        var halfOverlap = ScaledPx(YellowHalfOverlapPx, scale);
        int top = red.CenterY - fullExtent, bottom = red.CenterY + fullExtent;
        if (preferredVerticalHalf < 0) bottom = red.CenterY + halfOverlap;
        if (preferredVerticalHalf > 0) top = red.CenterY - halfOverlap;
        return new RectI(left - horizontalMargin, top, right - left + 1 + horizontalMargin * 2, bottom - top + 1);
    }

    private static RectI BuildFullYellowCaptureRegion(RedBar red, SearchCells cells, double scale)
    {
        var horizontalMargin = ScaledPx(6, scale);
        var verticalExtent = ScaledPx(320, scale);
        var left = cells.XRanges[0].first - horizontalMargin;
        var right = cells.XRanges[^1].second + horizontalMargin;
        int top = red.CenterY - verticalExtent, bottom = red.CenterY + verticalExtent;
        return new RectI(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>整帧分析：找红线和 8 根竖条，结果换算成屏幕坐标。</summary>
    private static FrameAnalysis AnalyzeFrame(CaptureFrame f, List<(int first, int second)>? knownXRuns = null)
    {
        var analysis = new FrameAnalysis { Red = LocateRedBar(f) };
        if (!analysis.Red.Ok) return analysis;
        var scale = f.AnalysisGeometryScale;
        if (knownXRuns is { Count: 8 }) analysis.Bars = LocateWhiteBarsAtKnownX(f, analysis.Red, knownXRuns, scale);
        if (analysis.Bars.Count < 8) analysis.Bars = LocateWhiteBars(f, analysis.Red, scale);
        analysis.Ok = analysis.Bars.Count >= 8;
        analysis.InMinigame = analysis.Ok;
        var red = analysis.Red;
        red.X1 = ToScreenX(f, red.X1); red.X2 = ToScreenX(f, red.X2);
        red.Y1 = ToScreenY(f, red.Y1); red.Y2 = ToScreenY(f, red.Y2);
        red.CenterY = ToScreenY(f, red.CenterY);
        analysis.Red = red;
        for (var i = 0; i < analysis.Bars.Count; i++)
        {
            var bar = analysis.Bars[i];
            bar.X1 = ToScreenX(f, bar.X1); bar.X2 = ToScreenX(f, bar.X2);
            bar.TopY1 = ToScreenY(f, bar.TopY1); bar.TopY2 = ToScreenY(f, bar.TopY2);
            bar.BottomY1 = ToScreenY(f, bar.BottomY1); bar.BottomY2 = ToScreenY(f, bar.BottomY2);
            bar.GapCenterY = f.Y + bar.GapCenterY * f.ToScreenY;
            analysis.Bars[i] = bar;
        }
        return analysis;
    }

    // ───────── 追踪与预测 ─────────

    private static void ResetTrack(TrackSlot slot)
    {
        slot.Valid = false;
        slot.LastCenter = 0.0;
        slot.Velocity = 0.0;
        slot.History.Clear();
    }

    /// <summary>最近 4 个样本做加权线性回归求速度，越新的样本权重越大。</summary>
    private static double EstimateVelocity(TrackSlot slot)
    {
        var history = slot.History;
        if (history.Count < 2) return 0.0;
        var begin = history.Count > 4 ? history.Count - 4 : 0;
        var tLast = history[^1].Time;
        double sumW = 0, sumT = 0, sumE = 0;
        for (var i = begin; i < history.Count; i++)
        {
            var t = (history[i].Time - tLast) / (double)Stopwatch.Frequency;
            var recency = Math.Clamp(1.0 + t / 0.10, 0.0, 1.0);
            var w = 0.50 + recency * recency * 2.50;
            sumW += w; sumT += w * t; sumE += w * history[i].Center;
        }
        if (sumW <= 0.0) return 0.0;
        double meanT = sumT / sumW, meanE = sumE / sumW, num = 0, den = 0;
        for (var i = begin; i < history.Count; i++)
        {
            var t = (history[i].Time - tLast) / (double)Stopwatch.Frequency;
            var recency = Math.Clamp(1.0 + t / 0.10, 0.0, 1.0);
            var w = 0.50 + recency * recency * 2.50;
            num += w * (t - meanT) * (history[i].Center - meanE);
            den += w * (t - meanT) * (t - meanT);
        }
        return den > 1e-6 ? num / den : 0.0;
    }

    private static void AddTrackSample(TrackSlot slot, double observedError, int topBottomY, int bottomTopY, long sampleTime)
    {
        slot.LastCenter = observedError;
        if (slot.History.Count > 0)
        {
            // 画面没刷新（同一帧被截了两次）就不算新样本
            var previous = slot.History[^1];
            var sameMarker = topBottomY == previous.TopBottomY && bottomTopY == previous.BottomTopY;
            var sameQuantizedCenter = Math.Abs(observedError - previous.Center) < 0.25;
            if (sameMarker || sameQuantizedCenter) return;
        }
        slot.History.Add(new TrackSample(observedError, sampleTime, topBottomY, bottomTopY));
        while (slot.History.Count > 32) slot.History.RemoveAt(0);
        slot.Velocity = EstimateVelocity(slot);
    }

    private static bool IsUsableTiming(double seconds) => double.IsFinite(seconds) && seconds > 0.0 && seconds < 1.0;

    private static double EstimateEdgeTriggerTime(double edgeError, double velocity, double scale, double lookaheadSeconds)
    {
        var speed = Math.Abs(velocity);
        var forecastZone = ScaledPx(EdgeTriggerZonePx, scale) + speed * Math.Max(0.0, lookaheadSeconds);
        if (Math.Abs(edgeError) > forecastZone || speed < MinUsableVelocityPxPerSec || edgeError * velocity >= 0.0) return InvalidPredictionSeconds;
        return Math.Abs(edgeError) / speed;
    }

    /// <summary>缺口中心到红线「迎面那条边」的距离：往下走看红线上边，往上走看下边。</summary>
    private static double EdgeTriggerError(YellowMeasure yellow, RedBar red, double velocity) =>
        velocity > 0.0 ? yellow.GapCenterY - red.Y1 : velocity < 0.0 ? yellow.GapCenterY - red.Y2 : yellow.GapCenterY - red.CenterY;

    // ───────── 会话 ─────────

    /// <summary>精确等到某个时刻：先粗睡，最后 2 毫秒让出时间片。</summary>
    private static void SleepUntil(long deadline)
    {
        while (true)
        {
            var remaining = (deadline - Now) * 1000.0 / Stopwatch.Frequency;
            if (remaining <= 0) return;
            if (remaining > 3) Thread.Sleep((int)(remaining - 2));
            else Thread.Yield();
        }
    }

    private bool WorkerLoop(Func<bool> stopRequested, Action<string> status)
    {
        var tracks = Enumerable.Range(0, 8).Select(_ => new TrackSlot()).ToArray();
        var lastEnter = Now - Ms(2000);
        var missFrames = 0;
        var lastFrameTime = Now;
        int expectedIndex = 0, lastYellowIndex = -1, trackedActive = -1, yellowMissFrames = 0, preferredYellowHalf = 0;
        var hasTrackingRegion = false;
        var lockedRedScreen = new RedBar();
        var knownBarXRunsScreen = new List<(int first, int second)>();
        var lastBarsScreen = new List<BarMeasure>();
        var searchCellsScreen = new SearchCells();
        var cellsLocked = false;
        var geometryScale = 1.0;
        ulong cachedWindowGeneration = 0;
        if (_cache.Valid)
        {
            lockedRedScreen = _cache.Red;
            lastBarsScreen = _cache.Bars;
            searchCellsScreen = _cache.Cells;
            cellsLocked = searchCellsScreen.Ok;
            hasTrackingRegion = lockedRedScreen.Ok && lastBarsScreen.Count >= 8 && cellsLocked;
            cachedWindowGeneration = _cache.WindowGeneration;
            geometryScale = _cache.GeometryScale;
        }
        var activeSearchStart = Now;
        var activeFoundOnce = false;
        var finishPendingAfter8 = false;
        var finishConfirmStart = Now;
        var postPress = new PostPressCalibration();
        var calibratedLatencySamples = new List<double> { DefaultEndToEndLatencySeconds };
        var calibratedLatencySeconds = DefaultEndToEndLatencySeconds;
        var analysisInterval = Ms(AnalysisIntervalSeconds * 1000);
        var nextAnalysisTime = Now;
        var analysisIntervalSamples = new double[30];
        int analysisIntervalSampleCount = 0, analysisIntervalSampleIndex = 0;
        double analysisIntervalSampleSum = 0, actualAnalysisIntervalSeconds = AnalysisIntervalSeconds;
        var frame = new CaptureFrame();
        var overlay = new OverlayState { TtcMs = -1, PreviewScale = 1.0, EndToEndMs = Round(calibratedLatencySeconds * 1000.0) };
        var lastUiUpdate = Now - Ms(1000);
        // UpdatePreview：信息框只在有竖条时显示，位置跟着红线
        void UpdatePreview(FrameAnalysis analysis, double scale)
        {
            overlay.PreviewHasBars = analysis.Bars.Count > 0;
            overlay.PreviewRed = analysis.Red;
            overlay.PreviewScale = scale;
        }

        void ForgetGeometry()
        {
            hasTrackingRegion = false;
            lockedRedScreen = new RedBar();
            knownBarXRunsScreen.Clear();
            searchCellsScreen = new SearchCells();
            cellsLocked = false;
            lastBarsScreen = [];
            cachedWindowGeneration = 0;
        }

        while (!stopRequested())
        {
            // 约 60 次 / 秒
            var frameTime = Now;
            if (frameTime < nextAnalysisTime)
            {
                SleepUntil(nextAnalysisTime);
                frameTime = Now;
            }
            nextAnalysisTime += analysisInterval;
            if (nextAnalysisTime <= frameTime) nextAnalysisTime = frameTime + analysisInterval;
            var frameIntervalSeconds = (frameTime - lastFrameTime) / (double)Stopwatch.Frequency;
            lastFrameTime = frameTime;
            if (frameIntervalSeconds is >= 0.001 and < 1.0)
            {
                if (analysisIntervalSampleCount < analysisIntervalSamples.Length) analysisIntervalSampleCount++;
                else analysisIntervalSampleSum -= analysisIntervalSamples[analysisIntervalSampleIndex];
                analysisIntervalSamples[analysisIntervalSampleIndex] = frameIntervalSeconds;
                analysisIntervalSampleSum += frameIntervalSeconds;
                analysisIntervalSampleIndex = (analysisIntervalSampleIndex + 1) % analysisIntervalSamples.Length;
                actualAnalysisIntervalSeconds = analysisIntervalSampleSum / analysisIntervalSampleCount;
                overlay.AnalysisHz10 = Round(10.0 / actualAnalysisIntervalSeconds);
            }

            var yellowCandidates = postPress.Active
                ? new[] { postPress.BarIndex, Math.Min(7, postPress.BarIndex + 1), Math.Max(0, postPress.BarIndex - 1) }
                : YellowCandidateIndices(lastYellowIndex, expectedIndex);
            var yellowSearchHalf = postPress.Active ? 0 : preferredYellowHalf;
            var focusedCapture = hasTrackingRegion && lockedRedScreen.Ok && searchCellsScreen.Ok && searchCellsScreen.XRanges.Count == 8;
            var allYellowCellsCaptured = !focusedCapture;
            RectI? trackingRegion = focusedCapture
                ? BuildYellowCaptureRegion(lockedRedScreen, searchCellsScreen, yellowCandidates, geometryScale, yellowSearchHalf)
                : null;
            if (!CaptureScreenRegion(frame, trackingRegion))
            {
                _cache = new InGameCache();
                if (focusedCapture)
                {
                    ForgetGeometry();
                    if (CaptureScreenRegion(frame, null)) allYellowCellsCaptured = true;
                    else
                    {
                        status("slider: capture failed");
                        Thread.Sleep(100);
                        continue;
                    }
                }
                else
                {
                    status("slider: capture failed");
                    Thread.Sleep(100);
                    continue;
                }
            }
            if (cachedWindowGeneration != 0 && frame.WindowGeneration != cachedWindowGeneration)
            {
                _cache = new InGameCache();
                ForgetGeometry();
                if (focusedCapture && !CaptureScreenRegion(frame, null))
                {
                    status("slider: capture failed");
                    Thread.Sleep(100);
                    continue;
                }
                allYellowCellsCaptured = true;
            }
            var sampleTime = Now;
            var canUseLockedGeometry = hasTrackingRegion && lockedRedScreen.Ok && lastBarsScreen.Count >= 8 && searchCellsScreen.Ok;
            FrameAnalysis a;
            if (canUseLockedGeometry)
                a = new FrameAnalysis { Ok = true, InMinigame = true, Red = lockedRedScreen, Bars = lastBarsScreen };
            else
            {
                if (focusedCapture && !CaptureScreenRegion(frame, null))
                {
                    status("slider: capture failed");
                    Thread.Sleep(100);
                    continue;
                }
                allYellowCellsCaptured = true;
                var knownLocal = knownBarXRunsScreen.Count == 8
                    ? knownBarXRunsScreen.Select(r => (ToFrameX(frame, r.first), ToFrameX(frame, r.second))).ToList()
                    : null;
                a = AnalyzeFrame(frame, knownLocal);
            }

            if (!a.InMinigame)
            {
                missFrames++;
                if (finishPendingAfter8 && missFrames >= 3 && postPress.InputJob is { Succeeded: true })
                {
                    status("slider: completed");
                    return true;
                }
                var finalInputPending = finishPendingAfter8 && postPress.InputJob is { Pending: true };
                if (activeFoundOnce && missFrames >= 10 && !finalInputPending)
                {
                    status("slider: minigame exited");
                    return false;
                }
                if (missFrames >= 3)
                {
                    ForgetGeometry();
                    geometryScale = 1.0;
                }
                status("slider: searching minigame");
                UpdatePreview(a, 1.0);
                PublishOverlay(overlay);
                Thread.Sleep(40);
                continue;
            }
            missFrames = 0;
            cachedWindowGeneration = frame.WindowGeneration;
            lockedRedScreen = a.Red;
            lastBarsScreen = a.Bars;
            geometryScale = frame.ScreenGeometryScale;
            if (a.Bars.Count >= 8)
            {
                knownBarXRunsScreen = a.Bars.Take(8).Select(b => (b.X1, b.X2)).ToList();
                if (!cellsLocked || !searchCellsScreen.Ok)
                {
                    searchCellsScreen = BuildSearchCellsFromWhiteBars(a.Bars, geometryScale);
                    cellsLocked = searchCellsScreen.Ok;
                }
            }
            hasTrackingRegion = true;

            // 找当前带黄框的那一根：先看候选的几根，不行再扩大
            var yellow = FindActiveYellowMeasureCandidates(frame, a.Red, searchCellsScreen, yellowCandidates, geometryScale, yellowSearchHalf);
            if (!yellow.Ok && focusedCapture)
            {
                var fallbackRegion = BuildFullYellowCaptureRegion(a.Red, searchCellsScreen, geometryScale);
                if (CaptureScreenRegion(frame, fallbackRegion))
                {
                    allYellowCellsCaptured = true;
                    sampleTime = Now;
                    yellow = FindActiveYellowMeasureCandidates(frame, a.Red, searchCellsScreen, yellowCandidates, geometryScale, yellowSearchHalf);
                }
            }
            if (postPress.Active && postPress.BarIndex < 7)
            {
                var nextIndex = postPress.BarIndex + 1;
                var nextYellow = FindActiveYellowMeasureCandidates(frame, a.Red, searchCellsScreen, [nextIndex, nextIndex, nextIndex], geometryScale, 0);
                if (nextYellow.Ok) yellow = nextYellow;
            }
            if (!yellow.Ok && allYellowCellsCaptured)
                yellow = FindActiveYellowMeasure(frame, a.Red, searchCellsScreen, geometryScale, preferredYellowHalf);
            if (!yellow.Ok && allYellowCellsCaptured)
            {
                // 哪一根都没有黄框：截整帧确认小游戏还在不在
                if (!CaptureScreenRegion(frame, null))
                {
                    _cache = new InGameCache();
                    status("slider: capture failed");
                    Thread.Sleep(100);
                    continue;
                }
                sampleTime = Now;
                var presence = AnalyzeFrame(frame);
                if (!presence.InMinigame)
                {
                    _cache = new InGameCache();
                    var done = finishPendingAfter8 && postPress.InputJob is { Succeeded: true };
                    status(done ? "slider: completed" : "slider: minigame exited");
                    return done;
                }
                a = presence;
                cachedWindowGeneration = frame.WindowGeneration;
                lockedRedScreen = a.Red;
                lastBarsScreen = a.Bars;
                geometryScale = frame.ScreenGeometryScale;
                knownBarXRunsScreen = a.Bars.Take(8).Select(b => (b.X1, b.X2)).ToList();
                searchCellsScreen = BuildSearchCellsFromWhiteBars(a.Bars, geometryScale);
                cellsLocked = searchCellsScreen.Ok;
                hasTrackingRegion = a.Bars.Count >= 8 && cellsLocked;
                _cache = new InGameCache
                {
                    Valid = hasTrackingRegion, WindowGeneration = frame.WindowGeneration, Red = a.Red, Bars = a.Bars,
                    Cells = searchCellsScreen, GeometryScale = geometryScale,
                };
                yellow = FindActiveYellowMeasure(frame, a.Red, searchCellsScreen, geometryScale, preferredYellowHalf);
            }

            var active = yellow.Ok ? yellow.Index : -1;
            if (active >= 0 && active != trackedActive)
            {
                ResetTrack(tracks[active]);
                trackedActive = active;
            }
            if (yellow.Ok)
            {
                yellowMissFrames = 0;
                lastYellowIndex = active;
                if (yellow.GapCenterY < a.Red.CenterY) preferredYellowHalf = -1;
                else if (yellow.GapCenterY > a.Red.CenterY) preferredYellowHalf = 1;
                tracks[active].Valid = true;
                AddTrackSample(tracks[active], yellow.GapCenterY - a.Red.CenterY, yellow.TopBottomY, yellow.BottomTopY, sampleTime);
                expectedIndex = active;
                // 叠加层光标：放在当前竖条左边，高度跟着缺口中心，先指向红线中线
                var minBarX = searchCellsScreen.Ok && searchCellsScreen.XRanges.Count == 8 ? searchCellsScreen.XRanges[0].first
                    : knownBarXRunsScreen.Count == 8 ? knownBarXRunsScreen[0].first : a.Red.X1;
                var cursorBaseX = yellow.Index >= 0 && yellow.Index < a.Bars.Count
                    ? a.Bars[yellow.Index].X1 - ScaledPx(16, geometryScale) - CursorArrowRightOffset
                    : minBarX - ScaledPx(16, geometryScale) - CursorArrowRightOffset;
                if (searchCellsScreen.Ok && searchCellsScreen.XRanges.Count == 8)
                    cursorBaseX = searchCellsScreen.XRanges[active].first - ScaledPx(16, geometryScale) - CursorArrowRightOffset;
                overlay.CursorX = cursorBaseX;
                overlay.CursorY = Round(yellow.GapCenterY);
                overlay.CursorTargetY = a.Red.CenterY;
                overlay.CursorInZone = false;
                overlay.CursorVisible = true;
            }

            if (postPress.Active)
            {
                // 按下回车后，下一根出现黄框的时刻减去按键时刻，就是一次完整的输入延迟
                var inputStartedAt = postPress.InputJob?.StartedTicks ?? 0;
                if (postPress.BarIndex < 7 && active == postPress.BarIndex + 1 && inputStartedAt != 0 && sampleTime >= inputStartedAt)
                {
                    var measured = (sampleTime - inputStartedAt) / (double)Stopwatch.Frequency;
                    var completedBar = postPress.BarIndex;
                    if (measured is > 0.0 and <= MaximumCalibratedLatencySeconds)
                    {
                        if (calibratedLatencySamples.Count == 2) calibratedLatencySamples.RemoveAt(0);
                        calibratedLatencySamples.Add(measured);
                        calibratedLatencySeconds = calibratedLatencySamples.Average();
                        overlay.EndToEndMs = Round(calibratedLatencySeconds * 1000.0);
                    }
                    postPress = new PostPressCalibration();
                    ResetTrack(tracks[completedBar]);
                    trackedActive = -1;
                    expectedIndex = completedBar + 1;
                    lastYellowIndex = -1;
                    preferredYellowHalf = 0;
                    continue;
                }
                var timeoutStart = inputStartedAt != 0 ? inputStartedAt : postPress.ScheduledAt;
                if (Now - timeoutStart > Ms(650))
                {
                    var timedOutBar = postPress.BarIndex;
                    postPress = new PostPressCalibration();
                    ResetTrack(tracks[timedOutBar]);
                    trackedActive = -1;
                    expectedIndex = timedOutBar;
                    lastYellowIndex = -1;
                    preferredYellowHalf = 0;
                    continue;
                }
            }

            if (active == -1)
            {
                if (!activeFoundOnce && Now - activeSearchStart > Ms(10000))
                {
                    status("slider: active bar timeout");
                    return false;
                }
                if (finishPendingAfter8 && Now - finishConfirmStart > Ms(650))
                {
                    status("slider: completed");
                    return true;
                }
                overlay.CursorVisible = false;
                overlay.CursorInZone = false;
                if (++yellowMissFrames >= 3)
                {
                    lastYellowIndex = -1;
                    trackedActive = -1;
                }
                status("slider: waiting yellow outline");
                UpdatePreview(a, geometryScale);
                PublishOverlay(overlay);
                Thread.Sleep(30);
                continue;
            }
            activeFoundOnce = true;
            if (finishPendingAfter8)
            {
                if (active == 6) finishPendingAfter8 = false; // 第 8 根没按中，退回了第 7 根
                else if (Now - finishConfirmStart > Ms(900))
                {
                    status("slider: completed");
                    return true;
                }
            }
            status("slider: tracking bar " + (active + 1));

            var velocity = tracks[active].Velocity;
            var edgeError = EdgeTriggerError(yellow, a.Red, velocity);
            var triggerTimeSec = EstimateEdgeTriggerTime(edgeError, velocity, geometryScale, actualAnalysisIntervalSeconds);
            var now = Now;
            var cooledDown = now - lastEnter > Ms(170);
            var modelReady = tracks[active].History.Count >= 2;
            var triggerReady = modelReady && IsUsableTiming(triggerTimeSec);
            // 叠加层：引线指向缺口迎面的那条红线边；预计下一帧进入（或越过）触发区时算「快到了」
            var forecastEdgeError = edgeError + velocity * actualAnalysisIntervalSeconds;
            overlay.CursorTargetY = velocity >= 0.0 ? a.Red.Y1 : a.Red.Y2;
            overlay.CursorInZone = Math.Abs(forecastEdgeError) <= ScaledPx(EdgeTriggerZonePx * 2.0, geometryScale) || edgeError * forecastEdgeError <= 0.0;
            if (now - lastUiUpdate >= Ms(50))
            {
                overlay.TtcMs = IsUsableTiming(triggerTimeSec) ? Round(triggerTimeSec * 1000.0) : -1;
                UpdatePreview(a, geometryScale);
                lastUiUpdate = now;
            }
            if (triggerReady && cooledDown)
            {
                var predictedAt = sampleTime + (long)((triggerTimeSec - calibratedLatencySeconds) * Stopwatch.Frequency);
                postPress = new PostPressCalibration
                {
                    Active = true, BarIndex = active, ScheduledAt = predictedAt,
                    InputJob = host.QueueImmediate(0x0D, predictedAt, 0),
                };
                lastEnter = predictedAt;
                if (active == 7)
                {
                    finishPendingAfter8 = true;
                    finishConfirmStart = predictedAt;
                }
            }
            PublishOverlay(overlay); // 放在排队按键之后：画图不耽误按键
        }
        return false;
    }

    public bool DetectInGame()
    {
        var frame = new CaptureFrame();
        if (!CaptureScreenRegion(frame, null))
        {
            _cache = new InGameCache();
            return false;
        }
        var analysis = AnalyzeFrame(frame);
        if (!analysis.InMinigame)
        {
            _cache = new InGameCache();
            return false;
        }
        _cache = new InGameCache
        {
            Valid = true, WindowGeneration = frame.WindowGeneration, Red = analysis.Red, Bars = analysis.Bars,
            Cells = BuildSearchCellsFromWhiteBars(analysis.Bars, frame.ScreenGeometryScale), GeometryScale = frame.ScreenGeometryScale,
        };
        return true;
    }

    public void ResetInGameCache() => _cache = new InGameCache();

    /// <summary>原版不论结果都当作完成；移植版只在确认第 8 根按中后才返回 true。</summary>
    public bool RunSession(Func<bool> stopRequested, Action<string> status)
    {
        var lastStatus = "";
        void SetStatus(string text)
        {
            if (text == lastStatus) return;
            lastStatus = text;
            status(text);
        }
        _overlayShown = null; // 上一次会话结束后宿主已经清过叠加层
        try { return WorkerLoop(stopRequested, SetStatus); }
        finally
        {
            HideOverlay(); // RunSession 结束时的 ClearOverlayState
            ResetInGameCache();
        }
    }
}
