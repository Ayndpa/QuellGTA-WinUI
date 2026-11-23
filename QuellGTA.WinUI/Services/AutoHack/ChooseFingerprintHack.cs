using System.Numerics;
using static QuellGTA.Services.AutoHack.HackUtil;

namespace QuellGTA.Services.AutoHack;

/// <summary>
/// 「选择指纹」骇入（games/choose_fingerprint_module.cpp）：右边是一枚完整的目标指纹，左边 2 列 × 4 行共 8 个指纹碎片，
/// 其中 4 个取自目标指纹。用方向键移动光标、回车选中或取消，选对 4 个后长按 Tab 提交。
/// 目标指纹下方「信号」标题条下有一条短横线，标出当前是第几关（共 4 格），它挪到下一格就表示这一关过了。
///
/// 识别流程：
/// 1. 找三条白色标题条（目标、碎片、信号），按相对位置确认是这个小游戏（detectMinigame）；
/// 2. 在碎片标题条下方，用灰度梯度的行列投影找 8 个方框的边线（detectComponentBoxesByBorder）；
/// 3. 每个碎片在目标指纹上做归一化互相关（ZNCC），8 个碎片共用同一个缩放倍数，得分最高的 4 个是答案（answerScores）；
/// 4. 每帧读出哪些碎片已选中（指纹纹路变亮）、光标在哪（方框四角的括号）（markStates），据此规划按键。
/// 识别结果画在叠加层上：目标框洋红，正确碎片绿框、错误碎片红框，光标粉点，已选中黄点，关卡位置绿色三角。
/// </summary>
public sealed class ChooseFingerprintHack(IHackHost host) : IHackGame
{
    public string Name => "选择指纹";

    // 原版用的是扫描码，这里换成对应的虚拟键：→ ← ↓ ↑ 回车 Tab
    private const int VkRight = 0x27, VkLeft = 0x25, VkDown = 0x28, VkUp = 0x26, VkEnter = 0x0D, VkTab = 0x09;
    private const int FrameDelayMs = 10; // kFrameDelayMs

    // ───────── 数据结构 ─────────

    /// <summary>C++ 的 Rect：左上角加宽高。</summary>
    private readonly record struct Box(int X, int Y, int W, int H)
    {
        public int Right => X + W;
        public int Bottom => Y + H;
    }

    private readonly record struct Region(Box Rect, int Pixels, double Aspect, double Cx, double Cy);

    /// <summary>一帧画面加上它的灰度图（captureScreen 里算的 gray）。</summary>
    private sealed class Frame
    {
        public readonly HackFrame Source;
        public readonly int W, H;
        public readonly uint[] Px;
        public readonly byte[] Gray;

        public Frame(HackFrame source)
        {
            Source = source;
            W = source.Width;
            H = source.Height;
            Px = source.Bgra;
            Gray = new byte[W * H];
            for (var i = 0; i < Gray.Length; i++)
            {
                var p = Px[i];
                int r = (int)((p >> 16) & 255), g = (int)((p >> 8) & 255), b = (int)(p & 255);
                Gray[i] = (byte)((77 * r + 150 * g + 29 * b) >> 8);
            }
        }

        public void Rgb(int index, out int r, out int g, out int b)
        {
            var p = Px[index];
            r = (int)((p >> 16) & 255); g = (int)((p >> 8) & 255); b = (int)(p & 255);
        }
    }

    private struct TitleBars
    {
        public Box Target, Components, Signals;
        public bool HasTarget, HasComponents, HasSignals;
    }

    private struct RoiInfo
    {
        public bool IsMinigame;
        public Box Panel;
        public TitleBars Bars;
    }

    /// <summary>InGameGeometry：识别阶段找到的版面，窗口没变、三条标题条还在时直接沿用。</summary>
    private sealed class Geometry
    {
        public static readonly Geometry Invalid = new();
        public bool Valid;
        public ulong WindowGeneration;
        public int FrameW, FrameH;
        public RoiInfo Roi;
        public Box Target;
        public Box[] Components = [];
    }

    private struct BlockInfo
    {
        public int Index;
        public Box Rect;
        public double Score;
        public bool Correct, Selected, Cursor;
        public int CursorScore;
    }

    private sealed class OverlayState
    {
        public bool Visible;
        public ulong TargetHash;
        public Box Target;
        public int LevelMarker = -1;
        public Box LevelMarkerLine;
        public BlockInfo[] Blocks = [];
    }

    /// <summary>SolverCache：同一枚目标指纹只算一次答案。</summary>
    private sealed class SolverCache
    {
        public bool Valid;
        public ulong TargetHash;
        public Box Target;
        public BlockInfo[] BaseBlocks = [];
    }

    private enum AutomationPhase { Idle, Selecting, VerifyDelay, Submitting, WaitingLevel }

    private sealed class AutomationState
    {
        public AutomationPhase Phase = AutomationPhase.Idle;
        public ulong PlannedHash;
        public int PlannedLevelMarker = -1;
        public int ObservedLevelMarker = -1;
        public int LevelChangeFrames;
        public long VerifyAfter, SubmittedAt;
        public HackJob? InputJob;
    }

    private Geometry _detected = Geometry.Invalid; // gDetectedGeometry

    // ───────── 小工具 ─────────

    private static int OddKernel(int h, int w, double ratio, int minimum = 3)
    {
        var s = Math.Max(minimum, Round(Math.Min(h, w) * ratio));
        return s % 2 != 0 ? s : s + 1;
    }

    private static Box ClampRect(Box r, int w, int h)
    {
        var x = Math.Max(0, r.X);
        var y = Math.Max(0, r.Y);
        var rw = Math.Max(0, Math.Min(r.W, w - x));
        var rh = Math.Max(0, Math.Min(r.H, h - y));
        return new Box(x, y, rw, rh);
    }

    /// <summary>scaleRectToScreen：分析坐标的矩形换成屏幕坐标，并夹在截图区域里。</summary>
    private static Box ScaleRectToScreen(HackFrame f, Box r)
    {
        var x1 = f.ScreenX + Round(r.X * f.ToScreenX);
        var y1 = f.ScreenY + Round(r.Y * f.ToScreenY);
        var x2 = f.ScreenX + Round((r.X + r.W) * f.ToScreenX);
        var y2 = f.ScreenY + Round((r.Y + r.H) * f.ToScreenY);
        x1 = Math.Clamp(x1, f.ScreenX, f.ScreenX + f.ScreenW);
        y1 = Math.Clamp(y1, f.ScreenY, f.ScreenY + f.ScreenH);
        x2 = Math.Clamp(x2, f.ScreenX, f.ScreenX + f.ScreenW);
        y2 = Math.Clamp(y2, f.ScreenY, f.ScreenY + f.ScreenH);
        return new Box(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    private static Box PadRect(Box r, int w, int h, double ratio)
    {
        var pad = Round(Math.Max(r.W, r.H) * ratio);
        return ClampRect(new Box(r.X - pad, r.Y - pad, r.W + pad * 2, r.H + pad * 2), w, h);
    }

    /// <summary>以 1080p 为基准的像素数，按画面高度缩放（scaledPx）。</summary>
    private static int ScaledPx(Frame f, int px1080) => Math.Max(1, Round(px1080 * (f.H / 1080.0)));

    private static int ScaledArea(Frame f, int area1080)
    {
        var scale = f.H / 1080.0;
        return Math.Max(1, Round(area1080 * scale * scale));
    }

    /// <summary>灰度梯度：左右差的绝对值加上下差的绝对值，坐标夹到边内一格。</summary>
    private static int GradientAt(Frame f, int x, int y)
    {
        x = Math.Clamp(x, 1, f.W - 2);
        y = Math.Clamp(y, 1, f.H - 2);
        var dx = Math.Abs(f.Gray[y * f.W + x + 1] - f.Gray[y * f.W + x - 1]);
        var dy = Math.Abs(f.Gray[(y + 1) * f.W + x] - f.Gray[(y - 1) * f.W + x]);
        return dx + dy;
    }

    /// <summary>和信号中继模块同一套的白色 UI 判定（isFlashingStyleWhite），在 HDR 桌面上也可靠。</summary>
    private static bool IsFlashingStyleWhite(int red, int green, int blue) =>
        red > 175 && green > 175 && blue > 175 && Math.Abs(red - green) < 55 && Math.Abs(red - blue) < 55;

    private static byte[] UiWhiteMask(Frame f)
    {
        var mask = new byte[f.W * f.H];
        for (var i = 0; i < mask.Length; i++)
        {
            f.Rgb(i, out var r, out var g, out var b);
            mask[i] = IsFlashingStyleWhite(r, g, b) ? (byte)1 : (byte)0;
        }
        return mask;
    }

    // ───────── 形态学（滑动窗口实现的方形膨胀 / 腐蚀） ─────────

    private static byte[] Dilate(byte[] src, int w, int h, int k)
    {
        var r = k / 2;
        byte[] tmp = new byte[w * h], dst = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            var sum = 0;
            for (var x = -r; x <= r; x++) if (0 <= x && x < w) sum += src[y * w + x];
            for (var x = 0; x < w; x++)
            {
                tmp[y * w + x] = sum > 0 ? (byte)1 : (byte)0;
                int oldx = x - r, newx = x + r + 1;
                if (0 <= oldx && oldx < w) sum -= src[y * w + oldx];
                if (0 <= newx && newx < w) sum += src[y * w + newx];
            }
        }
        for (var x = 0; x < w; x++)
        {
            var sum = 0;
            for (var y = -r; y <= r; y++) if (0 <= y && y < h) sum += tmp[y * w + x];
            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = sum > 0 ? (byte)1 : (byte)0;
                int oldy = y - r, newy = y + r + 1;
                if (0 <= oldy && oldy < h) sum -= tmp[oldy * w + x];
                if (0 <= newy && newy < h) sum += tmp[newy * w + x];
            }
        }
        return dst;
    }

    private static byte[] Erode(byte[] src, int w, int h, int k)
    {
        var r = k / 2;
        byte[] tmp = new byte[w * h], dst = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            int sum = 0, cnt = 0;
            for (var x = -r; x <= r; x++) if (0 <= x && x < w) { sum += src[y * w + x]; cnt++; }
            for (var x = 0; x < w; x++)
            {
                tmp[y * w + x] = sum == cnt ? (byte)1 : (byte)0;
                int oldx = x - r, newx = x + r + 1;
                if (0 <= oldx && oldx < w) { sum -= src[y * w + oldx]; cnt--; }
                if (0 <= newx && newx < w) { sum += src[y * w + newx]; cnt++; }
            }
        }
        for (var x = 0; x < w; x++)
        {
            int sum = 0, cnt = 0;
            for (var y = -r; y <= r; y++) if (0 <= y && y < h) { sum += tmp[y * w + x]; cnt++; }
            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = sum == cnt ? (byte)1 : (byte)0;
                int oldy = y - r, newy = y + r + 1;
                if (0 <= oldy && oldy < h) { sum -= tmp[oldy * w + x]; cnt--; }
                if (0 <= newy && newy < h) { sum += tmp[newy * w + x]; cnt++; }
            }
        }
        return dst;
    }

    private static byte[] CloseMask(byte[] m, int w, int h, int k) => Erode(Dilate(m, w, h, k), w, h, k);

    /// <summary>同一行里两段白色之间的空隙不超过 maxGap 就填上（标题条里的黑字会把条打断）。</summary>
    private static void BridgeHorizontalGaps(byte[] mask, int w, int h, int maxGap)
    {
        for (var y = 0; y < h; y++)
        {
            var previous = -1;
            for (var x = 0; x < w; x++)
            {
                if (mask[y * w + x] == 0) continue;
                if (previous >= 0 && x - previous - 1 <= maxGap)
                    Array.Fill(mask, (byte)1, y * w + previous + 1, x - previous - 1);
                previous = x;
            }
        }
    }

    /// <summary>8 连通区域，像素数不少于 minPixels 的才留下。</summary>
    private static List<Region> ConnectedRegions(byte[] mask, int w, int h, int minPixels)
    {
        var work = (byte[])mask.Clone();
        var regions = new List<Region>();
        var q = new List<int>(4096);
        for (var start = 0; start < w * h; start++)
        {
            if (work[start] == 0) continue;
            int sx = start % w, sy = start / w;
            int minx = sx, maxx = sx, miny = sy, maxy = sy, pixels = 0;
            q.Clear();
            q.Add(start);
            work[start] = 0;
            for (var qi = 0; qi < q.Count; qi++)
            {
                int p = q[qi], x = p % w, y = p / w;
                pixels++;
                minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
                miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if ((dx == 0 && dy == 0) || nx < 0 || nx >= w) continue;
                        var np = ny * w + nx;
                        if (work[np] != 0)
                        {
                            work[np] = 0;
                            q.Add(np);
                        }
                    }
                }
            }
            if (pixels >= minPixels)
            {
                var r = new Box(minx, miny, maxx - minx + 1, maxy - miny + 1);
                regions.Add(new Region(r, pixels, (double)r.W / Math.Max(1, r.H), r.X + r.W / 2.0, r.Y + r.H / 2.0));
            }
        }
        return regions;
    }

    // ───────── 标题条与版面 ─────────

    private static bool IsTitleBarCandidate(Frame f, Region rg)
    {
        var r = rg.Rect;
        var asp = r.W / (double)Math.Max(1, r.H);
        return r.W > ScaledPx(f, 269) && r.H >= ScaledPx(f, 7) && r.H <= ScaledPx(f, 48) && asp > 5.0 &&
               rg.Cy > f.H * 0.04 && rg.Cy < f.H * 0.86;
    }

    /// <summary>标题条和下面的白色内容连成了一大块时，从顶部切出标题条那一段。</summary>
    private static bool ExtractTitleStripFromTallRegion(Frame f, byte[] bright, Region rg, out Region result)
    {
        result = default;
        var r = rg.Rect;
        if (!(r.W > ScaledPx(f, 269) && r.H > ScaledPx(f, 48) && r.H < ScaledPx(f, 140))) return false;
        if (!(rg.Cy > f.H * 0.04 && rg.Cy < f.H * 0.86)) return false;

        var rowOn = Math.Max(4, Round(r.W * 0.012));
        var maxGap = ScaledPx(f, 4);
        var maxSearchH = Math.Min(r.H, ScaledPx(f, 62));
        int start = -1, end = -1, gap = 0;
        for (var yy = 0; yy < maxSearchH; yy++)
        {
            var y = r.Y + yy;
            var count = 0;
            var row = y * f.W + r.X;
            for (var x = 0; x < r.W; x++) count += bright[row + x] != 0 ? 1 : 0;
            if (count >= rowOn)
            {
                if (start < 0) start = yy;
                end = yy;
                gap = 0;
            }
            else if (start >= 0 && ++gap > maxGap) break;
        }
        if (start < 0 || end < start) return false;
        int y0 = r.Y + start, y1 = r.Y + end;
        int minx = r.X + r.W, maxx = r.X - 1, pixels = 0;
        for (var y = y0; y <= y1; y++)
        {
            var row = y * f.W;
            for (var x = r.X; x < r.X + r.W; x++)
            {
                if (bright[row + x] == 0) continue;
                minx = Math.Min(minx, x);
                maxx = Math.Max(maxx, x);
                pixels++;
            }
        }
        if (maxx < minx) return false;
        var strip = new Box(minx, y0, maxx - minx + 1, y1 - y0 + 1);
        var candidate = new Region(strip, pixels, strip.W / (double)Math.Max(1, strip.H), strip.X + strip.W / 2.0, strip.Y + strip.H / 2.0);
        if (!IsTitleBarCandidate(f, candidate)) return false;
        result = candidate;
        return true;
    }

    /// <summary>逐行（隔一行）找足够长的白色连续段，当作一条固定高度的标题条；重复的只留最宽的。</summary>
    private static List<Region> FindTitleBarsByRuns(Frame f, byte[] bright)
    {
        var minRun = ScaledPx(f, 269);
        var barHeight = ScaledPx(f, 24);
        var candidates = new List<Region>();
        for (var y = ScaledPx(f, 40); y < Round(f.H * 0.86); y += 2)
        {
            var start = -1;
            for (var x = 0; x <= f.W; x++)
            {
                var on = x < f.W && bright[y * f.W + x] != 0;
                if (on && start < 0) start = x;
                else if (!on && start >= 0)
                {
                    if (x - start >= minRun)
                    {
                        var rect = ClampRect(new Box(start, y - ScaledPx(f, 5), x - start, barHeight), f.W, f.H);
                        var candidate = new Region(rect, rect.W * rect.H, rect.W / (double)Math.Max(1, rect.H), rect.X + rect.W / 2.0, rect.Y + rect.H / 2.0);
                        if (IsTitleBarCandidate(f, candidate)) candidates.Add(candidate);
                    }
                    start = -1;
                }
            }
        }
        // 原版用 std::sort（不稳定）；这里用稳定排序，等宽时保持扫描顺序
        var sorted = candidates.OrderByDescending(c => c.Rect.W).ToList();
        var bars = new List<Region>();
        foreach (var candidate in sorted)
        {
            var duplicate = false;
            foreach (var kept in bars)
            {
                var overlap = Math.Min(candidate.Rect.Right, kept.Rect.Right) - Math.Max(candidate.Rect.X, kept.Rect.X);
                if (Math.Abs(candidate.Cy - kept.Cy) <= ScaledPx(f, 24) && overlap > Math.Min(candidate.Rect.W, kept.Rect.W) * 0.45)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate) bars.Add(candidate);
        }
        return bars;
    }

    /// <summary>
    /// detectMinigame：三条标题条——右上的「目标」、左边略低的「碎片」、目标正下方的「信号」——
    /// 按位置和宽度配对，再推出整个面板，面板大小比例合理才算是这个小游戏。
    /// </summary>
    private static RoiInfo DetectMinigame(Frame f)
    {
        var info = new RoiInfo();
        var bright = UiWhiteMask(f);
        BridgeHorizontalGaps(bright, f.W, f.H, ScaledPx(f, 18));
        var k = OddKernel(f.H, f.W, 0.0028);
        bright = CloseMask(bright, f.W, f.H, k);
        var regs = ConnectedRegions(bright, f.W, f.H, ScaledArea(f, 1659));

        var bars = FindTitleBarsByRuns(f, bright);
        foreach (var rg in regs)
        {
            if (IsTitleBarCandidate(f, rg))
            {
                bars.Add(rg);
                continue;
            }
            if (ExtractTitleStripFromTallRegion(f, bright, rg, out var strip)) bars.Add(strip);
        }

        static bool CloseEnough(double a, double b, double limit) => Math.Abs(a - b) <= limit;

        var bestScore = -1000000000;
        var bestBars = new TitleBars();
        var bestPanel = new Box();
        for (var ti = 0; ti < bars.Count; ti++)
        {
            var target = bars[ti];
            if (!(target.Rect.W > ScaledPx(f, 461) && target.Rect.W < ScaledPx(f, 1056))) continue;
            if (!(target.Cy > f.H * 0.05 && target.Cy < f.H * 0.20)) continue;
            for (var ci = 0; ci < bars.Count; ci++)
            {
                if (ci == ti) continue;
                var components = bars[ci];
                var componentWidthRatio = components.Rect.W / (double)target.Rect.W;
                if (!(target.Cx - components.Cx > target.Rect.W * 0.70 && componentWidthRatio > 0.45 && componentWidthRatio < 1.05)) continue;
                if (!(components.Cy > target.Cy + f.H * 0.07 && components.Cy < target.Cy + f.H * 0.23)) continue;
                for (var si = 0; si < bars.Count; si++)
                {
                    if (si == ti || si == ci) continue;
                    var signals = bars[si];
                    if (!(signals.Cy > components.Cy + f.H * 0.35 && signals.Cy < f.H * 0.84)) continue;
                    var pairedWidth = (target.Rect.W + signals.Rect.W) * 0.5;
                    if (!CloseEnough(signals.Cx, target.Cx, pairedWidth * 0.12)) continue;
                    if (!CloseEnough(signals.Rect.W, target.Rect.W, pairedWidth * 0.15)) continue;
                    if (!CloseEnough(signals.Rect.X, target.Rect.X, pairedWidth * 0.12)) continue;
                    if (!CloseEnough(signals.Rect.Right, target.Rect.Right, pairedWidth * 0.12)) continue;
                    double rightPaneLeft = Math.Min(target.Rect.X, signals.Rect.X);
                    if (components.Rect.Right > rightPaneLeft + pairedWidth * 0.03) continue;

                    var left = components.Rect.X - ScaledPx(f, 36);
                    var top = target.Rect.Y - ScaledPx(f, 14);
                    var panelRight = Math.Max(target.Rect.Right, signals.Rect.Right) + ScaledPx(f, 36);
                    var panelBottom = signals.Rect.Bottom + ScaledPx(f, 170);
                    var panel = ClampRect(new Box(left, top, panelRight - left, panelBottom - top), f.W, f.H);
                    var panelAsp = panel.W / (double)Math.Max(1, panel.H);
                    if (!(panel.W > ScaledPx(f, 864) && panel.H > f.H * 0.45 && panelAsp > 1.0 && panelAsp < 2.0)) continue;

                    var score = target.Pixels + components.Pixels + signals.Pixels;
                    score -= Round(Math.Abs(signals.Cx - target.Cx) * 2.0);
                    score -= Round(Math.Abs(target.Cx - components.Cx - ScaledPx(f, 595)) * 1.5);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestBars.Target = target.Rect;
                        bestBars.Components = components.Rect;
                        bestBars.Signals = signals.Rect;
                        bestBars.HasTarget = bestBars.HasComponents = bestBars.HasSignals = true;
                        bestPanel = panel;
                    }
                }
            }
        }
        if (bestScore <= 0) return info;
        info.Bars = bestBars;
        info.Panel = bestPanel;
        info.IsMinigame = info.Bars.HasTarget && info.Bars.HasComponents && info.Bars.HasSignals;
        return info;
    }

    /// <summary>沿用缓存的版面前确认：窗口没变，三条标题条所在位置仍有至少 2% 的白色采样点。</summary>
    private static bool ValidateMinigameGeometry(Frame f, Geometry geometry)
    {
        if (!geometry.Valid || geometry.WindowGeneration != f.Source.WindowGeneration || geometry.FrameW != f.W || geometry.FrameH != f.H) return false;
        bool HasWhiteUi(Box rect)
        {
            rect = ClampRect(rect, f.W, f.H);
            if (rect.W <= 0 || rect.H <= 0) return false;
            var step = Math.Max(1, Math.Min(rect.W, rect.H) / 12);
            int white = 0, samples = 0;
            for (var y = rect.Y; y < rect.Bottom; y += step)
                for (var x = rect.X; x < rect.Right; x += step)
                {
                    f.Rgb(y * f.W + x, out var r, out var g, out var b);
                    white += IsFlashingStyleWhite(r, g, b) ? 1 : 0;
                    samples++;
                }
            return samples > 0 && white * 100 >= samples * 2;
        }
        return HasWhiteUi(geometry.Roi.Bars.Target) && HasWhiteUi(geometry.Roi.Bars.Components) && HasWhiteUi(geometry.Roi.Bars.Signals);
    }

    // ───────── 碎片方框 ─────────

    /// <summary>投影曲线先做 5 点平滑，取不低于最大值 thresholdRatio 的局部极大，彼此至少隔 minDist。</summary>
    private static List<int> EdgeLinePeaks(int[] projection, int @base, int minDist, double thresholdRatio)
    {
        if (projection.Length == 0) return [];
        var smooth = new int[projection.Length];
        for (var i = 0; i < projection.Length; i++)
        {
            int sum = 0, count = 0;
            for (var d = -2; d <= 2; d++)
            {
                var j = i + d;
                if (0 <= j && j < projection.Length) { sum += projection[j]; count++; }
            }
            smooth[i] = sum / Math.Max(1, count);
        }
        var maxVal = smooth.Max();
        var threshold = Round(maxVal * thresholdRatio);
        var candidates = new List<(int score, int idx)>();
        for (var i = 1; i + 1 < smooth.Length; i++)
        {
            if (smooth[i] < threshold) continue;
            if (smooth[i] >= smooth[i - 1] && smooth[i] >= smooth[i + 1]) candidates.Add((smooth[i], i));
        }
        // 原版 std::sort 不稳定；这里稳定排序，同分时靠前的优先
        var chosen = new List<int>();
        foreach (var (_, idx) in candidates.OrderByDescending(c => c.score))
        {
            var isNear = false;
            foreach (var c in chosen)
                if (Math.Abs(c - idx) < minDist) { isNear = true; break; }
            if (!isNear) chosen.Add(idx);
        }
        chosen.Sort();
        for (var i = 0; i < chosen.Count; i++) chosen[i] += @base;
        return chosen;
    }

    /// <summary>从边线位置里挑出 pairCount 对（左右边或上下边），边长和间距最接近预期的组合（穷举 + 剪枝）。</summary>
    private static List<(int a, int b)> SelectGridBorderPairs(List<int> peaks, int pairCount, int minSide, int maxSide, double expectedSide, double expectedGap)
    {
        var best = new List<(int a, int b)>();
        var current = new List<(int a, int b)>();
        var bestCost = 1e100;
        var minGap = Math.Max(1, Round(expectedGap * 0.25));
        var maxGap = Math.Max(minGap, Round(expectedGap * 2.5));

        void Search(int firstIndex, double cost)
        {
            if (current.Count == pairCount)
            {
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = [.. current];
                }
                return;
            }
            var pairsLeft = pairCount - current.Count;
            if (peaks.Count - firstIndex < pairsLeft * 2) return;
            for (var leftIndex = firstIndex; leftIndex + 1 < peaks.Count; leftIndex++)
            {
                if (current.Count > 0)
                {
                    var gap = peaks[leftIndex] - current[^1].b;
                    if (gap < minGap) continue;
                    if (gap > maxGap) break;
                }
                for (var rightIndex = leftIndex + 1; rightIndex < peaks.Count; rightIndex++)
                {
                    var side = peaks[rightIndex] - peaks[leftIndex];
                    if (side < minSide) continue;
                    if (side > maxSide) break;
                    var nextCost = cost + Math.Pow((side - expectedSide) / expectedSide, 2.0);
                    if (current.Count > 0)
                    {
                        var gap = peaks[leftIndex] - current[^1].b;
                        nextCost += Math.Pow((gap - expectedGap) / expectedGap, 2.0);
                    }
                    if (nextCost >= bestCost) continue;
                    current.Add((peaks[leftIndex], peaks[rightIndex]));
                    Search(rightIndex + 1, nextCost);
                    current.RemoveAt(current.Count - 1);
                }
            }
        }

        Search(0, 0.0);
        return best;
    }

    /// <summary>detectComponentBoxesByBorder：碎片标题条下方，灰度梯度的列 / 行投影里找 2 列 × 4 行方框的边线。</summary>
    private static bool DetectComponentBoxesByBorder(Frame f, RoiInfo roi, out Box[] components)
    {
        components = [];
        var cb = roi.Bars.Components;
        var p = roi.Panel;
        var left = cb.X + Round(cb.W * 0.08);
        var right = cb.X + Round(cb.W * 0.86);
        var top = cb.Bottom + Round(cb.H * 0.25);
        var bottom = p.Y + Round(p.H * 0.84);
        var search = ClampRect(new Box(left, top, right - left, bottom - top), f.W, f.H);
        if (search.W <= 0 || search.H <= 0) return false;

        int[] vertical = new int[search.W], horizontal = new int[search.H];
        for (var y = search.Y; y < search.Bottom; y++)
        {
            var row = y * f.W;
            for (var x = search.X + 1; x < search.Right; x++)
                vertical[x - search.X] += Math.Abs(f.Gray[row + x] - f.Gray[row + x - 1]);
        }
        for (var y = search.Y + 1; y < search.Bottom; y++)
        {
            int row = y * f.W, prev = (y - 1) * f.W;
            for (var x = search.X; x < search.Right; x++)
                horizontal[y - search.Y] += Math.Abs(f.Gray[row + x] - f.Gray[prev + x]);
        }

        var minDist = ScaledPx(f, 10);
        var xs = EdgeLinePeaks(vertical, search.X, minDist, 0.45);
        var ys = EdgeLinePeaks(horizontal, search.Y, minDist, 0.45);

        var minSide = Math.Max(ScaledPx(f, 45), Round(cb.W * 0.16));
        var maxSide = Math.Max(minSide + 1, Round(cb.W * 0.36));
        var colPairs = SelectGridBorderPairs(xs, 2, minSide, maxSide, cb.W * 0.255, cb.W * 0.05);
        if (colPairs.Count != 2) return false;

        var avgW = 0.0;
        foreach (var (a, b) in colPairs) avgW += b - a;
        avgW /= colPairs.Count;

        var rowPairs = SelectGridBorderPairs(ys, 4, Round(avgW * 0.72), Round(avgW * 1.35), avgW, avgW * 0.20);
        if (rowPairs.Count != 4) return false;

        var list = new List<Box>(8);
        foreach (var (y0, y1) in rowPairs)
            foreach (var (x0, x1) in colPairs)
                list.Add(ClampRect(new Box(x0, y0, x1 - x0 + 1, y1 - y0 + 1), f.W, f.H));
        components = [.. list];
        return components.Length == 8;
    }

    /// <summary>detectRois：目标指纹区域按标题条比例推出；8 个碎片方框靠边线找。</summary>
    private static bool DetectRois(Frame f, RoiInfo roi, out Box target, out Box[] components)
    {
        Box tb = roi.Bars.Target, sb = roi.Bars.Signals;
        var targetTop = tb.Bottom + Round(tb.H * 0.35);
        var targetBottom = sb.Y - Round(tb.H * 0.55);
        var targetLeft = tb.X + Round(tb.W * 0.12);
        var targetRight = tb.X + Round(tb.W * 0.72);
        target = ClampRect(new Box(targetLeft, targetTop, targetRight - targetLeft, targetBottom - targetTop), f.W, f.H);
        if (!DetectComponentBoxesByBorder(f, roi, out components)) return false;
        return target.W > 0 && target.H > 0 && components.Length == 8;
    }

    // ───────── 关卡标记 ─────────

    /// <summary>
    /// detectLevelMarker：信号标题条下方一条短亮线，按它的中心落在 4 个格位中的哪一格返回 0–3，找不到返回 -1。
    /// markerLine 收到那条线（高 1 像素）。
    /// </summary>
    private static int DetectLevelMarker(Frame f, Box signalsBar, out Box markerLine)
    {
        markerLine = default;
        signalsBar = ClampRect(signalsBar, f.W, f.H);
        if (signalsBar.W <= 0 || signalsBar.H <= 0) return -1;

        var yStart = Math.Min(f.H, signalsBar.Bottom);
        var yEnd = Math.Min(f.H, signalsBar.Y + signalsBar.H * 2);
        var minRun = Math.Max(ScaledPx(f, 42), Round(signalsBar.W * 0.12));
        var maxRun = Math.Max(minRun + 1, Round(signalsBar.W * 0.27));
        var maxGap = ScaledPx(f, 3);
        int bestLeft = -1, bestRight = -1, bestY = -1;

        bool LinePixel(int x, int y)
        {
            f.Rgb(y * f.W + x, out var red, out var green, out var blue);
            var luma = (77 * red + 150 * green + 29 * blue) >> 8;
            var high = Math.Max(red, Math.Max(green, blue));
            var low = Math.Min(red, Math.Min(green, blue));
            return luma >= 135 && high - low <= 120;
        }

        for (var y = yStart; y < yEnd; y++)
        {
            int runStart = -1, lastOn = -1;
            for (var x = signalsBar.X; x <= signalsBar.Right; x++)
            {
                var on = x < signalsBar.Right && LinePixel(x, y);
                if (on)
                {
                    if (runStart < 0) runStart = x;
                    lastOn = x;
                }
                var gapEnded = runStart >= 0 && !on && x - lastOn > maxGap;
                var rowEnded = x == signalsBar.Right;
                if (!gapEnded && !rowEnded) continue;
                var runWidth = lastOn - runStart + 1;
                if (runWidth >= minRun && runWidth <= maxRun && runWidth > bestRight - bestLeft + 1)
                {
                    bestLeft = runStart;
                    bestRight = lastOn;
                    bestY = y;
                }
                runStart = -1;
                lastOn = -1;
            }
        }
        if (bestLeft < 0) return -1;

        var center = (bestLeft + bestRight) * 0.5;
        var firstCenter = signalsBar.X + signalsBar.W * 0.20;
        var spacing = signalsBar.W * 0.195;
        var marker = Round((center - firstCenter) / spacing);
        if (marker < 0 || marker >= 4) return -1;
        markerLine = new Box(bestLeft, bestY, bestRight - bestLeft + 1, 1);
        return marker;
    }

    // ───────── 指纹匹配 ─────────

    private sealed class GrayPatch(int w, int h)
    {
        public readonly int W = Math.Max(1, w), H = Math.Max(1, h);
        public readonly byte[] Pixels = new byte[Math.Max(1, w) * Math.Max(1, h)];
    }

    /// <summary>按 divisor 倍缩小（块平均）。</summary>
    private static GrayPatch DownsampleGray(Frame f, Box r, int divisor)
    {
        r = ClampRect(r, f.W, f.H);
        var output = new GrayPatch(r.W / divisor, r.H / divisor);
        for (var y = 0; y < output.H; y++)
        {
            var sy0 = r.Y + y * r.H / output.H;
            var sy1 = r.Y + (y + 1) * r.H / output.H;
            for (var x = 0; x < output.W; x++)
            {
                var sx0 = r.X + x * r.W / output.W;
                var sx1 = r.X + (x + 1) * r.W / output.W;
                int sum = 0, count = 0;
                for (var sy = sy0; sy < sy1; sy++)
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        sum += f.Gray[sy * f.W + sx];
                        count++;
                    }
                output.Pixels[y * output.W + x] = (byte)(sum / Math.Max(1, count));
            }
        }
        return output;
    }

    private static GrayPatch ResizeGrayBilinear(GrayPatch src, int w, int h)
    {
        var output = new GrayPatch(w, h);
        for (var y = 0; y < output.H; y++)
        {
            var sourceY = Math.Clamp((y + 0.5) * src.H / output.H - 0.5, 0.0, src.H - 1);
            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(src.H - 1, y0 + 1);
            var fy = sourceY - y0;
            for (var x = 0; x < output.W; x++)
            {
                var sourceX = Math.Clamp((x + 0.5) * src.W / output.W - 0.5, 0.0, src.W - 1);
                var x0 = (int)Math.Floor(sourceX);
                var x1 = Math.Min(src.W - 1, x0 + 1);
                var fx = sourceX - x0;
                var topValue = src.Pixels[y0 * src.W + x0] * (1.0 - fx) + src.Pixels[y0 * src.W + x1] * fx;
                var bottomValue = src.Pixels[y1 * src.W + x0] * (1.0 - fx) + src.Pixels[y1 * src.W + x1] * fx;
                output.Pixels[y * output.W + x] = (byte)Math.Clamp(Round(topValue * (1.0 - fy) + bottomValue * fy), 0, 255);
            }
        }
        return output;
    }

    /// <summary>碎片在目标上逐位置滑动，取零均值归一化互相关的最大值；碎片不比目标小或没有纹理时返回 -2。</summary>
    private static double MatchGrayZncc(GrayPatch target, GrayPatch component)
    {
        const double invalidScore = -2.0;
        if (component.W >= target.W || component.H >= target.H) return invalidScore;
        long componentSum = 0, componentSquared = 0;
        var samples = 0;
        for (var y = 0; y < component.H; y++)
            for (var x = 0; x < component.W; x++)
            {
                int value = component.Pixels[y * component.W + x];
                componentSum += value;
                componentSquared += value * value;
                samples++;
            }
        var componentVariance = componentSquared - (double)componentSum * componentSum / Math.Max(1, samples);
        if (componentVariance <= 1e-6) return invalidScore;

        var best = invalidScore;
        var tp = target.Pixels;
        var cp = component.Pixels;
        for (var y0 = 0; y0 <= target.H - component.H; y0++)
            for (var x0 = 0; x0 <= target.W - component.W; x0++)
            {
                long targetSum = 0, targetSquared = 0, cross = 0;
                for (var y = 0; y < component.H; y++)
                {
                    var targetRow = (y0 + y) * target.W + x0;
                    var componentRow = y * component.W;
                    for (var x = 0; x < component.W; x++)
                    {
                        int tv = tp[targetRow + x];
                        int cv = cp[componentRow + x];
                        targetSum += tv;
                        targetSquared += tv * tv;
                        cross += tv * cv;
                    }
                }
                var targetVariance = targetSquared - (double)targetSum * targetSum / samples;
                if (targetVariance <= 1e-6) continue;
                var covariance = cross - (double)targetSum * componentSum / samples;
                best = Math.Max(best, covariance / Math.Sqrt(targetVariance * componentVariance));
            }
        return best;
    }

    /// <summary>
    /// answerScores：每个碎片和目标指纹的相似度。8 个碎片出自同一张图、缩放倍数相同，
    /// 所以按倍数（1.0 / 1.15 / 1.3 / 1.45）统一试，取「前 4 名得分之和」最高的那个倍数下的得分；
    /// 若允许每个碎片各选倍数，干扰项容易凑巧模仿出目标的纹路。
    /// </summary>
    private static double[] AnswerScores(Frame f, Box target, Box[] components)
    {
        var targetInset = Math.Max(1, Round(Math.Min(target.W, target.H) * 0.02));
        target = ClampRect(new Box(target.X + targetInset, target.Y + targetInset, target.W - targetInset * 2, target.H - targetInset * 2), f.W, f.H);
        const int down = 4;
        var targetPatch = DownsampleGray(f, target, down);
        var componentPatches = new List<GrayPatch>(components.Length);
        foreach (var c in components)
        {
            var inset = Math.Max(1, Round(Math.Min(c.W, c.H) * 0.05));
            var cr = ClampRect(new Box(c.X + inset, c.Y + inset, c.W - inset * 2, c.H - inset * 2), f.W, f.H);
            componentPatches.Add(DownsampleGray(f, cr, down));
        }

        double[] scales = [1.0, 1.15, 1.3, 1.45];
        var scores = Enumerable.Repeat(-2.0, componentPatches.Count).ToArray();
        var bestScaleScore = -1e100;
        foreach (var scale in scales)
        {
            var scaleScores = new double[componentPatches.Count];
            for (var i = 0; i < componentPatches.Count; i++)
            {
                var component = componentPatches[i];
                var sw = Math.Max(2, Round(component.W * scale));
                var sh = Math.Max(2, Round(component.H * scale));
                scaleScores[i] = MatchGrayZncc(targetPatch, ResizeGrayBilinear(component, sw, sh));
            }
            var scaleScore = scaleScores.OrderByDescending(s => s).Take(4).Sum();
            if (scaleScore > bestScaleScore)
            {
                bestScaleScore = scaleScore;
                scores = scaleScores;
            }
        }
        return scores;
    }

    /// <summary>目标指纹的 8×8 差值哈希（按梯度能量）：相邻格比大小，不受伽马和曝光的单调变化影响。用来判断换没换题。</summary>
    private static ulong TargetFingerprintHash(Frame f, Box target)
    {
        var inset = Math.Max(1, Round(Math.Min(target.W, target.H) * 0.02));
        target = ClampRect(new Box(target.X + inset, target.Y + inset, target.W - inset * 2, target.H - inset * 2), f.W, f.H);
        ulong hash = 0;
        Span<int> values = stackalloc int[9];
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 9; col++)
            {
                var energy = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    var y = target.Y + Math.Min(target.H - 1, (row * 4 + sy) * target.H / 32);
                    for (var sx = 0; sx < 4; sx++)
                    {
                        var x = target.X + Math.Min(target.W - 1, (col * 4 + sx) * target.W / 36);
                        energy += GradientAt(f, x, y);
                    }
                }
                values[col] = energy;
            }
            for (var col = 0; col < 8; col++)
            {
                hash <<= 1;
                hash |= values[col] > values[col + 1] ? 1ul : 0ul;
            }
        }
        return hash;
    }

    private static bool SameTargetFingerprint(ulong a, ulong b) => BitOperations.PopCount(a ^ b) <= 6;

    /// <summary>以梯度为权重的平均灰度：只看纹路本身有多亮，不受底色影响。</summary>
    private static int EdgeWeightedGray(Frame f, Box r)
    {
        r = ClampRect(r, f.W, f.H);
        long weightedGray = 0, totalWeight = 0;
        for (var y = r.Y; y < r.Bottom; y++)
            for (var x = r.X; x < r.Right; x++)
            {
                var weight = GradientAt(f, x, y);
                weightedGray += (long)f.Gray[y * f.W + x] * weight;
                totalWeight += weight;
            }
        return totalWeight > 0 ? (int)(weightedGray / totalWeight) : 0;
    }

    /// <summary>
    /// markStates：读出每个碎片是否已选中、光标在哪。
    /// 选中的碎片纹路更亮，但绝对亮度随 HDR 色调映射变化，所以把 8 个值分成两组，两组分得足够开才认为有选中的；
    /// 光标是方框外四角的括号，四个角上梯度能量明显最高（比第二名高 8%、比中位数高 15%）的那个就是光标。
    /// </summary>
    private static void MarkStates(Frame f, BlockInfo[] blocks)
    {
        var selectedScores = new List<(int score, int block)>(blocks.Length);
        int bestCursor = -1, bestScore = -1, secondScore = -1;
        var cursorScores = new List<int>();
        for (var i = 0; i < blocks.Length; i++)
        {
            ref var b = ref blocks[i];
            var inset = Math.Max(1, Round(Math.Min(b.Rect.W, b.Rect.H) * 0.08));
            var selectedRect = ClampRect(new Box(b.Rect.X + inset, b.Rect.Y + inset, b.Rect.W - inset * 2, b.Rect.H - inset * 2), f.W, f.H);
            selectedScores.Add((EdgeWeightedGray(f, selectedRect), b.Index));
            b.Selected = false;

            var cursorRect = PadRect(b.Rect, f.W, f.H, 0.12);
            var len = Math.Max(ScaledPx(f, 10), Round(Math.Min(cursorRect.W, cursorRect.H) * 0.22));
            var band = Math.Max(ScaledPx(f, 3), Round(Math.Min(cursorRect.W, cursorRect.H) * 0.045));
            int At(int x, int y) => GradientAt(f, cursorRect.X + x, cursorRect.Y + y);
            var score = 0;
            for (var y = 0; y < band; y++) for (var x = 0; x < len; x++) score += At(x, y) + At(cursorRect.W - 1 - x, y);
            for (var y = 0; y < len; y++) for (var x = 0; x < band; x++) score += At(x, y) + At(cursorRect.W - 1 - x, y);
            for (var y = 0; y < band; y++) for (var x = 0; x < len; x++) score += At(x, cursorRect.H - 1 - y) + At(cursorRect.W - 1 - x, cursorRect.H - 1 - y);
            for (var y = 0; y < len; y++) for (var x = 0; x < band; x++) score += At(x, cursorRect.H - 1 - y) + At(cursorRect.W - 1 - x, cursorRect.H - 1 - y);
            cursorScores.Add(score);
            b.CursorScore = score;
            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                bestCursor = b.Index;
            }
            else if (score > secondScore) secondScore = score;
        }

        // 原版 std::sort 不稳定；这里稳定排序
        var sortedScores = selectedScores.OrderBy(s => s.score).ToList();
        var bestSeparation = 0.0;
        var bestSplit = -1;
        for (var split = 1; split < sortedScores.Count; split++)
        {
            double lowMean = 0, highMean = 0;
            for (var i = 0; i < split; i++) lowMean += sortedScores[i].score;
            for (var i = split; i < sortedScores.Count; i++) highMean += sortedScores[i].score;
            lowMean /= split;
            highMean /= sortedScores.Count - split;
            double lowVar = 0, highVar = 0;
            for (var i = 0; i < split; i++) lowVar += Math.Pow(sortedScores[i].score - lowMean, 2.0);
            for (var i = split; i < sortedScores.Count; i++) highVar += Math.Pow(sortedScores[i].score - highMean, 2.0);
            lowVar /= split;
            highVar /= sortedScores.Count - split;
            var separation = Math.Pow(highMean - lowMean, 2.0) / (lowVar + highVar + 1.0);
            if (separation > bestSeparation)
            {
                bestSeparation = separation;
                bestSplit = split;
            }
        }
        if (bestSplit > 0 && bestSeparation >= 9.0)
            for (var i = bestSplit; i < sortedScores.Count; i++)
                for (var j = 0; j < blocks.Length; j++)
                    if (blocks[j].Index == sortedScores[i].block) blocks[j].Selected = true;

        cursorScores.Sort();
        var medianCursor = cursorScores.Count == 0 ? 0 : cursorScores[cursorScores.Count / 2];
        var cursorSeparated = bestCursor > 0 && bestScore > 0 &&
                              (long)bestScore * 100 >= (long)Math.Max(0, secondScore) * 108 &&
                              (long)bestScore * 100 >= (long)medianCursor * 115;
        if (cursorSeparated)
            for (var j = 0; j < blocks.Length; j++) blocks[j].Cursor = blocks[j].Index == bestCursor;
    }

    // ───────── 自动输入 ─────────

    private void ResetAutomation(ref AutomationState aut)
    {
        host.CancelAll();
        aut = new AutomationState();
    }

    private static bool AllBlocksCorrectlySelected(OverlayState state)
    {
        if (!state.Visible || state.Blocks.Length != 8) return false;
        foreach (var b in state.Blocks)
            if (b.Selected != b.Correct) return false;
        return true;
    }

    private static int CursorIndex(OverlayState state)
    {
        foreach (var b in state.Blocks)
            if (b.Cursor) return b.Index;
        return -1;
    }

    /// <summary>光标从 cur 走到 target（编号 1–8，两列）：先左右，再上下。</summary>
    private static void AppendCursorMoves(ref int cur, int target, List<HackKey> keys)
    {
        int curRow = (cur - 1) / 2, curCol = (cur - 1) % 2;
        int targetRow = (target - 1) / 2, targetCol = (target - 1) % 2;
        while (curCol < targetCol) { keys.Add(new HackKey(VkRight)); curCol++; }
        while (curCol > targetCol) { keys.Add(new HackKey(VkLeft)); curCol--; }
        while (curRow < targetRow) { keys.Add(new HackKey(VkDown)); curRow++; }
        while (curRow > targetRow) { keys.Add(new HackKey(VkUp)); curRow--; }
        cur = target;
    }

    /// <summary>
    /// planAndRunAutomation：选中状态和答案不一致就排一串「移动 + 回车」，执行完等 120 毫秒再读画面核对；
    /// 全部一致时长按 Tab 提交。原版按键都不检查前台窗口。
    /// </summary>
    private void PlanAndRunAutomation(OverlayState state, ref AutomationState aut, int levelMarker)
    {
        if (!state.Visible)
        {
            ResetAutomation(ref aut);
            return;
        }
        if (aut.PlannedHash != 0 && !SameTargetFingerprint(aut.PlannedHash, state.TargetHash)) ResetAutomation(ref aut);

        if (aut.Phase == AutomationPhase.Selecting && aut.InputJob is not null)
        {
            if (aut.InputJob.Pending) return;
            if (!aut.InputJob.Succeeded)
            {
                ResetAutomation(ref aut);
                return;
            }
            aut.InputJob = null;
            aut.Phase = AutomationPhase.VerifyDelay;
            aut.VerifyAfter = Now + Ms(120);
            return;
        }
        if (aut.Phase == AutomationPhase.VerifyDelay && Now < aut.VerifyAfter) return;
        if (aut.Phase is AutomationPhase.Submitting or AutomationPhase.WaitingLevel) return;

        if (AllBlocksCorrectlySelected(state))
        {
            aut.InputJob = host.QueueSequence([new HackKey(VkTab, LongPress: true)], 0);
            aut.Phase = AutomationPhase.Submitting;
            aut.PlannedHash = state.TargetHash;
            aut.PlannedLevelMarker = levelMarker;
            return;
        }

        var cur = CursorIndex(state);
        if (cur < 1 || state.Blocks.Length != 8) return;

        bool[] selected = new bool[9], correct = new bool[9];
        foreach (var b in state.Blocks)
            if (b.Index is >= 1 and <= 8)
            {
                selected[b.Index] = b.Selected;
                correct[b.Index] = b.Correct;
            }
        var keys = new List<HackKey>();
        for (var target = 1; target <= 8; target++)
        {
            if (selected[target] == correct[target]) continue;
            AppendCursorMoves(ref cur, target, keys);
            keys.Add(new HackKey(VkEnter));
            selected[target] = !selected[target];
        }
        aut.InputJob = host.QueueSequence(keys, 0);
        aut.Phase = AutomationPhase.Selecting;
        aut.PlannedHash = state.TargetHash;
        aut.PlannedLevelMarker = levelMarker;
    }

    // ───────── 每帧分析 ─────────

    /// <summary>analyzeFrame：定位（或沿用缓存的版面）→ 目标哈希 → 必要时重新算答案 → 读关卡标记和选中 / 光标状态。</summary>
    private static OverlayState AnalyzeFrame(Frame f, ref SolverCache cache, ref Geometry geometry, out bool minigame)
    {
        var os = new OverlayState();
        var geometryHit = ValidateMinigameGeometry(f, geometry);
        var roi = geometryHit ? geometry.Roi : DetectMinigame(f);
        minigame = roi.IsMinigame;
        if (!roi.IsMinigame)
        {
            geometry = Geometry.Invalid;
            return os;
        }

        var target = geometryHit ? geometry.Target : default;
        var comps = geometryHit ? geometry.Components : [];
        if (!geometryHit && !DetectRois(f, roi, out target, out comps)) return os;
        if (!geometryHit)
        {
            geometry = new Geometry
            {
                Valid = true, WindowGeneration = f.Source.WindowGeneration, FrameW = f.W, FrameH = f.H,
                Roi = roi, Target = target, Components = comps,
            };
        }

        var targetHash = TargetFingerprintHash(f, target);
        var cacheHit = cache.Valid && SameTargetFingerprint(cache.TargetHash, targetHash) &&
                       cache.BaseBlocks.Length == comps.Length && cache.Target == target;
        if (!cacheHit)
        {
            var scores = AnswerScores(f, target, comps);
            var validScores = scores.Count(score => score > -1.5);
            if (validScores < 4) return os;
            var order = Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]).ToArray();
            var top = order.Take(Math.Min(4, order.Length)).ToHashSet();
            cache = new SolverCache { Valid = true, TargetHash = targetHash, Target = target, BaseBlocks = new BlockInfo[comps.Length] };
            for (var i = 0; i < comps.Length; i++)
                cache.BaseBlocks[i] = new BlockInfo { Index = i + 1, Rect = comps[i], Score = scores[i], Correct = top.Contains(i) };
        }

        os.Visible = true;
        os.TargetHash = targetHash;
        os.Target = target;
        os.Blocks = (BlockInfo[])cache.BaseBlocks.Clone();
        os.LevelMarker = DetectLevelMarker(f, roi.Bars.Signals, out os.LevelMarkerLine);
        MarkStates(f, os.Blocks);
        return os;
    }

    // ───────── 叠加层 ─────────

    private const uint OverlayGreen = 0xFF00FF00, OverlayRed = 0xFFFF3232, OverlayMagenta = 0xFFFF00FF;
    private const uint OverlayYellow = 0xFFFFE600, OverlayPink = 0xFFFF46BE;

    /// <summary>
    /// drawOverlay 的移植：目标区域洋红框；碎片框绿色是答案、红色不是；光标所在碎片外侧一个粉点，
    /// 已选中的碎片外侧一个黄点（左列画在左边，右列画在右边）；关卡标记线下方一个朝下的绿色实心三角。
    /// 线宽和点的大小都按碎片框的屏幕宽度换算，和原版一致。
    /// </summary>
    private static HackScene BuildScene(HackFrame frame, OverlayState state)
    {
        var scene = new HackScene();
        if (!state.Visible) return scene;
        var target = ScaleRectToScreen(frame, state.Target);
        var blocks = state.Blocks.Select(b => (b.Index, Rect: ScaleRectToScreen(frame, b.Rect), b.Correct, b.Selected, b.Cursor)).ToArray();

        var refBlock = blocks.Length == 0 ? Math.Max(1, target.W / 3) : Math.Max(1, blocks[0].Rect.W);
        var blockPen = Math.Max(1, Round(refBlock / 60.0));
        var dot = Math.Max(4, Round(refBlock * 0.10));
        var dotMargin = Math.Max(2, Round(refBlock * 0.05));

        scene.Rect(target.X, target.Y, target.W, target.H, OverlayMagenta, blockPen);
        foreach (var b in blocks)
        {
            int x = b.Rect.X, y = b.Rect.Y;
            var pen = b.Correct ? OverlayGreen : OverlayRed;
            scene.Rect(x, y, b.Rect.W, b.Rect.H, pen, blockPen);
            var leftColumn = b.Index % 2 == 1;
            var markerTop = y + (b.Rect.H - dot) / 2;
            void Dot(int gap, uint fill)
            {
                var markerLeft = leftColumn ? x - gap - dot : x + b.Rect.W + gap;
                // GDI 的 Ellipse 用当前画刷填充、当前画笔（碎片框的颜色）描边
                scene.FillEllipse(markerLeft + dot / 2f, markerTop + dot / 2f, dot / 2f, dot / 2f, fill);
                scene.Ellipse(markerLeft + dot / 2f, markerTop + dot / 2f, dot / 2f, dot / 2f, pen, blockPen);
            }
            if (b.Cursor)
            {
                var markerGap = Math.Max(dot, dotMargin * 2);
                if (b.Selected) markerGap += dot + dotMargin;
                Dot(markerGap, OverlayPink);
            }
            if (b.Selected) Dot(Math.Max(dot, dotMargin * 2), OverlayYellow);
        }

        if (state.LevelMarker >= 0)
        {
            var line = ScaleRectToScreen(frame, state.LevelMarkerLine);
            if (line.W > 0)
            {
                var centerX = line.X + line.W / 2;
                var markerWidth = Math.Max(10, Round(line.W * 0.16));
                var markerHeight = Math.Max(7, Round(markerWidth * 0.65));
                var markerTop = line.Y + line.H + Math.Max(4, Round(markerHeight * 0.75));
                float leftX = centerX - markerWidth / 2, rightX = centerX + markerWidth / 2;
                // 没有多边形图元：用逐行的细条填满三角形，再描三条边
                for (var i = 0; i < markerHeight; i++)
                {
                    var t = (i + 0.5f) / markerHeight;
                    var half = (rightX - leftX) / 2 * (1 - t);
                    scene.FillRect(centerX - half, markerTop + i, half * 2, 1, OverlayGreen);
                }
                scene.Line(leftX, markerTop, rightX, markerTop, OverlayGreen, blockPen);
                scene.Line(rightX, markerTop, centerX, markerTop + markerHeight, OverlayGreen, blockPen);
                scene.Line(centerX, markerTop + markerHeight, leftX, markerTop, OverlayGreen, blockPen);
            }
        }
        return scene;
    }

    // ───────── 入口 ─────────

    private Frame? CaptureScreen()
    {
        var shot = host.Capture();
        if (shot is null || shot.Width < 3 || shot.Height < 3 || shot.Bgra.Length != shot.Width * shot.Height) return null;
        return new Frame(shot);
    }

    public bool DetectInGame()
    {
        var f = CaptureScreen();
        if (f is null)
        {
            _detected = Geometry.Invalid;
            return false;
        }
        var roi = DetectMinigame(f);
        if (!roi.IsMinigame || !DetectRois(f, roi, out var target, out var components))
        {
            _detected = Geometry.Invalid;
            return false;
        }
        _detected = new Geometry
        {
            Valid = true, WindowGeneration = f.Source.WindowGeneration, FrameW = f.W, FrameH = f.H,
            Roi = roi, Target = target, Components = components,
        };
        return true;
    }

    public void ResetInGameCache() => _detected = Geometry.Invalid;

    public bool RunSession(Func<bool> stopRequested, Action<string> status)
    {
        const int automationMissToleranceFrames = 3;
        const int overlayMissToleranceFrames = 8;
        var overlay = host.Overlay;
        var lastStatus = "";
        var overlayShown = false;
        var retainedUsable = false;
        var retainedScene = new HackScene();
        void SetStatus(string text)
        {
            if (text == lastStatus) return;
            lastStatus = text;
            status(text);
        }
        void PublishEmpty() => overlay.Clear();
        // syncOverlay：叠加层开关打开时补发保留的最后一帧，关掉时清空
        void SyncOverlay()
        {
            if (overlay.Enabled)
            {
                if (!overlayShown)
                {
                    if (retainedUsable) overlay.Show(retainedScene);
                    overlayShown = true;
                }
            }
            else if (overlayShown)
            {
                PublishEmpty();
                overlayShown = false;
            }
        }

        var cache = new SolverCache();
        var geometry = _detected;
        var automation = new AutomationState();
        // 原版先取游戏客户区（GetGameClientRect）摆放叠加层窗口，取不到就直接退出
        if (host.Capture() is null)
        {
            ResetInGameCache();
            return false;
        }
        SyncOverlay();
        var completedAnyLevel = false;
        int lostFrames = 0, invalidFrames = 0;
        SetStatus("fingerprint: locating");
        try
        {
            while (!stopRequested())
            {
                SyncOverlay();
                var frame = CaptureScreen();
                if (frame is null)
                {
                    geometry = Geometry.Invalid;
                    cache = new SolverCache();
                    ResetInGameCache();
                    Thread.Sleep(30);
                    continue;
                }
                if (geometry.Valid && geometry.WindowGeneration != frame.Source.WindowGeneration)
                {
                    geometry = Geometry.Invalid;
                    cache = new SolverCache();
                    ResetInGameCache();
                }

                // 已经按了 Tab：只盯着关卡标记，连续两帧挪到别的格子就算这一关过了
                var waitingForLevel = automation.Phase is AutomationPhase.Submitting or AutomationPhase.WaitingLevel;
                if (waitingForLevel && automation.PlannedLevelMarker >= 0)
                {
                    var levelMarker = -1;
                    if (!ValidateMinigameGeometry(frame, geometry))
                    {
                        var relocatedRoi = DetectMinigame(frame);
                        if (relocatedRoi.IsMinigame && DetectRois(frame, relocatedRoi, out var relocatedTarget, out var relocatedComponents))
                        {
                            geometry = new Geometry
                            {
                                Valid = true, WindowGeneration = frame.Source.WindowGeneration, FrameW = frame.W, FrameH = frame.H,
                                Roi = relocatedRoi, Target = relocatedTarget, Components = relocatedComponents,
                            };
                        }
                        else geometry = Geometry.Invalid;
                        cache = new SolverCache();
                    }
                    if (geometry.Valid) levelMarker = DetectLevelMarker(frame, geometry.Roi.Bars.Signals, out _);

                    if (levelMarker >= 0 && levelMarker != automation.PlannedLevelMarker)
                    {
                        if (automation.ObservedLevelMarker == levelMarker) automation.LevelChangeFrames++;
                        else
                        {
                            automation.ObservedLevelMarker = levelMarker;
                            automation.LevelChangeFrames = 1;
                        }
                    }
                    else
                    {
                        automation.ObservedLevelMarker = -1;
                        automation.LevelChangeFrames = 0;
                    }

                    if (automation.LevelChangeFrames >= 2)
                    {
                        completedAnyLevel = true;
                        SetStatus("fingerprint: level complete");
                        ResetAutomation(ref automation);
                        cache = new SolverCache();
                        invalidFrames = 0;
                        retainedUsable = false;
                        PublishEmpty();
                        Thread.Sleep(120);
                        continue;
                    }

                    if (automation.Phase == AutomationPhase.Submitting && automation.InputJob is not null)
                    {
                        if (automation.InputJob.Pending)
                        {
                            SetStatus("fingerprint: waiting next level");
                            Thread.Sleep(FrameDelayMs);
                            continue;
                        }
                        if (!automation.InputJob.Succeeded)
                        {
                            ResetAutomation(ref automation);
                            cache = new SolverCache();
                            SetStatus("fingerprint: locating");
                            continue;
                        }
                        automation.InputJob = null;
                        automation.Phase = AutomationPhase.WaitingLevel;
                        automation.SubmittedAt = Now;
                    }

                    if (automation.Phase == AutomationPhase.WaitingLevel && ElapsedMs(automation.SubmittedAt) >= 3000)
                    {
                        ResetAutomation(ref automation);
                        cache = new SolverCache();
                        SetStatus("fingerprint: locating");
                        continue;
                    }

                    SetStatus("fingerprint: waiting next level");
                    Thread.Sleep(FrameDelayMs);
                    continue;
                }

                var state = AnalyzeFrame(frame, ref cache, ref geometry, out var minigame);
                if (!minigame)
                {
                    invalidFrames++;
                    if (++lostFrames >= 15)
                    {
                        SetStatus("fingerprint: exited");
                        break;
                    }
                    SetStatus("fingerprint: confirming exit");
                    if (invalidFrames == automationMissToleranceFrames) ResetAutomation(ref automation);
                    if (invalidFrames == overlayMissToleranceFrames)
                    {
                        retainedUsable = false;
                        PublishEmpty();
                    }
                    Thread.Sleep(50);
                    continue;
                }
                lostFrames = 0;
                if (!state.Visible)
                {
                    invalidFrames++;
                    if (invalidFrames == automationMissToleranceFrames) ResetAutomation(ref automation);
                    if (invalidFrames == overlayMissToleranceFrames)
                    {
                        retainedUsable = false;
                        PublishEmpty();
                    }
                    SetStatus("fingerprint: locating");
                    Thread.Sleep(FrameDelayMs);
                    continue;
                }
                invalidFrames = 0;
                retainedScene = BuildScene(frame.Source, state);
                retainedUsable = true;
                if (overlay.Enabled) overlay.Show(retainedScene);
                SetStatus("fingerprint: auto input");
                if (state.LevelMarker >= 0) PlanAndRunAutomation(state, ref automation, state.LevelMarker);
                Thread.Sleep(FrameDelayMs);
            }
        }
        finally
        {
            ResetAutomation(ref automation);
            overlay.Clear();
            ResetInGameCache();
        }
        return completedAnyLevel;
    }
}
