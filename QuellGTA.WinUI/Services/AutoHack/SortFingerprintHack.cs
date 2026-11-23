using System.Numerics;
using static QuellGTA.Services.AutoHack.HackUtil;

namespace QuellGTA.Services.AutoHack;

/// <summary>
/// 「指纹排序」骇入（games/sort_fingerprint_module.cpp）：右边是一枚完整的目标指纹，左边是竖着排的 8 个碎片框，
/// 每个框里是指纹的一条横向切片。当前选中的框是白色的，其余是绿色。A / D 把选中框里的切片换成上一个 / 下一个，
/// S 移到下一个框（到底后回到第一个）。目标是让第 i 个框里放的正好是目标指纹从上往下的第 i 条。
/// 做法：先由三条绿色标题条定位，再找出 8 个碎片框和白色的选中框；把目标指纹切成 8 条，
/// 每个碎片和每一条做二值图比对，认出它现在是第几条，最后算出最少的 A / D / S 按键。
/// 按完后等 350 毫秒再看一遍，还没排好就再修正；画面消失后最多等 5 秒看有没有下一轮。
/// 原版的叠加窗口（每个框左边一个括号，需要调整的框标红并写上要按几下）改为 HackScene 画在屏幕坐标上。
/// </summary>
public sealed class SortFingerprintHack(IHackHost host) : IHackGame
{
    public string Name => "排序指纹";

    /// <summary>原版 RunSession 的 log 回调：定位、方案、出错等过程记录（英文，原版文字）。</summary>
    public event Action<string>? Logged;

    // ───────── 数据结构 ─────────

    private readonly record struct RectI(int X, int Y, int W, int H)
    {
        public int Right => X + W;
        public int Bottom => Y + H;
        public double Cx => X + W / 2.0;
        public double Cy => Y + H / 2.0;
    }

    /// <summary>二值图（Binary）：每个像素 0 或 1。</summary>
    private sealed class Binary(int w, int h)
    {
        public readonly int W = w, H = h;
        public readonly byte[] Data = new byte[w * h];
        public byte this[int x, int y]
        {
            get => Data[y * W + x];
            set => Data[y * W + x] = value;
        }
    }

    /// <summary>三条绿色标题条：右上（目标指纹上方）、左（碎片列上方）、右（和左边同一行）。</summary>
    private readonly record struct Anchors(RectI Top, RectI Left, RectI Right);

    private sealed record RoiGeometry(Anchors Anchors, RectI[] Frames);

    private sealed class RoiResult
    {
        public Anchors Anchors;
        public RectI[] Frames = new RectI[8];
        public RectI TargetRect;
        public Binary TargetMask = new(0, 0);
        public int Selected;
        public readonly int[] Labels = new int[8];
        public readonly double[] Confidence = new double[8];
        public ulong TargetHash;
    }

    private sealed class Plan
    {
        public readonly List<int> Keys = [];
        public int[] Labels = new int[8];
        public int Selected;
    }

    /// <summary>原版用 std::runtime_error 表示「这一帧没认出来」，消息原样进日志。</summary>
    private sealed class RoiException(string message) : Exception(message);

    private const int VkA = 0x41, VkD = 0x44, VkS = 0x53;

    // 识别阶段找到的几何位置（原版的全局 gDetectedGeometry / gDetectedGeneration）
    private RoiGeometry? _detectedGeometry;
    private ulong _detectedGeneration;

    // ───────── 像素 ─────────

    private static int Blue(uint p) => (int)(p & 0xff);
    private static int Green(uint p) => (int)((p >> 8) & 0xff);
    private static int Red(uint p) => (int)((p >> 16) & 0xff);

    private static bool IsGreen(uint p, int minimum = 65)
    {
        int b = Blue(p), g = Green(p), r = Red(p);
        return g >= minimum && g * 100 > r * 128 && g * 100 > b * 120;
    }

    private static bool IsWhite(uint p)
    {
        int b = Blue(p), g = Green(p), r = Red(p);
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        return minimum >= 165 && maximum - minimum <= 65;
    }

    private static int Gray(uint p) => (29 * Blue(p) + 150 * Green(p) + 77 * Red(p)) >> 8;

    /// <summary>Image::at：和原版一样直接按 y * w + x 取，不做越界检查（越出整个数组时会抛异常，按「这一帧没认出来」处理）。</summary>
    private static uint At(HackFrame image, int x, int y) => image.Bgra[y * image.Width + x];

    // ───────── 标题条 ─────────

    /// <summary>dedupeRects：按面积从大到小保留，位置和大小都接近的算重复；最后按 (y, x) 排序。</summary>
    private static List<RectI> DedupeRects(List<RectI> input, int tolerance)
    {
        // 原版用 std::sort（不稳定）；这里用稳定排序，面积相同时保留先出现的
        var sorted = input.OrderByDescending(r => r.W * r.H).ToList();
        var result = new List<RectI>();
        foreach (var rect in sorted)
        {
            var duplicate = false;
            foreach (var kept in result)
            {
                if (Math.Abs(rect.Cx - kept.Cx) <= tolerance &&
                    Math.Abs(rect.Cy - kept.Cy) <= tolerance &&
                    Math.Abs(rect.W - kept.W) <= Math.Max(tolerance, (int)(kept.W * .08)) &&
                    Math.Abs(rect.H - kept.H) <= Math.Max(tolerance, (int)(kept.H * .08)))
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate) result.Add(rect);
        }
        return result.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
    }

    /// <summary>findHeaderBars：用一条高约 2.6% 画面高度的滑动带，逐列数绿色像素，找出宽度合适、足够实心的绿色横条。</summary>
    private static List<RectI> FindHeaderBars(HackFrame image)
    {
        int w = image.Width, h = image.Height;
        var bandH = Math.Max(8, Round(h * .026));
        var yStep = Math.Max(1, h / 1080);
        var allowedGap = Math.Max(3, (int)(w * .0045));
        var columns = new int[w];
        for (var y = 0; y < Math.Min(bandH, h); y++)
            for (var x = 0; x < w; x++) columns[x] += IsGreen(At(image, x, y)) ? 1 : 0;

        var activeMin = Math.Max(2, (int)(bandH * .16));
        var raw = new List<RectI>();
        for (var y = 0; y + bandH <= h; y++)
        {
            if (y > 0)
            {
                for (var x = 0; x < w; x++)
                {
                    columns[x] -= IsGreen(At(image, x, y - 1)) ? 1 : 0;
                    columns[x] += IsGreen(At(image, x, y + bandH - 1)) ? 1 : 0;
                }
            }
            if (y % yStep != 0) continue;
            int start = -1, lastActive = -1;
            long densitySum = 0;
            void Finish(int endX)
            {
                if (start < 0) return;
                var width = endX - start + 1;
                if (width >= w * .18 && width <= w * .48)
                {
                    var fill = (double)densitySum / (width * bandH);
                    if (fill >= .65) raw.Add(new RectI(start, y, width, bandH));
                }
                start = lastActive = -1;
                densitySum = 0;
            }
            for (var x = 0; x < w; x++)
            {
                var active = columns[x] >= activeMin;
                if (active)
                {
                    if (start < 0) start = x;
                    lastActive = x;
                }
                if (start >= 0) densitySum += columns[x];
                if (start >= 0 && x - lastActive > allowedGap) Finish(lastActive);
            }
            if (start >= 0) Finish(lastActive);
        }
        return DedupeRects(raw, Math.Max(7, h / 80));
    }

    /// <summary>selectAnchors：同一行的左右两条，加上右边那条正上方的一条；越靠下越好。</summary>
    private static Anchors? SelectAnchors(List<RectI> headers, int sw, int sh)
    {
        var bestScore = -1e30;
        Anchors? best = null;
        for (var i = 0; i < headers.Count; i++)
        {
            for (var j = i + 1; j < headers.Count; j++)
            {
                RectI left = headers[i], right = headers[j];
                if (left.X > right.X) (left, right) = (right, left);
                var sameRow = Math.Abs(left.Cy - right.Cy);
                if (sameRow > Math.Max(left.H * 1.0, Math.Max(right.H * 1.0, sh * .018))) continue;
                if (right.Cx - left.Cx < sw * .17) continue;
                if (Math.Abs(left.W - right.W) > Math.Max(left.W, right.W) * .35) continue;
                foreach (var top in headers)
                {
                    if (top.Cy >= right.Cy - right.H * 1.5) continue;
                    if (Math.Abs(top.Cx - right.Cx) >= right.W * .25) continue;
                    if (Math.Abs(top.W - right.W) >= right.W * .35) continue;
                    var score = left.Y * 2.0 - sameRow - Math.Abs(left.W - right.W) * .2;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new Anchors(top, left, right);
                    }
                }
            }
        }
        return best;
    }

    private static double GreenFillRatio(HackFrame image, RectI rect)
    {
        var x1 = Math.Clamp(rect.X, 0, image.Width);
        var y1 = Math.Clamp(rect.Y, 0, image.Height);
        var x2 = Math.Clamp(rect.Right, 0, image.Width);
        var y2 = Math.Clamp(rect.Bottom, 0, image.Height);
        var greenPixels = 0;
        for (var y = y1; y < y2; y++)
            for (var x = x1; x < x2; x++)
                greenPixels += IsGreen(At(image, x, y)) ? 1 : 0;
        return (double)greenPixels / Math.Max(1, (x2 - x1) * (y2 - y1));
    }

    /// <summary>anchorsPresent：三条标题条都还在原位（区域里至少 48% 是绿色）。</summary>
    private static bool AnchorsPresent(HackFrame image, Anchors anchors) =>
        GreenFillRatio(image, anchors.Top) >= .48 &&
        GreenFillRatio(image, anchors.Left) >= .48 &&
        GreenFillRatio(image, anchors.Right) >= .48;

    /// <summary>isSortFingerprintAnchorLayout：三条标题条的相对位置和宽度符合这个小游戏的版面（按 1080 高度换算）。</summary>
    private static bool IsSortFingerprintAnchorLayout(Anchors anchors, int screenH)
    {
        var scale = screenH / 1080.0;
        var alignTolerance = Math.Max(6, Round(16 * scale));
        var rowTolerance = Math.Max(6, Round(18 * scale));
        var heightTolerance = Math.Max(4, Round(7 * scale));
        var columnGap = anchors.Right.X - anchors.Left.Right;
        var rowGap = anchors.Right.Y - anchors.Top.Y;

        if (Math.Abs(anchors.Top.X - anchors.Right.X) > alignTolerance) return false;
        if (Math.Abs(anchors.Top.Right - anchors.Right.Right) > alignTolerance) return false;
        if (Math.Abs(anchors.Left.Y - anchors.Right.Y) > rowTolerance) return false;
        if (Math.Abs(anchors.Top.H - anchors.Right.H) > heightTolerance) return false;
        if (Math.Abs(anchors.Left.H - anchors.Right.H) > heightTolerance) return false;
        if (columnGap < (int)(4 * scale) || columnGap > (int)(44 * scale)) return false;
        if (rowGap < (int)(160 * scale) || rowGap > (int)(235 * scale)) return false;
        if (anchors.Left.W < screenH * .42 || anchors.Left.W > screenH * .56) return false;
        if (anchors.Right.W < screenH * .55 || anchors.Right.W > screenH * .70) return false;
        return true;
    }

    // ───────── 碎片框 ─────────

    private struct LineGroup { public int Y1, Y2, X1, X2, Count; }

    /// <summary>
    /// findFragmentFrames：在左标题条下方逐行找够长的绿 / 白横线，两两配成框；
    /// 只量最上面两个完整的框，用它们的间距按等差数列推出全部 8 个框。
    /// </summary>
    private static List<RectI> FindFragmentFrames(HackFrame image, RectI header)
    {
        var x1 = Math.Max(0, header.X - (int)(header.W * .03));
        var x2 = Math.Min(image.Width, header.Right + (int)(header.W * .03));
        var y1 = Math.Min(image.Height - 1, header.Bottom + Math.Max(4, (int)(image.Height * .010)));
        var threshold = Math.Max(30, (int)(header.W * .55));
        var allowedGap = Math.Max(3, (int)(header.W * .010));
        var lines = new List<LineGroup>();
        for (var y = y1; y < image.Height; y++)
        {
            int count = 0, first = x2, last = x1;
            int runStart = -1, runLast = -1, runCount = 0;
            void FinishRun()
            {
                if (runStart >= 0 && runLast - runStart > last - first)
                {
                    first = runStart; last = runLast; count = runCount;
                }
                runStart = runLast = -1; runCount = 0;
            }
            for (var x = x1; x < x2; x++)
            {
                var p = At(image, x, y);
                if (IsGreen(p) || IsWhite(p))
                {
                    if (runStart < 0) runStart = x;
                    runLast = x;
                    ++runCount;
                }
                if (runStart >= 0 && x - runLast > allowedGap) FinishRun();
            }
            FinishRun();
            if (count < threshold) continue;
            if (lines.Count > 0 && y <= lines[^1].Y2 + 2)
            {
                var line = lines[^1];
                line.Y2 = y;
                if (count > line.Count)
                {
                    line.Count = count;
                    line.X1 = first;
                    line.X2 = last;
                }
                lines[^1] = line;
            }
            else lines.Add(new LineGroup { Y1 = y, Y2 = y, X1 = first, X2 = last, Count = count });
        }

        var candidates = new List<RectI>();
        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = i + 1; j < lines.Count; j++)
            {
                var boxH = lines[j].Y2 - lines[i].Y1 + 1;
                if (boxH > image.Height * .10) break;
                if (boxH < image.Height * .03) continue;
                var bx1 = Math.Min(lines[i].X1, lines[j].X1);
                var bx2 = Math.Max(lines[i].X2, lines[j].X2);
                var boxW = bx2 - bx1 + 1;
                if (boxW < header.W * .64 || boxW > header.W * 1.04) continue;
                if (Math.Abs((bx1 + bx2) / 2.0 - header.Cx) > header.W * .18) continue;
                candidates.Add(new RectI(bx1, lines[i].Y1, boxW, boxH));
            }
        }
        candidates = DedupeRects(candidates, Math.Max(2, image.Height / 500));

        // 只量最上面两个完整的框，它们的上边坐标决定其余各行的等差位置
        var bestPairScore = 1e30;
        (RectI first, RectI second)? firstPair = null;
        foreach (var first in candidates)
        {
            foreach (var second in candidates)
            {
                if (second.Y <= first.Y) continue;
                var averageH = (first.H + second.H) / 2.0;
                var pitch = second.Y - first.Y;
                var gap = pitch - averageH;
                if (Math.Abs(first.H - second.H) > averageH * .12) continue;
                if (Math.Abs(first.W - second.W) > Math.Max(first.W, second.W) * .08) continue;
                if (Math.Abs(first.Cx - second.Cx) > Math.Max(first.W, second.W) * .05) continue;
                if (gap < averageH * .03 || gap > averageH * .32) continue;
                var score = first.Y * 1000.0 + Math.Abs(first.H - second.H) * 20.0 + Math.Abs(first.W - second.W) + gap;
                if (score < bestPairScore)
                {
                    bestPairScore = score;
                    firstPair = (first, second);
                }
            }
        }
        if (firstPair is not { } pair) return [];

        var p0 = pair.first;
        var p1 = pair.second;
        var step = p1.Y - p0.Y;
        var fx = Round((p0.X + p1.X) / 2.0);
        var fw = Round((p0.W + p1.W) / 2.0);
        var fh = Round((p0.H + p1.H) / 2.0);
        var inferred = new List<RectI>(8);
        for (var index = 0; index < 8; index++) inferred.Add(new RectI(fx, p0.Y + index * step, fw, fh));
        return inferred;
    }

    /// <summary>selectEightFrames：从候选框里挑 8 个间距最均匀、宽度和左右位置最一致的。</summary>
    private static RectI[]? SelectEightFrames(List<RectI> candidates, RectI header)
    {
        var useful = candidates.Where(r => r.Y > header.Bottom).OrderBy(r => r.Cy).ToList();
        if (useful.Count < 8) return null;
        if (useful.Count > 18) useful.RemoveRange(18, useful.Count - 18);

        var bestScore = 1e30;
        RectI[]? best = null;
        var pick = new int[8];
        void Evaluate()
        {
            var group = new RectI[8];
            var gaps = new double[7];
            double avgH = 0, avgW = 0, avgX = 0;
            for (var i = 0; i < 8; i++)
            {
                group[i] = useful[pick[i]];
                avgH += group[i].H;
                avgW += group[i].W;
                avgX += group[i].Cx;
                if (i > 0) gaps[i - 1] = group[i].Cy - group[i - 1].Cy;
            }
            avgH /= 8; avgW /= 8; avgX /= 8;
            var sortedGaps = (double[])gaps.Clone();
            Array.Sort(sortedGaps);
            var pitch = sortedGaps[3];
            if (pitch < avgH * .90 || pitch > avgH * 1.80) return;
            foreach (var gap in gaps) if (gap <= avgH * .65) return;
            double gapErr = 0, widthErr = 0, xErr = 0, heightErr = 0;
            foreach (var gap in gaps) gapErr += Math.Abs(gap - pitch) / pitch;
            foreach (var r in group)
            {
                widthErr += Math.Abs(r.W - avgW) / avgW;
                xErr += Math.Abs(r.Cx - avgX) / avgW;
                heightErr += Math.Abs(r.H - avgH) / avgH;
            }
            var score = gapErr * 5 + widthErr * 3 + xErr * 4 + heightErr * 2;
            if (score < bestScore) { bestScore = score; best = group; }
        }
        void Choose(int depth, int start)
        {
            if (depth == 8) { Evaluate(); return; }
            for (var i = start; i <= useful.Count - (8 - depth); i++)
            {
                pick[depth] = i;
                Choose(depth + 1, i + 1);
            }
        }
        Choose(0, 0);
        return best;
    }

    /// <summary>findSelected：框的上边和左右两边，白色占比最高的那个框是当前选中的。</summary>
    private static int FindSelected(HackFrame image, RectI[] frames)
    {
        var selected = -1;
        double bestRatio = 0;
        var bestWhite = 0;
        for (var index = 0; index < 8; index++)
        {
            var r = frames[index];
            var bandY = Math.Max(2, (int)(r.H * .13));
            var bandX = Math.Max(2, (int)(r.W * .018));
            var upper = Math.Max(bandY + 1, (int)(r.H * .58));
            int whiteCount = 0, greenCount = 0;
            for (var y = 0; y < upper; y++)
            {
                for (var x = 0; x < r.W; x++)
                {
                    if (!(y < bandY || x < bandX || x >= r.W - bandX)) continue;
                    var p = At(image, r.X + x, r.Y + y);
                    whiteCount += IsWhite(p) ? 1 : 0;
                    greenCount += IsGreen(p) ? 1 : 0;
                }
            }
            var ratio = (double)whiteCount / Math.Max(1, whiteCount + greenCount);
            if (ratio > bestRatio) { bestRatio = ratio; bestWhite = whiteCount; selected = index; }
        }
        if (selected < 0 || bestRatio < .52 || bestWhite < Math.Max(20, (int)(frames[selected].W * .12)))
            throw new RoiException("white selected frame was not found");
        return selected;
    }

    // ───────── 目标指纹 ─────────

    /// <summary>largestCluster：有序下标里，相邻差不超过 maxGap 的跨度最大的一段。</summary>
    private static List<int> LargestCluster(List<int> indices, int maxGap)
    {
        if (indices.Count == 0) return [];
        int bestStart = 0, bestEnd = 1, start = 0;
        for (var i = 1; i <= indices.Count; i++)
        {
            if (i == indices.Count || indices[i] - indices[i - 1] > maxGap)
            {
                if (indices[i - 1] - indices[start] > indices[bestEnd - 1] - indices[bestStart])
                {
                    bestStart = start; bestEnd = i;
                }
                start = i;
            }
        }
        return indices.GetRange(bestStart, bestEnd - bestStart);
    }

    /// <summary>findTarget：右标题条下方的亮绿色像素，按行列投影找出指纹的外接框，并裁出二值图。</summary>
    private static (RectI rect, Binary mask) FindTarget(HackFrame image, RectI header, RectI[] frames)
    {
        var x1 = Math.Max(0, header.X + (int)(header.W * .05));
        var x2 = Math.Min(image.Width, header.Right - (int)(header.W * .05));
        var y1 = Math.Min(image.Height - 1, header.Bottom + Math.Max(8, (int)(image.Height * .012)));
        var y2 = Math.Min(image.Height, Math.Max(frames[7].Bottom + (int)(image.Height * .025), y1 + (int)(image.Height * .35)));
        int rw = x2 - x1, rh = y2 - y1;
        var mask = new Binary(rw, rh);
        int[] rowCounts = new int[rh], colCounts = new int[rw];
        for (var y = 0; y < rh; y++)
        {
            for (var x = 0; x < rw; x++)
            {
                if (IsGreen(At(image, x1 + x, y1 + y), 105))
                {
                    mask[x, y] = 1;
                    ++rowCounts[y]; ++colCounts[x];
                }
            }
        }
        var rows = new List<int>();
        var cols = new List<int>();
        var rowMin = Math.Max(4, (int)(header.W * .012));
        var colMin = Math.Max(4, (int)(rh * .012));
        for (var y = 0; y < rh; y++) if (rowCounts[y] >= rowMin && rowCounts[y] < rw * .62) rows.Add(y);
        for (var x = 0; x < rw; x++) if (colCounts[x] >= colMin && colCounts[x] < rh * .62) cols.Add(x);
        rows = LargestCluster(rows, Math.Max(7, (int)(rh * .018)));
        cols = LargestCluster(cols, Math.Max(7, (int)(rw * .018)));
        if (rows.Count < 20 || cols.Count < 20) throw new RoiException("target fingerprint was not found");
        var target = new RectI(x1 + cols[0], y1 + rows[0], cols[^1] - cols[0] + 1, rows[^1] - rows[0] + 1);
        var cropped = new Binary(target.W, target.H);
        for (var y = 0; y < target.H; y++)
            for (var x = 0; x < target.W; x++)
                cropped[x, y] = mask[cols[0] + x, rows[0] + y];
        return (target, cropped);
    }

    // ───────── 碎片比对 ─────────

    private static int OtsuThreshold(int[] histogram, int total)
    {
        long sum = 0;
        for (var i = 0; i < 256; i++) sum += (long)i * histogram[i];
        long backgroundSum = 0;
        var backgroundWeight = 0;
        double best = -1;
        var threshold = 0;
        for (var t = 0; t < 256; t++)
        {
            backgroundWeight += histogram[t];
            if (backgroundWeight == 0) continue;
            var foregroundWeight = total - backgroundWeight;
            if (foregroundWeight == 0) break;
            backgroundSum += (long)t * histogram[t];
            var meanB = (double)backgroundSum / backgroundWeight;
            var meanF = (double)(sum - backgroundSum) / foregroundWeight;
            var between = (double)backgroundWeight * foregroundWeight * (meanB - meanF) * (meanB - meanF);
            if (between > best) { best = between; threshold = t; }
        }
        return threshold;
    }

    /// <summary>extractPiece：框内中间一块，亮于 Otsu 阈值且颜色对（选中框找白色，其余找绿色）的像素为前景。</summary>
    private static Binary ExtractPiece(HackFrame image, RectI r, bool selected)
    {
        var x1 = r.X + (int)(r.W * .11);
        var x2 = r.X + (int)(r.W * .89);
        var y1 = r.Y + Math.Max(2, (int)(r.H * .10));
        var y2 = r.Y + Math.Min(r.H - 2, (int)(r.H * .90));
        var result = new Binary(x2 - x1, y2 - y1);
        var histogram = new int[256];
        for (var y = y1; y < y2; y++) for (var x = x1; x < x2; x++) ++histogram[Gray(At(image, x, y))];
        var threshold = OtsuThreshold(histogram, result.W * result.H);
        for (var y = 0; y < result.H; y++)
        {
            for (var x = 0; x < result.W; x++)
            {
                var pixel = At(image, x1 + x, y1 + y);
                var grayscaleForeground = Gray(pixel) > threshold;
                var expectedColor = selected ? IsWhite(pixel) : IsGreen(pixel);
                result[x, y] = (byte)(grayscaleForeground && expectedColor ? 1 : 0);
            }
        }
        return result;
    }

    /// <summary>normalize：把前景的外接框等比缩放后居中放进 outW × outH。</summary>
    private static Binary Normalize(Binary source, int outW = 256, int outH = 56)
    {
        int minX = source.W, minY = source.H, maxX = -1, maxY = -1, count = 0;
        for (var y = 0; y < source.H; y++)
            for (var x = 0; x < source.W; x++)
                if (source[x, y] != 0)
                {
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); ++count;
                }
        var output = new Binary(outW, outH);
        if (count < 12) return output;
        int sw = maxX - minX + 1, sh = maxY - minY + 1;
        var scale = Math.Min((outW - 8.0) / sw, (outH - 4.0) / sh);
        var nw = Math.Max(1, (int)(sw * scale));
        var nh = Math.Max(1, (int)(sh * scale));
        int ox = (outW - nw) / 2, oy = (outH - nh) / 2;
        for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            {
                var sx = minX + Math.Min(sw - 1, (int)(x / scale));
                var sy = minY + Math.Min(sh - 1, (int)(y / scale));
                output[ox + x, oy + y] = source[sx, sy];
            }
        return output;
    }

    /// <summary>resizeFull：最近邻缩放到 outW × outH（不保持比例）。</summary>
    private static Binary ResizeFull(Binary source, int outW = 256, int outH = 56)
    {
        var output = new Binary(outW, outH);
        for (var y = 0; y < outH; y++)
        {
            var sy = Math.Min(source.H - 1, y * source.H / outH);
            for (var x = 0; x < outW; x++)
            {
                var sx = Math.Min(source.W - 1, x * source.W / outW);
                output[x, y] = source[sx, sy];
            }
        }
        return output;
    }

    private const int CompareW = 120;
    private const int CompareH = 24;
    private const int PackedWordsPerRow = (CompareW + 63) / 64;
    private const int ShiftStep = 4;

    /// <summary>TargetCandidate：目标指纹的一个横向窗口（属于第几条），带 5 种左右平移的打包位图。</summary>
    private sealed class TargetCandidate
    {
        public int Label;
        public readonly ulong[][] Shifted = new ulong[5][];
    }

    /// <summary>packMask：120 × 24 的二值图按位打包，可左右平移。</summary>
    private static ulong[] PackMask(Binary source, int shiftX = 0)
    {
        var packed = new ulong[CompareH * PackedWordsPerRow];
        for (var y = 0; y < CompareH; y++)
        {
            for (var x = 0; x < CompareW; x++)
            {
                if (source[x, y] == 0) continue;
                var destinationX = x + shiftX;
                if (destinationX < 0 || destinationX >= CompareW) continue;
                packed[y * PackedWordsPerRow + destinationX / 64] |= 1UL << (destinationX % 64);
            }
        }
        return packed;
    }

    /// <summary>buildTargetCandidates：在目标指纹上用几种高度的窗口从上往下滑，每个窗口按位置归到 0–7 条之一。</summary>
    private static List<TargetCandidate> BuildTargetCandidates(Binary target)
    {
        var candidates = new List<TargetCandidate>();
        var minWindow = Math.Max(8, (int)(target.H * .090));
        var maxWindow = Math.Max(minWindow, (int)(target.H * .132));
        var windowStep = Math.Max(2, target.H / 60);
        var yStep = Math.Max(1, target.H / 180);
        for (var windowH = minWindow; windowH <= maxWindow; windowH += windowStep)
        {
            var segmentPitch = (target.H - windowH) / 7.0;
            if (segmentPitch <= 0) continue;
            for (var y = 0; y + windowH <= target.H; y += yStep)
            {
                var resized = new Binary(CompareW, CompareH);
                for (var outY = 0; outY < CompareH; outY++)
                {
                    var sourceY = y + Math.Min(windowH - 1, outY * windowH / CompareH);
                    for (var outX = 0; outX < CompareW; outX++)
                    {
                        var sourceX = Math.Min(target.W - 1, outX * target.W / CompareW);
                        resized[outX, outY] = target[sourceX, sourceY];
                    }
                }
                var candidate = new TargetCandidate { Label = Math.Clamp(Round(y / segmentPitch), 0, 7) };
                for (var shiftIndex = 0; shiftIndex < 5; shiftIndex++)
                    candidate.Shifted[shiftIndex] = PackMask(resized, (shiftIndex - 2) * ShiftStep);
                candidates.Add(candidate);
            }
        }
        return candidates;
    }

    /// <summary>slidingScores：碎片和每一条的最大 Dice 相似度。validFraction 小于 1 时只比上面那部分行。</summary>
    private static double[] SlidingScores(Binary piece, List<TargetCandidate> candidates, double validFraction)
    {
        var normalizedPiece = ResizeFull(piece, CompareW, CompareH);
        var packedPiece = PackMask(normalizedPiece);
        var validHeight = Math.Max(1, (int)(CompareH * validFraction));
        var words = validHeight * PackedWordsPerRow;
        var pieceCount = 0;
        for (var index = 0; index < words; index++) pieceCount += BitOperations.PopCount(packedPiece[index]);

        var scores = new double[8];
        foreach (var candidate in candidates)
        {
            double best = 0;
            foreach (var shifted in candidate.Shifted)
            {
                int candidateCount = 0, intersection = 0;
                for (var index = 0; index < words; index++)
                {
                    candidateCount += BitOperations.PopCount(shifted[index]);
                    intersection += BitOperations.PopCount(packedPiece[index] & shifted[index]);
                }
                best = Math.Max(best, 2.0 * intersection / Math.Max(1, pieceCount + candidateCount));
            }
            scores[candidate.Label] = Math.Max(scores[candidate.Label], best);
        }
        return scores;
    }

    /// <summary>fingerprintHash：目标指纹缩到 64 × 96，按 8 × 12 的块是否有内容得到 64 位哈希，用来判断是不是换了一轮。</summary>
    private static ulong FingerprintHash(Binary target)
    {
        var fixedMask = Normalize(target, 64, 96);
        ulong hash = 0;
        const int blockW = 8;
        const int blockH = 12;
        var bitIndex = 0;
        for (var by = 0; by < fixedMask.H; by += blockH)
        {
            for (var bx = 0; bx < fixedMask.W; bx += blockW)
            {
                var occupied = 0;
                for (var y = by; y < Math.Min(by + blockH, fixedMask.H); y++)
                    for (var x = bx; x < Math.Min(bx + blockW, fixedMask.W); x++)
                        occupied += fixedMask[x, y];
                if (occupied >= 11) hash |= 1UL << bitIndex;
                ++bitIndex;
            }
        }
        return hash;
    }

    private static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>analyzePieces：每个框上下错动几个像素各取一次碎片，和 8 条比对，取得分最高的那条（错动越多扣分越多）。</summary>
    private static void AnalyzePieces(HackFrame image, RoiResult result)
    {
        var targetCandidates = BuildTargetCandidates(result.TargetMask);
        var scores = new double[8][];
        for (var row = 0; row < 8; row++)
        {
            scores[row] = new double[8];
            var tolerance = Math.Max(3, (int)(result.Frames[row].H * .06)) + row;
            var variants = new List<(int offset, Binary piece)>(tolerance * 2 + 1);
            for (var offset = -tolerance; offset <= tolerance; offset++)
            {
                var shifted = result.Frames[row];
                shifted = shifted with { Y = Math.Clamp(shifted.Y + offset, 0, image.Height - shifted.H) };
                variants.Add((offset, ExtractPiece(image, shifted, row == result.Selected)));
            }
            foreach (var variant in variants)
            {
                // 最后一行被选中时，下半部分可能被画面下方的提示遮住，只比上半
                var validFraction = row == 7 && row == result.Selected ? .50 : 1.0;
                var variantScores = SlidingScores(variant.piece, targetCandidates, validFraction);
                for (var targetIndex = 0; targetIndex < 8; targetIndex++)
                {
                    var adjusted = variantScores[targetIndex] - Math.Abs(variant.offset) * .005;
                    scores[row][targetIndex] = Math.Max(scores[row][targetIndex], adjusted);
                }
            }
        }
        for (var row = 0; row < 8; row++)
        {
            // std::max_element：相同时取靠前的
            var label = 0;
            for (var i = 1; i < 8; i++) if (scores[row][i] > scores[row][label]) label = i;
            result.Labels[row] = label;
            var sorted = (double[])scores[row].Clone();
            Array.Sort(sorted);
            Array.Reverse(sorted);
            var margin = sorted[0] - sorted[1];
            result.Confidence[row] = Math.Clamp(scores[row][label] * .75 + margin * 1.5, 0.0, 1.0);
        }
    }

    private static RoiResult AnalyzeStoredRoi(HackFrame image, RoiGeometry geometry, bool includePieceAnalysis = true)
    {
        var result = new RoiResult { Anchors = geometry.Anchors, Frames = (RectI[])geometry.Frames.Clone() };
        result.Selected = FindSelected(image, result.Frames);
        var (targetRect, targetMask) = FindTarget(image, geometry.Anchors.Right, result.Frames);
        result.TargetRect = targetRect;
        result.TargetMask = targetMask;
        result.TargetHash = FingerprintHash(result.TargetMask);
        if (includePieceAnalysis) AnalyzePieces(image, result);
        return result;
    }

    private static RoiResult AnalyzeRoi(HackFrame image, Anchors anchors, bool includePieceAnalysis = true)
    {
        var candidates = FindFragmentFrames(image, anchors.Left);
        var frames = SelectEightFrames(candidates, anchors.Left) ?? throw new RoiException("eight colored fragment frames were not found");
        return AnalyzeStoredRoi(image, new RoiGeometry(anchors, frames), includePieceAnalysis);
    }

    /// <summary>
    /// makePlan：从选中框开始往下依次处理（S 换到下一个框），每个框按 D（往后）或 A（往前）里步数少的方向调到正确的那条；
    /// 最后一个需要调整的框之后就不再按 S。
    /// </summary>
    private static Plan MakePlan(RoiResult result)
    {
        var plan = new Plan { Labels = (int[])result.Labels.Clone(), Selected = result.Selected };
        var lastRequiredStep = -1;
        for (var step = 0; step < 8; step++)
        {
            var row = (result.Selected + step) % 8;
            if (result.Labels[row] != row) lastRequiredStep = step;
        }
        for (var step = 0; step <= lastRequiredStep; step++)
        {
            var row = (result.Selected + step) % 8;
            var current = result.Labels[row];
            var target = row;
            var rightSteps = (target - current + 8) % 8;
            var leftSteps = (current - target + 8) % 8;
            var key = rightSteps <= leftSteps ? VkD : VkA;
            var count = Math.Min(rightSteps, leftSteps);
            for (var i = 0; i < count; i++) plan.Keys.Add(key);
            if (step != lastRequiredStep) plan.Keys.Add(VkS);
        }
        return plan;
    }

    private static string KeyPlanText(Plan plan) => string.Concat(plan.Keys.Select(key => (char)key + " "));

    private const int VerificationDelayMs = 350;

    // ───────── 叠加层 ─────────

    private readonly record struct OverlayRow(float Left, float Top, float Right, float Bottom, int Clicks);

    private HackScene? _overlayScene;
    private bool _overlayShown;

    /// <summary>ScaleUi：按游戏客户区高度（屏幕像素）相对 1080 缩放，限制在 0.75–2 倍。</summary>
    private static int ScaleUi(HackFrame image, int value)
    {
        var height = image.ClientHeight > 0 ? image.ClientHeight : 1080;
        var scale = Math.Clamp(height / 1080.0, 0.75, 2.0);
        return Math.Max(1, Round(value * scale));
    }

    private static uint Argb(int r, int g, int b) => 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;

    /// <summary>
    /// PublishOverlay + DrawOverlay：每个框左边画一个「[」形括号，不用调整的框是蓝色；
    /// 需要调整（includePlan 时）的框是红色，括号左边再画一个深色圆角标签「N clicks」和一条红色连线。
    /// 原版的圆角矩形这里画成直角。
    /// </summary>
    private void PublishOverlay(HackFrame image, RoiResult result, bool includePlan)
    {
        var rows = new OverlayRow[8];
        for (var row = 0; row < 8; row++)
        {
            // ToScreenRect：分析坐标按比例换成屏幕坐标，四舍五入到整像素
            var frame = result.Frames[row];
            float left = image.ScreenX + Round(frame.X * image.ToScreenX), top = image.ScreenY + Round(frame.Y * image.ToScreenY);
            float right = image.ScreenX + Round(frame.Right * image.ToScreenX), bottom = image.ScreenY + Round(frame.Bottom * image.ToScreenY);
            var clicks = 0;
            if (includePlan)
            {
                var rightClicks = (row - result.Labels[row] + 8) % 8;
                var leftClicks = (result.Labels[row] - row + 8) % 8;
                clicks = Math.Min(rightClicks, leftClicks);
            }
            rows[row] = new OverlayRow(left, top, right, bottom, clicks);
        }

        var originX = image.ScreenX;
        var bracketGap = ScaleUi(image, 10);
        var bracketWidth = ScaleUi(image, 12);
        var penWidth = ScaleUi(image, 3);
        var glowWidth = penWidth + ScaleUi(image, 4);
        var tagW = ScaleUi(image, 82);
        var tagH = ScaleUi(image, 27);
        var tagGap = ScaleUi(image, 13);
        var blueGlow = Argb(0, 34, 62);
        var blue = Argb(24, 174, 255);
        var redGlow = Argb(74, 8, 12);
        var red = Argb(255, 55, 65);
        var tagBorder = Argb(255, 84, 92);
        var tagFill = Argb(17, 22, 31);
        var tagText = Argb(245, 248, 252);

        var scene = new HackScene();
        void Bracket(OverlayRow r, uint color, float width)
        {
            var x = r.Left - bracketGap;
            scene.Line(x + bracketWidth, r.Top, x, r.Top, color, width);
            scene.Line(x, r.Top, x, r.Bottom, color, width);
            scene.Line(x, r.Bottom, x + bracketWidth, r.Bottom, color, width);
        }
        foreach (var r in rows)
        {
            if (r.Clicks > 0)
            {
                Bracket(r, redGlow, glowWidth);
                Bracket(r, red, penWidth);
            }
            else
            {
                Bracket(r, blueGlow, glowWidth);
                Bracket(r, blue, penWidth);
            }
            if (r.Clicks <= 0) continue;

            // 标签位置按相对游戏客户区左边的坐标算，至少离左边 4 像素（和原版一样）
            var relativeLeft = (int)r.Left - originX;
            var tagLeft = originX + Math.Max(ScaleUi(image, 4), relativeLeft - bracketGap - bracketWidth - tagGap - tagW);
            var tagTop = ((int)r.Top + (int)r.Bottom - tagH) / 2;
            scene.FillRect(tagLeft, tagTop, tagW, tagH, tagFill);
            scene.Rect(tagLeft, tagTop, tagW, tagH, tagBorder, ScaleUi(image, 1));
            scene.Text(tagLeft + tagW / 2f, tagTop + tagH / 2f, $"{r.Clicks} clicks", tagText, ScaleUi(image, 15), outline: false);

            var cy = ((int)r.Top + (int)r.Bottom) / 2;
            scene.Line(tagLeft + tagW + ScaleUi(image, 3), cy, r.Left - bracketGap - ScaleUi(image, 3), cy, red, penWidth);
        }

        _overlayScene = scene;
        if (host.Overlay.Enabled)
        {
            host.Overlay.Show(scene);
            _overlayShown = true;
        }
    }

    /// <summary>ClearOverlayState：清掉要画的内容。</summary>
    private void ClearOverlayState()
    {
        _overlayScene = null;
        _overlayShown = false;
        host.Overlay.Clear();
    }

    /// <summary>SyncOverlayWindow：叠加层开关中途变化时，关掉就清空，打开就补画最近一次的内容。</summary>
    private void SyncOverlay()
    {
        var enabled = host.Overlay.Enabled;
        if (!enabled && _overlayShown)
        {
            host.Overlay.Clear();
            _overlayShown = false;
        }
        else if (enabled && !_overlayShown && _overlayScene is { IsEmpty: false } scene)
        {
            host.Overlay.Show(scene);
            _overlayShown = true;
        }
    }

    // ───────── 入口 ─────────

    public bool DetectInGame()
    {
        try
        {
            var screen = host.Capture() ?? throw new RoiException("screen capture failed");
            var headers = FindHeaderBars(screen);
            var anchors = SelectAnchors(headers, screen.Width, screen.Height);
            if (anchors is not { } found || !AnchorsPresent(screen, found) || !IsSortFingerprintAnchorLayout(found, screen.Height))
            {
                ResetInGameCache();
                return false;
            }
            var located = AnalyzeRoi(screen, found, false);
            _detectedGeometry = new RoiGeometry(located.Anchors, located.Frames);
            _detectedGeneration = screen.WindowGeneration;
            return true;
        }
        catch (Exception)
        {
            ResetInGameCache();
            return false;
        }
    }

    public void ResetInGameCache()
    {
        _detectedGeometry = null;
        _detectedGeneration = 0;
    }

    private void Log(string message) => Logged?.Invoke(message);

    /// <summary>
    /// 运行到「一轮或多轮做完后 5 秒内没出现下一轮」为止，或被要求停止。
    /// 和原版一样返回「至少完成过一轮」（被要求停止时也是）。
    /// </summary>
    public bool RunSession(Func<bool> stopRequested, Action<string> status)
    {
        const double frameIntervalMs = 16.667;

        var storedGeometry = _detectedGeometry;
        var storedGeneration = _detectedGeneration;
        ulong? handledHash = null;
        var completedAnyRound = false;
        var waitingForNextRound = false;
        var verificationPending = false;
        HackJob? inputJob = null;
        ulong inputTargetHash = 0;
        var correctionAttempt = 0;
        long verificationDue = 0, nextRoundDeadline = 0;
        var lastError = "";
        long lastErrorTime = 0;
        var lastStatus = "";
        _overlayScene = null;
        _overlayShown = false;

        void SetStatus(string text)
        {
            if (text == lastStatus) return;
            lastStatus = text;
            status(text);
        }
        void WaitForNextFrame(long frameStarted) => WaitForFrame(stopRequested, frameStarted, frameIntervalMs);
        void BeginNextRoundWait(long now, string message)
        {
            if (waitingForNextRound) return;
            verificationPending = false;
            ClearOverlayState();
            waitingForNextRound = true;
            nextRoundDeadline = now + Ms(5000);
            SetStatus("sort_fingerprint: waiting next round");
            Log(message);
        }
        void ResumeNextRound()
        {
            waitingForNextRound = false;
            verificationPending = false;
            correctionAttempt = 0;
            handledHash = null;
            SetStatus("sort_fingerprint: analyzing next round");
            Log("sort_fingerprint: detected next round; reusing cached ROI geometry");
        }

        SetStatus("sort_fingerprint: locating");
        while (!stopRequested())
        {
            var frameStarted = Now;
            if (waitingForNextRound && frameStarted >= nextRoundDeadline)
            {
                Log("sort_fingerprint: no next round within 5 seconds; completed");
                break;
            }
            SyncOverlay();
            try
            {
                var now = Now;
                var screen = host.Capture() ?? throw new RoiException("screen capture failed");
                // 原版取的是 GetForegroundWindow()；这里只在前台正好是游戏窗口时才有值
                var foreground = host.ForegroundGameWindow(screen);

                if (storedGeometry is not null && storedGeneration != screen.WindowGeneration)
                {
                    storedGeometry = null;
                    storedGeneration = 0;
                    ResetInGameCache();
                }

                if (storedGeometry is null)
                {
                    var headers = FindHeaderBars(screen);
                    var anchors = SelectAnchors(headers, screen.Width, screen.Height);
                    if (anchors is not { } found)
                    {
                        if (completedAnyRound)
                            BeginNextRoundWait(now, "sort_fingerprint: geometry lost after a completed round; waiting up to 5 seconds for next round");
                        WaitForNextFrame(frameStarted);
                        continue;
                    }
                    var located = AnalyzeRoi(screen, found, false);
                    storedGeometry = new RoiGeometry(located.Anchors, located.Frames);
                    storedGeneration = screen.WindowGeneration;
                    PublishOverlay(screen, located, false);
                    if (waitingForNextRound) ResumeNextRound();
                    else
                    {
                        SetStatus("sort_fingerprint: analyzing");
                        Log("sort_fingerprint: detected first round; cached ROI geometry");
                    }
                }
                else if (!AnchorsPresent(screen, storedGeometry.Anchors))
                {
                    if (!waitingForNextRound)
                    {
                        var headers = FindHeaderBars(screen);
                        var relocatedAnchors = SelectAnchors(headers, screen.Width, screen.Height);
                        if (relocatedAnchors is { } relocatedFound && AnchorsPresent(screen, relocatedFound) &&
                            IsSortFingerprintAnchorLayout(relocatedFound, screen.Height))
                        {
                            var relocated = AnalyzeRoi(screen, relocatedFound, false);
                            storedGeometry = new RoiGeometry(relocated.Anchors, relocated.Frames);
                            storedGeneration = screen.WindowGeneration;
                            PublishOverlay(screen, relocated, false);
                            Log("sort_fingerprint: cached anchors failed; relocated in full frame");
                        }
                        else
                        {
                            BeginNextRoundWait(now, "sort_fingerprint: minigame left; waiting up to 5 seconds for next round");
                        }
                    }

                    if (waitingForNextRound)
                    {
                        if (inputJob is not null && !inputJob.Pending)
                        {
                            if (inputJob.Succeeded)
                            {
                                handledHash = inputTargetHash;
                                completedAnyRound = true;
                            }
                            else
                            {
                                handledHash = null;
                                correctionAttempt = 0;
                                Log("sort_fingerprint: foreground changed or input failed while waiting");
                            }
                            inputJob = null;
                        }
                        if (now >= nextRoundDeadline)
                        {
                            Log("sort_fingerprint: no next round within 5 seconds; completed");
                            break;
                        }
                        WaitForNextFrame(frameStarted);
                        continue;
                    }
                }
                else if (waitingForNextRound)
                {
                    ResumeNextRound();
                }

                if (inputJob is not null)
                {
                    if (inputJob.Pending)
                    {
                        WaitForNextFrame(frameStarted);
                        continue;
                    }
                    if (inputJob.Succeeded)
                    {
                        handledHash = inputTargetHash;
                        completedAnyRound = true;
                        verificationPending = true;
                        verificationDue = Now + Ms(VerificationDelayMs);
                        SetStatus("sort_fingerprint: verifying input");
                    }
                    else
                    {
                        handledHash = null;
                        verificationPending = false;
                        correctionAttempt = 0;
                        Log("sort_fingerprint: foreground changed or input failed; retrying analysis");
                    }
                    inputJob = null;
                    WaitForNextFrame(frameStarted);
                    continue;
                }

                if (handledHash is not null && (!verificationPending || Now < verificationDue))
                {
                    WaitForNextFrame(frameStarted);
                    continue;
                }

                if (foreground == 0)
                {
                    // 游戏不在前台时先不分析也不发键（原版取 GetForegroundWindow() 直接发，前台一变才中止）
                    WaitForNextFrame(frameStarted);
                    continue;
                }

                var result = AnalyzeStoredRoi(screen, storedGeometry!, false);
                var sameHash = handledHash is { } handled && HammingDistance(handled, result.TargetHash) <= 3;
                var isVerification = false;
                if (sameHash)
                {
                    if (!verificationPending || Now < verificationDue)
                    {
                        WaitForNextFrame(frameStarted);
                        continue;
                    }
                    verificationPending = false;
                    isVerification = true;
                    ++correctionAttempt;
                }
                else if (handledHash is not null)
                {
                    verificationPending = false;
                    correctionAttempt = 0;
                    handledHash = null;
                    Log("sort_fingerprint: fingerprint changed without a visible gap; treating it as a new round");
                }

                AnalyzePieces(screen, result);
                var plan = MakePlan(result);
                PublishOverlay(screen, result, true);
                Log($"sort_fingerprint: {(isVerification ? "correction" : "initial")} plan, selected row {result.Selected + 1}, keys: {KeyPlanText(plan)}");

                if (plan.Keys.Count == 0)
                {
                    handledHash = result.TargetHash;
                    completedAnyRound = true;
                    verificationPending = false;
                    SetStatus("sort_fingerprint: round complete");
                }
                else
                {
                    var keys = plan.Keys.Select(key => new HackKey(key)).ToList();
                    inputTargetHash = result.TargetHash;
                    inputJob = host.QueueSequence(keys, foreground);
                    SetStatus("sort_fingerprint: verifying input");
                }
            }
            catch (Exception error)
            {
                storedGeometry = null;
                storedGeneration = 0;
                ResetInGameCache();
                var what = error.Message;
                var now = Now;
                if (completedAnyRound)
                    BeginNextRoundWait(now, "sort_fingerprint: analysis lost after a completed round; waiting up to 5 seconds for next round");
                if (what != lastError || now - lastErrorTime >= Ms(2000))
                {
                    Log("sort_fingerprint: ROI not stable: " + what);
                    lastError = what;
                    lastErrorTime = now;
                }
            }
            WaitForNextFrame(frameStarted);
        }

        host.CancelAll();
        ClearOverlayState();
        ResetInGameCache();
        return completedAnyRound;
    }
}
