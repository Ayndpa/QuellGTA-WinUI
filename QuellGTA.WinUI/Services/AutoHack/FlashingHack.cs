using System.Diagnostics;
using static QuellGTA.Services.AutoHack.HackUtil;

namespace QuellGTA.Services.AutoHack;

/// <summary>
/// 「信号中继」骇入（games/flashing_module.cpp）：5 行 × 6 列的圆圈，答案是每列一个圆圈连成的路径，
/// 会用蓝色反复闪烁几次。看到同一个闪烁图案连续出现 3 次就锁定答案，然后从左到右：
/// 把光标（白色外圈）上下移到这一列的目标行，回车确认，等光标跳到下一列再继续。
/// 右下角有个四格的关卡指示框，焦点框移到下一格表示进入下一关，重新开始看闪烁。
/// 原版在画面上叠加显示识别结果的那部分没有移植。
/// </summary>
public sealed class FlashingHack(IHackHost host) : IHackGame
{
    public string Name => "信号中继";

    private const int GridRows = 5, GridCols = 6, GridCells = GridRows * GridCols;
    private const int TimerMs = 16;
    private const int SelectedStableFramesBeforeInput = 2;
    private const int LevelFocusHideAfterMissFrames = 5;
    private const int ThresholdPct = 8;

    private readonly record struct WhiteBar(int X, int Y, int W, int H, int Score);
    private readonly record struct Circle(int X, int Y, int R, int Score);
    private readonly record struct UiRect(int Left, int Top, int Right, int Bottom);
    private readonly record struct HLine(int X1, int Y, int X2);

    private sealed class Geometry
    {
        public ulong WindowGeneration;
        public int FrameW, FrameH;
        public WhiteBar Signal, RightTitle, TopTitle;
    }

    private sealed class PatternTracker
    {
        public int[] LastFlash = new int[GridCells], Target = new int[GridCells];
        public bool HasLastFlash, TargetReady;
        public int RepeatCount, FlashCount;
    }

    // ───────── 状态（原版的全局 AppState） ─────────

    private int _roiX = 430, _roiY = 285, _roiW = 680, _roiH = 540;
    private readonly Circle[] _circles = new Circle[GridCells];
    private bool _circlesReady;
    private int _blankFrames;
    private int _selectedIndex = -1, _lastSelectedIndex = -1, _selectedStableFrames;
    private int _levelSlot, _observedLevelSlot, _observedLevelStableFrames;
    private int _levelFocusMissFrames;
    private long _nextInputAt;
    private HackJob? _inputJob;
    private bool _autoInputEnabled;
    private int _pendingVerifyCol = -1;
    private long _finalSubmitAt;
    private Geometry? _geometry;
    private int[] _flashUnion = new int[GridCells];
    private PatternTracker _tracker = new();

    private static long Tick => Environment.TickCount64;

    // ───────── 像素 ─────────

    private static bool At(HackFrame f, int x, int y, out int r, out int g, out int b)
    {
        if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) { r = g = b = 0; return false; }
        var p = f.Bgra[y * f.Width + x];
        r = (int)((p >> 16) & 255); g = (int)((p >> 8) & 255); b = (int)(p & 255);
        return true;
    }

    private static bool IsWhiteUiPixel(int r, int g, int b) =>
        r > 175 && g > 175 && b > 175 && Math.Abs(r - g) < 55 && Math.Abs(r - b) < 55;

    private static bool IsLevelLinePixel(int r, int g, int b)
    {
        var gray = (r * 30 + g * 59 + b * 11) / 100;
        var cyan = g > 120 && b > 120 && r < 110 && g - r > 25 && b - r > 25;
        return gray >= 140 && !cyan;
    }

    private static bool IsBluePixel(int r, int g, int b)
    {
        var cyan = g > 95 && b > 105 && r < 115 && g - r > 28 && b - r > 28;
        var brightCyan = g > 145 && b > 145 && r < 150 && g + b > r * 2 + 80;
        return cyan || brightCyan;
    }

    private static int ScalePx(int px, int screenH) => Math.Max(1, Round(px * (screenH / 1080.0)));

    private static int ToScreenX(HackFrame shot, int x) => shot.ScreenX + Round(x * shot.ToScreenX);
    private static int ToScreenY(HackFrame shot, int y) => shot.ScreenY + Round(y * shot.ToScreenY);
    private static int ToShotY(HackFrame shot, int y) => Round((y - shot.ScreenY) / Math.Max(0.0001, shot.ToScreenY));

    // ───────── 圆圈 ─────────

    private static int CircleEdgePixelScore(HackFrame shot, int x, int y)
    {
        if (!At(shot, x, y, out var r, out var g, out var b)) return 0;
        var lum = (r + g + b) / 3;
        var sat = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        if (r > 125 && g > 125 && b > 125 && sat < 80) return 3;
        if (r > 70 && r > g + 15 && r > b + 15) return 2;
        if (lum > 72 && sat < 70) return 1;
        return 0;
    }

    private static readonly (double x, double y)[] Unit =
        Enumerable.Range(0, 128).Select(i => (Math.Cos(2.0 * Math.PI * i / 128), Math.Sin(2.0 * Math.PI * i / 128))).ToArray();

    /// <summary>圆周上 128 个采样点的「像边线」程度，四个象限都要有，排除半圆和直线。</summary>
    private static int CircleEdgeScore(HackFrame shot, int cx, int cy, int radius)
    {
        var hits = 0;
        Span<int> quadrants = stackalloc int[4];
        for (var i = 0; i < 128; i++)
        {
            var best = 0;
            for (var rr = radius - 1; rr <= radius + 1; rr++)
                best = Math.Max(best, CircleEdgePixelScore(shot, cx + Round(rr * Unit[i].x), cy + Round(rr * Unit[i].y)));
            if (best > 0)
            {
                hits += best;
                quadrants[i * 4 / 128]++;
            }
        }
        var minQuadrant = Math.Min(Math.Min(quadrants[0], quadrants[1]), Math.Min(quadrants[2], quadrants[3]));
        return hits + minQuadrant * 8;
    }

    private static Circle RefineCircleByEdge(HackFrame shot, int seedX, int seedY, int seedR, int maxDx, int maxDy, int minR, int maxR)
    {
        var best = new Circle(seedX, seedY, seedR, -1);
        minR = Math.Max(ScalePx(26, shot.Height), minR);
        maxR = Math.Min(ScalePx(70, shot.Height), maxR);
        if (minR > maxR) (minR, maxR) = (maxR, minR);
        for (var y = seedY - maxDy; y <= seedY + maxDy; y += 2)
            for (var x = seedX - maxDx; x <= seedX + maxDx; x += 2)
                for (var r = minR; r <= maxR; r += 2)
                {
                    var score = CircleEdgeScore(shot, x, y, r);
                    if (score > best.Score) best = new Circle(x, y, r, score);
                }
        return best;
    }

    private static bool AcceptCircleTriplet(Circle topLeft, Circle rightNeighbor, Circle down, int seedR, double cellW, double cellH, int minScore)
    {
        if (topLeft.Score < minScore || rightNeighbor.Score < minScore || down.Score < minScore) return false;
        int dx = rightNeighbor.X - topLeft.X, dy = down.Y - topLeft.Y;
        if (dx < seedR || dy < seedR) return false;
        if (dx < cellW * 0.72 || dx > cellW * 1.28) return false;
        if (dy < cellH * 0.72 || dy > cellH * 1.28) return false;
        if (Math.Abs(rightNeighbor.Y - topLeft.Y) > cellH * 0.18) return false;
        if (Math.Abs(down.X - topLeft.X) > cellW * 0.18) return false;
        return true;
    }

    /// <summary>由左上角、它右边、它下面三个圆推出整个网格，并转换成屏幕坐标。</summary>
    private void CommitCircleGrid(HackFrame shot, Circle topLeft, Circle rightNeighbor, Circle down)
    {
        int dx = rightNeighbor.X - topLeft.X, dy = down.Y - topLeft.Y;
        int[] radii = [topLeft.R, rightNeighbor.R, down.R];
        Array.Sort(radii);
        var radius = radii[1];
        for (var r = 0; r < GridRows; r++)
            for (var c = 0; c < GridCols; c++)
                _circles[r * GridCols + c] = new Circle(topLeft.X + c * dx, topLeft.Y + r * dy, radius, 999);
        int left = shot.Width, top = shot.Height, right = 0, bottom = 0;
        var pad = ScalePx(24, shot.Height);
        foreach (var circle in _circles)
        {
            left = Math.Min(left, circle.X - circle.R - pad);
            top = Math.Min(top, circle.Y - circle.R - pad);
            right = Math.Max(right, circle.X + circle.R + pad);
            bottom = Math.Max(bottom, circle.Y + circle.R + pad);
        }
        _roiX = Math.Max(0, left);
        _roiY = Math.Max(0, top);
        _roiW = Math.Min(shot.Width - 1, right) - _roiX;
        _roiH = Math.Min(shot.Height - 1, bottom) - _roiY;
        _circlesReady = true;
    }

    private void ScaleLockedGeometryToScreen(HackFrame shot)
    {
        var roiRight = ToScreenX(shot, _roiX + _roiW);
        var roiBottom = ToScreenY(shot, _roiY + _roiH);
        _roiX = ToScreenX(shot, _roiX);
        _roiY = ToScreenY(shot, _roiY);
        _roiW = Math.Max(1, roiRight - _roiX);
        _roiH = Math.Max(1, roiBottom - _roiY);
        for (var i = 0; i < GridCells; i++)
        {
            var c = _circles[i];
            _circles[i] = c with
            {
                X = ToScreenX(shot, c.X), Y = ToScreenY(shot, c.Y),
                R = Math.Max(1, Round(c.R * (shot.ToScreenX + shot.ToScreenY) * 0.5)),
            };
        }
    }

    private bool DetectCircles(HackFrame shot, int seedRoiX, int seedRoiY, int seedRoiW, int seedRoiH)
    {
        var cellW = (double)seedRoiW / GridCols;
        var cellH = (double)seedRoiH / GridRows;
        var seedR = Round(Math.Min(cellW, cellH) * 0.38);
        var maxDx = Math.Max(ScalePx(16, shot.Height), Round(cellW * 0.20));
        var maxDy = Math.Max(ScalePx(16, shot.Height), Round(cellH * 0.20));
        var radiusSlack = ScalePx(16, shot.Height);
        var x0Seed = seedRoiX + Round(0.5 * cellW);
        var y0Seed = seedRoiY + Round(0.5 * cellH);
        var topLeft = RefineCircleByEdge(shot, x0Seed, y0Seed, seedR, maxDx, maxDy, seedR - radiusSlack, seedR + radiusSlack);
        var edgeMinR = topLeft.R - Math.Max(ScalePx(3, shot.Height), radiusSlack / 2);
        var edgeMaxR = topLeft.R + Math.Max(ScalePx(3, shot.Height), radiusSlack / 2);
        var rightNeighbor = RefineCircleByEdge(shot, topLeft.X + Round(cellW), topLeft.Y, topLeft.R, maxDx,
            Math.Max(ScalePx(4, shot.Height), maxDy / 2), edgeMinR, edgeMaxR);
        var down = RefineCircleByEdge(shot, topLeft.X, topLeft.Y + Round(cellH), topLeft.R,
            Math.Max(ScalePx(4, shot.Height), maxDx / 2), maxDy, edgeMinR, edgeMaxR);
        if (!AcceptCircleTriplet(topLeft, rightNeighbor, down, seedR, cellW, cellH, 70))
        {
            _circlesReady = false;
            return false;
        }
        CommitCircleGrid(shot, topLeft, rightNeighbor, down);
        return true;
    }

    /// <summary>从「信号中继」标题条的位置推出圆圈网格所在的区域，再在里面精确找圆。</summary>
    private bool AutoLocateRoiFromShot(HackFrame shot)
    {
        int screenW = shot.Width, screenH = shot.Height;
        WhiteBar best;
        if (_geometry is { } geometry && geometry.WindowGeneration == shot.WindowGeneration && geometry.FrameW == shot.Width && geometry.FrameH == shot.Height)
            best = geometry.Signal;
        else if (!FindSignalRepeaterBar(shot, FindWhiteBars(shot), out best)) return false;
        if (best.W <= 0 || best.H <= 0) return false;

        // 标题条可能被文字打断，按「这一列有没有白色」找出最长的一段，作为标题的实际宽度
        var searchPad = ScalePx(80, screenH);
        var searchLeft = Math.Max(0, best.X - searchPad);
        var searchRight = Math.Min(screenW - 1, best.X + best.W + searchPad);
        var active = new bool[searchRight - searchLeft + 1];
        var activeHeightThreshold = Math.Max(2, best.H / 6);
        for (var xx = searchLeft; xx <= searchRight; xx++)
        {
            var count = 0;
            for (var yy = best.Y; yy < Math.Min(best.Y + best.H, screenH); yy++)
                if (At(shot, xx, yy, out var r, out var g, out var b) && IsWhiteUiPixel(r, g, b)) count++;
            active[xx - searchLeft] = count >= activeHeightThreshold;
        }
        int runStart = searchLeft, runEnd = searchRight, curStart = -1, gap = 0, bestLen = 0;
        for (var i = 0; i < active.Length; i++)
        {
            var x = searchLeft + i;
            if (active[i])
            {
                if (curStart < 0) curStart = x;
                gap = 0;
            }
            else if (curStart >= 0)
            {
                gap++;
                if (gap > ScalePx(24, screenH))
                {
                    var end = x - gap;
                    var len = end - curStart;
                    if (len > bestLen) { bestLen = len; runStart = curStart; runEnd = end; }
                    curStart = -1;
                    gap = 0;
                }
            }
        }
        if (curStart >= 0 && searchRight - curStart > bestLen)
        {
            runStart = curStart;
            runEnd = searchRight;
            bestLen = runEnd - runStart;
        }
        var refinedTitleW = Math.Max(ScalePx(620, screenH), Math.Min(ScalePx(920, screenH), bestLen));
        var titleX = Math.Max(0, runStart);
        var titleY = best.Y;
        var roiW = (int)(refinedTitleW * 0.86);
        roiW = Math.Max(ScalePx(520, screenH), Math.Min(ScalePx(760, screenH), roiW));
        var roiH = (int)(roiW * 0.795);
        var roiX = titleX + (int)(refinedTitleW * 0.075);
        var roiY = titleY + (int)(roiW * 0.102);
        if (roiX + roiW > screenW) roiW = screenW - roiX;
        if (roiY + roiH > screenH) roiH = screenH - roiY;
        if (roiW < ScalePx(300, screenH) || roiH < ScalePx(250, screenH)) return false;
        _circlesReady = false;
        if (!DetectCircles(shot, roiX, roiY, roiW, roiH))
        {
            _roiX = roiX; _roiY = roiY; _roiW = roiW; _roiH = roiH;
            ScaleLockedGeometryToScreen(shot);
            return false;
        }
        ScaleLockedGeometryToScreen(shot);
        return true;
    }

    private bool AutoLocateRoi() => host.Capture() is { } shot && AutoLocateRoiFromShot(shot);

    // ───────── 页面识别 ─────────

    /// <summary>逐行找足够长的白色横条（标题栏）。</summary>
    private static List<WhiteBar> FindWhiteBars(HackFrame shot)
    {
        int screenW = shot.Width, screenH = shot.Height;
        var yMin = Math.Max(ScalePx(80, screenH), screenH / 10);
        var yMax = Math.Min(screenH / 2, screenH - ScalePx(260, screenH));
        var minRun = ScalePx(274, screenH);
        var maxGap = ScalePx(18, screenH);
        var barPadTop = ScalePx(5, screenH);
        var barPadBottom = ScalePx(18, screenH);
        var barHeight = ScalePx(24, screenH);
        var densityStep = ScalePx(6, screenH);
        var bars = new List<WhiteBar>();
        for (var y = yMin; y < yMax; y += 2)
        {
            var inRun = false;
            int runStart = 0, gap = 0;
            for (var x = 0; x < screenW; x += 2)
            {
                At(shot, x, y, out var r, out var g, out var b);
                if (IsWhiteUiPixel(r, g, b))
                {
                    if (!inRun) { inRun = true; runStart = x; }
                    gap = 0;
                }
                else if (inRun)
                {
                    gap += 2;
                    if (gap > maxGap)
                    {
                        var runEnd = x - gap;
                        var runW = runEnd - runStart;
                        if (runW >= minRun)
                        {
                            int density = 0, samples = 0;
                            for (var yy = Math.Max(y - barPadTop, 0); yy <= Math.Min(y + barPadBottom, screenH - 1); yy += 2)
                                for (var xx = runStart; xx <= runEnd; xx += densityStep)
                                {
                                    At(shot, xx, yy, out var qr, out var qg, out var qb);
                                    if (IsWhiteUiPixel(qr, qg, qb)) density++;
                                    samples++;
                                }
                            bars.Add(new WhiteBar(runStart, y - barPadTop, runW, barHeight, samples > 0 ? density * 100 / samples : 0));
                        }
                        inRun = false;
                        gap = 0;
                    }
                }
            }
        }
        return bars;
    }

    /// <summary>「信号中继」标题条：在画面上部偏中间、足够宽，取最宽最靠下的那条。</summary>
    private static bool FindSignalRepeaterBar(HackFrame shot, List<WhiteBar> bars, out WhiteBar signal)
    {
        var screenH = shot.Height;
        var bestRank = -1000000;
        signal = default;
        foreach (var b in bars)
        {
            var belowTopBoxes = b.Y > screenH * 16 / 100;
            var notTooLow = b.Y < screenH * 34 / 100;
            var wideEnough = b.W > ScalePx(537, screenH);
            if (!belowTopBoxes || !notTooLow || !wideEnough || b.Score < 25) continue;
            var rank = b.W * 3 + b.Y * 6;
            if (rank > bestRank) { bestRank = rank; signal = b; }
        }
        return bestRank != -1000000;
    }

    /// <summary>页面特征：信号中继标题条，它右边同一高度的另一个标题条，以及上方的大标题。</summary>
    private static Geometry? AnalyzeMinigamePage(HackFrame shot)
    {
        var bars = FindWhiteBars(shot);
        if (!FindSignalRepeaterBar(shot, bars, out var signal)) return null;
        var titleYSlack = ScalePx(35, shot.Height);
        var hasRightTitle = false;
        var topTitleCount = 0;
        WhiteBar rightTitle = default, topTitle = default;
        foreach (var b in bars)
        {
            var centerY = b.Y + b.H / 2;
            if (Math.Abs(centerY - (signal.Y + signal.H / 2)) < titleYSlack && b.X > signal.X + signal.W * 3 / 4 &&
                b.W > ScalePx(268, shot.Height) && b.Score >= 28)
            {
                hasRightTitle = true;
                if (b.Score > rightTitle.Score) rightTitle = b;
            }
            if (centerY < signal.Y - titleYSlack && centerY > shot.Height * 8 / 100 && centerY < shot.Height * 24 / 100 &&
                b.W > ScalePx(345, shot.Height) && b.Score >= 28)
            {
                topTitleCount++;
                if (b.Score > topTitle.Score) topTitle = b;
            }
        }
        if (!hasRightTitle || topTitleCount < 1) return null;
        return new Geometry
        {
            WindowGeneration = shot.WindowGeneration, FrameW = shot.Width, FrameH = shot.Height,
            Signal = signal, RightTitle = rightTitle, TopTitle = topTitle,
        };
    }

    private static bool ValidateMinigameGeometry(HackFrame shot, Geometry? geometry)
    {
        if (geometry is null || geometry.WindowGeneration != shot.WindowGeneration || geometry.FrameW != shot.Width || geometry.FrameH != shot.Height) return false;
        bool BarPresent(WhiteBar bar)
        {
            int left = Math.Clamp(bar.X, 0, shot.Width), top = Math.Clamp(bar.Y, 0, shot.Height);
            int right = Math.Clamp(bar.X + bar.W, 0, shot.Width), bottom = Math.Clamp(bar.Y + bar.H, 0, shot.Height);
            if (right <= left || bottom <= top) return false;
            var step = Math.Max(1, Math.Min(right - left, bottom - top) / 10);
            int white = 0, samples = 0;
            for (var y = top; y < bottom; y += step)
                for (var x = left; x < right; x += step)
                {
                    At(shot, x, y, out var r, out var g, out var b);
                    if (IsWhiteUiPixel(r, g, b)) white++;
                    samples++;
                }
            return samples > 0 && white * 100 >= samples * 2;
        }
        return BarPresent(geometry.Signal) && BarPresent(geometry.RightTitle) && BarPresent(geometry.TopTitle);
    }

    private bool CheckMinigamePage(HackFrame? shot)
    {
        if (shot is null)
        {
            _geometry = null;
            _circlesReady = false;
            return false;
        }
        if (ValidateMinigameGeometry(shot, _geometry)) return true;
        var relocated = AnalyzeMinigamePage(shot);
        if (relocated is null)
        {
            _geometry = null;
            _circlesReady = false;
            return false;
        }
        _geometry = relocated;
        _circlesReady = AutoLocateRoiFromShot(shot);
        return true;
    }

    // ───────── 关卡指示框 ─────────

    private static bool FindDecodedDigitsBar(HackFrame shot, List<WhiteBar> bars, WhiteBar signal, out WhiteBar decoded)
    {
        var titleYSlack = ScalePx(35, shot.Height);
        var signalCenterY = signal.Y + signal.H / 2;
        var bestRank = -1000000;
        decoded = default;
        foreach (var b in bars)
        {
            int centerX = b.X + b.W / 2, centerY = b.Y + b.H / 2;
            if (Math.Abs(centerY - signalCenterY) > titleYSlack) continue;
            if (b.X <= signal.X + signal.W * 3 / 4) continue;
            if (b.W < signal.W * 25 / 100 || b.W > signal.W * 70 / 100) continue;
            if (b.Score < 28) continue;
            var rank = b.W * 4 - Math.Abs(centerY - signalCenterY) * 16 + centerX / 8;
            if (rank > bestRank) { bestRank = rank; decoded = b; }
        }
        return bestRank != -1000000;
    }

    private static List<HLine> FindHorizontalRuns(HackFrame shot, UiRect region, int minLen, int maxLen, int maxGap)
    {
        var lines = new List<HLine>();
        var left = Math.Max(0, Math.Min(region.Left, shot.Width - 1));
        var top = Math.Max(0, Math.Min(region.Top, shot.Height - 1));
        var right = Math.Max(left, Math.Min(region.Right, shot.Width - 1));
        var bottom = Math.Max(top, Math.Min(region.Bottom, shot.Height - 1));
        maxGap = Math.Max(1, maxGap);
        for (var y = top; y <= bottom; y++)
        {
            var inRun = false;
            int runStart = left, lastWhite = left, gap = 0;
            for (var x = left; x <= right; x++)
            {
                var white = At(shot, x, y, out var r, out var g, out var b) && IsLevelLinePixel(r, g, b);
                if (white)
                {
                    if (!inRun) { inRun = true; runStart = x; }
                    lastWhite = x;
                    gap = 0;
                }
                else if (inRun)
                {
                    gap++;
                    if (gap > maxGap)
                    {
                        var len = lastWhite - runStart + 1;
                        if (len >= minLen && len <= maxLen) lines.Add(new HLine(runStart, y, lastWhite));
                        inRun = false;
                        gap = 0;
                    }
                }
            }
            if (inRun)
            {
                var len = lastWhite - runStart + 1;
                if (len >= minLen && len <= maxLen) lines.Add(new HLine(runStart, y, lastWhite));
            }
        }
        return lines;
    }

    /// <summary>关卡指示框：右侧标题下方、圆圈网格最下一行附近的一个横长方框。</summary>
    private bool FindLevelBlock(HackFrame shot, out UiRect block)
    {
        block = default;
        var scaleH = shot.Height;
        if (!_circlesReady) return false;
        var bars = FindWhiteBars(shot);
        if (!FindSignalRepeaterBar(shot, bars, out var signal)) return false;
        if (!FindDecodedDigitsBar(shot, bars, signal, out var decoded)) return false;
        var bottom = 0;
        var centers = new List<int>();
        var radii = new List<int>();
        for (var row = 0; row < GridRows; row++)
        {
            var circle = _circles[row * GridCols + GridCols - 1];
            var cy = ToShotY(shot, circle.Y);
            var r = Math.Max(1, Round(circle.R / Math.Max(0.0001, shot.ToScreenY)));
            centers.Add(cy);
            radii.Add(r);
            bottom = Math.Max(bottom, cy + r);
        }
        centers.Sort();
        radii.Sort();
        var radius = radii[radii.Count / 2];
        var search = new UiRect(Math.Max(0, decoded.X - ScalePx(8, scaleH)), Math.Max(0, centers[^1] - radius),
            Math.Min(shot.Width - 1, decoded.X + decoded.W + ScalePx(8, scaleH)), Math.Min(shot.Height - 1, bottom + ScalePx(150, scaleH)));
        if (search.Right <= search.Left || search.Bottom <= search.Top) return false;
        var minBlockW = Math.Max(ScalePx(240, scaleH), decoded.W * 70 / 100);
        var maxBlockW = decoded.W + ScalePx(36, scaleH);
        var lines = FindHorizontalRuns(shot, search, minBlockW, maxBlockW, ScalePx(20, scaleH));
        var bestScore = -1;
        UiRect best = default;
        foreach (var topLine in lines)
            foreach (var bottomLine in lines)
            {
                if (bottomLine.Y <= topLine.Y) continue;
                var h = bottomLine.Y - topLine.Y;
                if (h < ScalePx(90, scaleH) || h > ScalePx(150, scaleH)) continue;
                var left = Math.Min(topLine.X1, bottomLine.X1);
                var right = Math.Max(topLine.X2, bottomLine.X2);
                var w = right - left + 1;
                if (w < minBlockW || w > maxBlockW) continue;
                if (Math.Abs(topLine.X1 - bottomLine.X1) > ScalePx(28, scaleH)) continue;
                if (Math.Abs(topLine.X2 - bottomLine.X2) > ScalePx(28, scaleH)) continue;
                var decodedCenterX = decoded.X + decoded.W / 2;
                var score = w * h - Math.Abs((left + right) / 2 - decodedCenterX);
                if (score > bestScore) { bestScore = score; best = new UiRect(left, topLine.Y, right, bottomLine.Y); }
            }
        if (bestScore < 0) return false;
        block = best;
        return true;
    }

    /// <summary>方框里表示当前关卡的那个正方形焦点框。</summary>
    private static bool FindLevelFocusFrame(HackFrame shot, UiRect block, out UiRect focus)
    {
        var scaleH = shot.Height;
        var inner = new UiRect(block.Left + ScalePx(8, scaleH), block.Top + ScalePx(8, scaleH), block.Right - ScalePx(8, scaleH), block.Bottom - ScalePx(18, scaleH));
        var lines = FindHorizontalRuns(shot, inner, ScalePx(54, scaleH), ScalePx(90, scaleH), ScalePx(6, scaleH));
        var bestScore = -1;
        UiRect best = default;
        foreach (var top in lines)
            foreach (var bottom in lines)
            {
                if (bottom.Y <= top.Y) continue;
                var h = bottom.Y - top.Y + 1;
                if (h < ScalePx(50, scaleH) || h > ScalePx(86, scaleH)) continue;
                int wTop = top.X2 - top.X1 + 1, wBottom = bottom.X2 - bottom.X1 + 1;
                if (Math.Abs(top.X1 - bottom.X1) > ScalePx(12, scaleH)) continue;
                if (Math.Abs(top.X2 - bottom.X2) > ScalePx(12, scaleH)) continue;
                var left = Math.Min(top.X1, bottom.X1);
                var right = Math.Max(top.X2, bottom.X2);
                var w = right - left + 1;
                if (Math.Abs(w - h) > ScalePx(18, scaleH)) continue;
                var score = w * h + Math.Min(wTop, wBottom) * 8;
                if (score > bestScore) { bestScore = score; best = new UiRect(left, top.Y, right, bottom.Y); }
            }
        focus = best;
        return bestScore >= 0;
    }

    /// <summary>当前是第几关（1–4），看不出来返回 0。</summary>
    private int DetectLevelSlotFromShot(HackFrame shot)
    {
        if (!FindLevelBlock(shot, out var block)) return 0;
        if (!FindLevelFocusFrame(shot, block, out var focus)) return 0;
        double blockW = Math.Max(1, block.Right - block.Left + 1);
        var focusCenterX = (focus.Left + focus.Right) * 0.5;
        var ratio = (focusCenterX - block.Left) / blockW;
        var slot = (int)Math.Floor(ratio * 4.0) + 1;
        return Math.Max(1, Math.Min(4, slot));
    }

    private int DetectLevelSlotFromCaptured(HackFrame? shot)
    {
        var slot = shot is null ? 0 : DetectLevelSlotFromShot(shot);
        if (slot > 0) _levelFocusMissFrames = 0;
        else _levelFocusMissFrames++;
        return slot;
    }

    /// <summary>关卡号连续两帧一致才算数；返回 changed 表示进入了新的一关。</summary>
    private bool UpdateObservedLevelSlot(int detectedSlot, out bool changed, bool initialIsChange)
    {
        changed = false;
        if (detectedSlot <= 0)
        {
            if (_levelFocusMissFrames < LevelFocusHideAfterMissFrames) return _observedLevelStableFrames >= 2;
            _observedLevelSlot = 0;
            _observedLevelStableFrames = 0;
            return false;
        }
        if (detectedSlot == _observedLevelSlot) _observedLevelStableFrames++;
        else
        {
            _observedLevelSlot = detectedSlot;
            _observedLevelStableFrames = 1;
        }
        if (_observedLevelStableFrames < 2) return false;
        if (_levelSlot == 0)
        {
            _levelSlot = detectedSlot;
            if (initialIsChange) changed = true;
            return true;
        }
        if (detectedSlot != _levelSlot)
        {
            _levelSlot = detectedSlot;
            changed = true;
        }
        return true;
    }

    // ───────── 每帧读取 ─────────

    /// <summary>只截圆圈网格那一块：读每个圆里的蓝色（闪烁图案）和每个圆外圈的白色（光标位置）。</summary>
    private (int[] pattern, int selectedIndex) CaptureFrame(bool detectBlue)
    {
        var pattern = new int[GridCells];
        var captured = host.Capture(new HackRect(_roiX, _roiY, _roiX + _roiW, _roiY + _roiH));
        if (captured is null) return (pattern, -1);
        var downScale = (captured.ToScreenX + captured.ToScreenY) * 0.5;
        int captureW = captured.Width, captureH = captured.Height;
        if (detectBlue)
            for (var idx = 0; idx < GridCells; idx++)
            {
                var circle = _circles[idx];
                var cx = (double)(circle.X - captured.ScreenX) / captured.ToScreenX;
                var cy = (double)(circle.Y - captured.ScreenY) / captured.ToScreenY;
                var radius = Math.Max(6.0, circle.R * 0.36 / downScale);
                var radiusSq = radius * radius;
                var x0 = Math.Max(0, (int)Math.Floor(cx - radius));
                var x1 = Math.Min(captureW - 1, (int)Math.Ceiling(cx + radius));
                var y0 = Math.Max(0, (int)Math.Floor(cy - radius));
                var y1 = Math.Min(captureH - 1, (int)Math.Ceiling(cy + radius));
                int blue = 0, samples = 0;
                for (var y = y0; y < y1; y += 2)
                    for (var x = x0; x < x1; x += 2)
                    {
                        double dx = x - cx, dy = y - cy;
                        if (dx * dx + dy * dy > radiusSq) continue;
                        At(captured, x, y, out var r, out var g, out var b);
                        if (IsBluePixel(r, g, b)) blue++;
                        samples++;
                    }
                var pct = samples > 0 ? blue * 100 / samples : 0;
                pattern[idx] = pct >= ThresholdPct ? 1 : 0;
            }
        int bestIdx = -1, bestScore = 0;
        for (var idx = 0; idx < GridCells; idx++)
        {
            var circle = _circles[idx];
            var cx = (double)(circle.X - captured.ScreenX) / captured.ToScreenX;
            var cy = (double)(circle.Y - captured.ScreenY) / captured.ToScreenY;
            var rLocal = circle.R / downScale;
            var inner = rLocal + Math.Max(3.0, rLocal * 0.10);
            var outer = rLocal + Math.Max(10.0, rLocal * 0.38);
            double innerSq = inner * inner, outerSq = outer * outer;
            var x0 = Math.Max(0, (int)Math.Floor(cx - outer));
            var x1 = Math.Min(captureW - 1, (int)Math.Ceiling(cx + outer));
            var y0 = Math.Max(0, (int)Math.Floor(cy - outer));
            var y1 = Math.Min(captureH - 1, (int)Math.Ceiling(cy + outer));
            int white = 0, samples = 0;
            for (var y = y0; y < y1; y += 2)
                for (var x = x0; x < x1; x += 2)
                {
                    double dx = x - cx, dy = y - cy;
                    var d2 = dx * dx + dy * dy;
                    if (d2 < innerSq || d2 > outerSq) continue;
                    At(captured, x, y, out var r, out var g, out var b);
                    if (IsWhiteUiPixel(r, g, b)) white++;
                    samples++;
                }
            var score = samples > 0 ? white * 100 / samples : 0;
            if (score > bestScore) { bestScore = score; bestIdx = idx; }
        }
        return (pattern, bestScore >= 10 ? bestIdx : -1);
    }

    // ───────── 答题 ─────────

    private static bool Any(int[] p) => p.Any(v => v != 0);

    private static int TargetRowForColumn(int[] pattern, int col)
    {
        if (col < 0 || col >= GridCols) return -1;
        for (var row = 0; row < GridRows; row++)
            if (pattern[row * GridCols + col] != 0) return row;
        return -1;
    }

    private static int LastTargetColumn(int[] pattern)
    {
        for (var col = GridCols - 1; col >= 0; col--)
            if (TargetRowForColumn(pattern, col) >= 0) return col;
        return -1;
    }

    private bool WaitingColumnVerify() => _tracker.TargetReady && _pendingVerifyCol >= 0 && Tick < _nextInputAt;
    private bool AwaitingFinalSubmitResult() => _tracker.TargetReady && _finalSubmitAt != 0;

    /// <summary>一次闪烁结束：同一个图案连续出现 3 次就锁定为答案。</summary>
    private void RecordCompletedFlash(int[] p)
    {
        if (!Any(p)) return;
        _tracker.FlashCount++;
        if (_tracker.HasLastFlash && p.SequenceEqual(_tracker.LastFlash)) _tracker.RepeatCount++;
        else
        {
            _tracker.LastFlash = (int[])p.Clone();
            _tracker.HasLastFlash = true;
            _tracker.RepeatCount = 1;
        }
        if (_tracker.RepeatCount >= 3 && !_tracker.TargetReady)
        {
            _tracker.Target = (int[])p.Clone();
            _tracker.TargetReady = true;
            _autoInputEnabled = true;
            _pendingVerifyCol = -1;
            _finalSubmitAt = 0;
        }
    }

    private void TickAutoInput()
    {
        if (_selectedIndex == _lastSelectedIndex && _selectedIndex >= 0) _selectedStableFrames++;
        else
        {
            _lastSelectedIndex = _selectedIndex;
            _selectedStableFrames = _selectedIndex >= 0 ? 1 : 0;
        }
        if (!_autoInputEnabled || !_tracker.TargetReady) return;
        if (_inputJob is not null)
        {
            if (_inputJob.Pending) return;
            if (_inputJob.Succeeded) _nextInputAt = Tick + 120;
            else _pendingVerifyCol = -1;
            _inputJob = null;
            return;
        }
        if (Tick < _nextInputAt) return;
        if (_selectedIndex < 0) return;
        if (_selectedStableFrames < SelectedStableFramesBeforeInput) return;
        var selectedRow = _selectedIndex / GridCols;
        var selectedCol = _selectedIndex % GridCols;
        var lastTargetCol = LastTargetColumn(_tracker.Target);
        if (lastTargetCol < 0) return;
        if (_pendingVerifyCol >= 0)
        {
            var pendingCol = _pendingVerifyCol;
            _pendingVerifyCol = -1;
            // 光标已经跳到后面一列：上一列确认成功
            if (selectedCol > pendingCol && pendingCol >= lastTargetCol)
            {
                _autoInputEnabled = false;
                return;
            }
        }
        var targetRow = TargetRowForColumn(_tracker.Target, selectedCol);
        if (targetRow < 0) return;
        var keys = new List<HackKey>();
        for (var row = selectedRow; row < targetRow; row++) keys.Add(new HackKey(0x28));
        for (var row = selectedRow; row > targetRow; row--) keys.Add(new HackKey(0x26));
        keys.Add(new HackKey(0x0D));
        _inputJob = host.QueueSequence(keys, 0);
        _nextInputAt = 0;
        _pendingVerifyCol = selectedCol;
        if (selectedCol >= lastTargetCol) _finalSubmitAt = Tick;
    }

    private void ClearLevelState()
    {
        _blankFrames = 0;
        _selectedIndex = -1;
        _lastSelectedIndex = -1;
        _selectedStableFrames = 0;
        _nextInputAt = 0;
        _inputJob = null;
        _autoInputEnabled = false;
        _pendingVerifyCol = -1;
        _finalSubmitAt = 0;
        _flashUnion = new int[GridCells];
        _tracker = new PatternTracker();
    }

    private void ClearRuntimeState()
    {
        _circlesReady = false;
        _levelSlot = 0;
        _observedLevelSlot = 0;
        _observedLevelStableFrames = 0;
        _levelFocusMissFrames = 0;
        ClearLevelState();
    }

    // ───────── 入口 ─────────

    public bool DetectInGame()
    {
        var shot = host.Capture();
        var geometry = shot is null ? null : AnalyzeMinigamePage(shot);
        if (geometry is null)
        {
            ResetInGameCache();
            return false;
        }
        _geometry = geometry;
        if (!AutoLocateRoiFromShot(shot!))
        {
            ResetInGameCache();
            return false;
        }
        return true;
    }

    public void ResetInGameCache()
    {
        _geometry = null;
        _circlesReady = false;
    }

    /// <summary>开始一次会话：清掉上一次的状态，但保留识别阶段刚找到的网格位置。</summary>
    private void ResetForSession()
    {
        var keepLocation = _geometry is not null && _circlesReady;
        var geometry = _geometry;
        var circles = (Circle[])_circles.Clone();
        int roiX = _roiX, roiY = _roiY, roiW = _roiW, roiH = _roiH;
        ClearRuntimeState();
        if (!keepLocation) return;
        _geometry = geometry;
        circles.CopyTo(_circles, 0);
        _roiX = roiX; _roiY = roiY; _roiW = roiW; _roiH = roiH;
        _circlesReady = true;
    }

    public bool RunSession(Func<bool> stopRequested, Action<string> status)
    {
        var lastStatus = "";
        void SetStatus(string text)
        {
            if (text == lastStatus) return;
            lastStatus = text;
            status(text);
        }
        ResetForSession();
        if (!_circlesReady) AutoLocateRoi();
        var lostFrames = 0;
        var completedAnyLevel = false;
        SetStatus("flashing: locating");
        while (!stopRequested())
        {
            var loopStart = Stopwatch.GetTimestamp();
            var detectBlue = !_tracker.TargetReady;
            var shot = host.Capture();
            UpdateObservedLevelSlot(DetectLevelSlotFromCaptured(shot), out var levelChanged, AwaitingFinalSubmitResult());
            if (levelChanged)
            {
                completedAnyLevel = true;
                SetStatus("flashing: next level");
                ClearLevelState();
                Thread.Sleep(0);
                continue;
            }
            if (!CheckMinigamePage(shot))
            {
                if (++lostFrames >= 15)
                {
                    SetStatus("flashing: exited");
                    ClearRuntimeState();
                    break;
                }
                SetStatus("flashing: confirming exit");
                Thread.Sleep(50);
                continue;
            }
            lostFrames = 0;
            if (WaitingColumnVerify())
            {
                _selectedIndex = -1;
                var lastTargetCol = LastTargetColumn(_tracker.Target);
                SetStatus(_pendingVerifyCol >= lastTargetCol ? "flashing: waiting next level" : "flashing: verifying column");
                Thread.Sleep(0);
                continue;
            }
            if (AwaitingFinalSubmitResult())
            {
                _selectedIndex = -1;
                SetStatus("flashing: waiting next level");
                Thread.Sleep(0);
                continue;
            }
            if (!_circlesReady)
            {
                SetStatus("flashing: locating");
                AutoLocateRoi();
                Thread.Sleep(30);
                continue;
            }
            SetStatus(detectBlue ? "flashing: reading pattern" : "flashing: auto input");
            var (framePattern, selectedIndex) = CaptureFrame(detectBlue);
            _selectedIndex = selectedIndex;
            if (detectBlue)
            {
                if (Any(framePattern))
                {
                    // 一次闪烁可能跨好几帧，并起来；连续两帧空白才算这次闪烁结束
                    _blankFrames = 0;
                    for (var i = 0; i < GridCells; i++) _flashUnion[i] = _flashUnion[i] != 0 || framePattern[i] != 0 ? 1 : 0;
                }
                else
                {
                    _blankFrames++;
                    if (Any(_flashUnion) && _blankFrames >= 2)
                    {
                        RecordCompletedFlash(_flashUnion);
                        _flashUnion = new int[GridCells];
                    }
                }
            }
            TickAutoInput();
            var elapsedMs = (Stopwatch.GetTimestamp() - loopStart) * 1000.0 / Stopwatch.Frequency;
            var sleepMs = TimerMs - Round(elapsedMs);
            Thread.Sleep(sleepMs > 1 ? sleepMs : 0);
        }
        ResetInGameCache();
        return completedAnyLevel;
    }
}
