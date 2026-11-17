using static QuellGTA.Services.AutoHack.HackUtil;

namespace QuellGTA.Services.AutoHack;

/// <summary>
/// 「找数字」骇入（games/find_number_module.cpp）：画面顶部是一条蓝色横条，下面一行 8 个红色的目标数字，
/// 再下面是 8 行 × 10 组两位数。当前选中的 4 组是红色的，用方向键把选区移到与目标相同的 4 组上再回车。
/// 所有坐标都是分析画面上的像素，不是屏幕像素。
/// </summary>
public sealed class FindNumberHack(IHackHost host) : IHackGame
{
    public string Name => "找数字";

    private struct Color { public double R, G, B; }
    private struct Box { public int Left, Top, Right, Bottom, Pixels; }
    private readonly record struct Bar(int Left, int Top, int Right, int Bottom, bool Found);
    private sealed record Geometry(Bar Bar, HackRect TargetRoi, HackRect GridRoi, ulong WindowGeneration, int FrameWidth, int FrameHeight);
    private struct Analysis { public bool Readable, Match; public int CurrentStart, TargetStart; }
    private enum PixelKind { RedOnly, AllInk }

    private const int GlyphW = 20, GlyphH = 32;

    private Geometry? _detected;

    private static Color PixelColor(uint pixel) => new() { R = (pixel >> 16) & 255, G = (pixel >> 8) & 255, B = pixel & 255 };

    private static double ColorDistance(Color a, Color b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db) / 441.673;
    }

    /// <summary>偏蓝的彩色像素（顶部横条的颜色）。</summary>
    private static bool CoolChromatic(uint pixel)
    {
        var c = PixelColor(pixel);
        var maximum = Math.Max(c.R, Math.Max(c.G, c.B));
        var minimum = Math.Min(c.R, Math.Min(c.G, c.B));
        var chroma = maximum - minimum;
        return maximum >= 48 && chroma >= 30 && chroma / maximum >= .32 &&
               c.B - c.R >= Math.Max(22.0, chroma * .42) && c.B >= c.G - chroma * .18;
    }

    private static bool Red(uint pixel)
    {
        var c = PixelColor(pixel);
        var maximum = Math.Max(c.R, Math.Max(c.G, c.B));
        var minimum = Math.Min(c.R, Math.Min(c.G, c.B));
        var chroma = maximum - minimum;
        return c.R >= 105 && chroma >= 45 && c.R == maximum && c.R - Math.Max(c.G, c.B) >= chroma * .55;
    }

    /// <summary>数字的笔画：红色，或者偏亮的灰白色。</summary>
    private static bool Ink(uint pixel)
    {
        if (Red(pixel)) return true;
        var c = PixelColor(pixel);
        var maximum = Math.Max(c.R, Math.Max(c.G, c.B));
        var minimum = Math.Min(c.R, Math.Min(c.G, c.B));
        return maximum >= 125 && minimum >= 92 && maximum - minimum <= maximum * .28;
    }

    private static bool Matches(uint pixel, PixelKind kind) => kind == PixelKind.RedOnly ? Red(pixel) : Ink(pixel);

    private static Color MeanColor(HackFrame frame, int left, int top, int right, int bottom, int step)
    {
        left = Math.Clamp(left, 0, frame.Width);
        right = Math.Clamp(right, 0, frame.Width);
        top = Math.Clamp(top, 0, frame.Height);
        bottom = Math.Clamp(bottom, 0, frame.Height);
        var result = new Color();
        var count = 0;
        for (var y = top; y < bottom; y += step)
            for (var x = left; x < right; x += step)
            {
                var c = PixelColor(frame.Bgra[y * frame.Width + x]);
                result.R += c.R; result.G += c.G; result.B += c.B; count++;
            }
        if (count > 0) { result.R /= count; result.G /= count; result.B /= count; }
        return result;
    }

    private struct RowRun { public int Left, Right, Y; public double Purity; }

    /// <summary>一行里最长的一段蓝色，允许中间夹少量杂色。</summary>
    private static RowRun LongestCoolRun(HackFrame frame, int y, int allowedGap)
    {
        var best = new RowRun();
        int start = -1, good = 0, misses = 0;
        void Consider(int right)
        {
            var length = right - start;
            var purity = length > 0 ? good / (double)length : 0;
            if (length > best.Right - best.Left && purity >= .94) best = new RowRun { Left = start, Right = right, Y = y, Purity = purity };
        }
        for (var x = 0; x < frame.Width; x++)
        {
            if (CoolChromatic(frame.Bgra[y * frame.Width + x]))
            {
                if (start < 0) start = x;
                good++; misses = 0; continue;
            }
            if (start < 0) continue;
            if (++misses <= allowedGap) continue;
            Consider(x - misses + 1);
            start = -1; good = 0; misses = 0;
        }
        if (start >= 0) Consider(frame.Width - misses);
        return best;
    }

    private static bool MatchingBarRow(HackFrame frame, int y, int left, int right, Color reference)
    {
        var inset = Math.Max(3, Round(frame.Height * .035));
        var step = Math.Max(1, frame.Height / 540);
        int cool = 0, close = 0, count = 0;
        for (var x = left + inset; x < right - inset; x += step)
        {
            var pixel = frame.Bgra[y * frame.Width + x];
            if (CoolChromatic(pixel)) cool++;
            if (ColorDistance(PixelColor(pixel), reference) <= .12) close++;
            count++;
        }
        return count > 0 && cool / (double)count >= .95 && close / (double)count >= .93;
    }

    private static bool ValidateBar(HackFrame frame, Bar bar)
    {
        if (!bar.Found || frame.Height < 360 || bar.Left < 0 || bar.Top < 0 || bar.Right > frame.Width || bar.Bottom > frame.Height) return false;
        int length = bar.Right - bar.Left, thickness = bar.Bottom - bar.Top;
        if (length < frame.Height * .86 || length > frame.Height * 1.45 ||
            thickness < frame.Height * .035 || thickness > frame.Height * .09) return false;
        var step = Math.Max(1, frame.Height / 540);
        var inset = Math.Max(3, Round(frame.Height * .04));
        var reference = MeanColor(frame, bar.Left + inset, bar.Top, bar.Right - inset, bar.Bottom, step);
        int valid = 0, count = 0;
        for (var y = bar.Top; y < bar.Bottom; y += step)
            for (var x = bar.Left + inset; x < bar.Right - inset; x += step)
            {
                var pixel = frame.Bgra[y * frame.Width + x];
                if (CoolChromatic(pixel) && ColorDistance(PixelColor(pixel), reference) <= .12) valid++;
                count++;
            }
        if (count == 0 || valid / (double)count < .955) return false;
        // 横条两端内外的颜色要有明显差别，排除整片蓝色的背景
        var edgeWidth = Math.Max(3, Round(frame.Height * .018));
        var edgeInset = Math.Max(2, Round(frame.Height * .008));
        var verticalInset = Math.Max(1, thickness / 4);
        var insideLeft = MeanColor(frame, bar.Left + edgeInset, bar.Top + verticalInset, bar.Left + edgeInset + edgeWidth, bar.Bottom - verticalInset, step);
        var outsideLeft = MeanColor(frame, bar.Left - edgeInset - edgeWidth, bar.Top + verticalInset, bar.Left - edgeInset, bar.Bottom - verticalInset, step);
        var insideRight = MeanColor(frame, bar.Right - edgeInset - edgeWidth, bar.Top + verticalInset, bar.Right - edgeInset, bar.Bottom - verticalInset, step);
        var outsideRight = MeanColor(frame, bar.Right + edgeInset, bar.Top + verticalInset, bar.Right + edgeInset + edgeWidth, bar.Bottom - verticalInset, step);
        return ColorDistance(insideLeft, outsideLeft) >= .18 && ColorDistance(insideRight, outsideRight) >= .18;
    }

    private static Bar FindBar(HackFrame frame)
    {
        if (frame.Height < 360 || frame.Width <= 0 || frame.Bgra.Length != frame.Width * frame.Height) return default;
        var scanTop = Math.Max(0, Round(frame.Height * .035));
        var scanBottom = Math.Min(frame.Height, Round(frame.Height * .24));
        var step = Math.Max(1, frame.Height / 540);
        var best = new RowRun();
        for (var y = scanTop; y < scanBottom; y += step)
        {
            var run = LongestCoolRun(frame, y, step);
            var length = run.Right - run.Left;
            if (length >= frame.Height * .86 && length <= frame.Height * 1.45 && length > best.Right - best.Left) best = run;
        }
        if (best.Right <= best.Left) return default;
        var inset = Math.Max(3, Round(frame.Height * .04));
        var reference = MeanColor(frame, best.Left + inset, best.Y, best.Right - inset, best.Y + 1, step);
        int top = best.Y, bottom = best.Y + 1;
        while (top > 0 && MatchingBarRow(frame, top - 1, best.Left, best.Right, reference)) top--;
        while (bottom < frame.Height && MatchingBarRow(frame, bottom, best.Left, best.Right, reference)) bottom++;
        var bar = new Bar(best.Left, top, best.Right, bottom, true);
        return ValidateBar(frame, bar) ? bar : default;
    }

    /// <summary>在区域里找连通的笔画块（八邻域）。</summary>
    private static List<Box> Components(HackFrame frame, HackRect roi, int minimumHeight, int minimumPixels, PixelKind kind)
    {
        int roiLeft = Math.Clamp(roi.Left, 0, frame.Width), roiRight = Math.Clamp(roi.Right, 0, frame.Width);
        int roiTop = Math.Clamp(roi.Top, 0, frame.Height), roiBottom = Math.Clamp(roi.Bottom, 0, frame.Height);
        int width = roiRight - roiLeft, height = roiBottom - roiTop;
        var result = new List<Box>();
        if (width <= 0 || height <= 0) return result;
        var seen = new bool[width * height];
        var pending = new List<int>(2048);
        for (var sy = 0; sy < height; sy++)
            for (var sx = 0; sx < width; sx++)
            {
                var start = sy * width + sx;
                if (seen[start] || !Matches(frame.Bgra[(roiTop + sy) * frame.Width + roiLeft + sx], kind)) continue;
                seen[start] = true; pending.Clear(); pending.Add(start);
                var box = new Box { Left = roiLeft + sx, Top = roiTop + sy, Right = roiLeft + sx + 1, Bottom = roiTop + sy + 1 };
                for (var q = 0; q < pending.Count; q++)
                {
                    int index = pending[q], px = index % width, py = index / width;
                    box.Pixels++;
                    box.Left = Math.Min(box.Left, roiLeft + px); box.Top = Math.Min(box.Top, roiTop + py);
                    box.Right = Math.Max(box.Right, roiLeft + px + 1); box.Bottom = Math.Max(box.Bottom, roiTop + py + 1);
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int nx = px + dx, ny = py + dy;
                            if ((dx == 0 && dy == 0) || nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                            var next = ny * width + nx;
                            if (!seen[next] && Matches(frame.Bgra[(roiTop + ny) * frame.Width + roiLeft + nx], kind))
                            {
                                seen[next] = true; pending.Add(next);
                            }
                        }
                }
                if (box.Bottom - box.Top >= minimumHeight && box.Pixels >= minimumPixels) result.Add(box);
            }
        return result;
    }

    private static double CenterY(List<Box> row) => row.Count == 0 ? 0 : row.Sum(b => (b.Top + b.Bottom) * .5) / row.Count;

    private static List<List<Box>> GroupRows(List<Box> boxes, int tolerance)
    {
        boxes.Sort((a, b) => a.Top + a.Bottom != b.Top + b.Bottom ? (a.Top + a.Bottom).CompareTo(b.Top + b.Bottom) : a.Left.CompareTo(b.Left));
        var rows = new List<List<Box>>();
        var centers = new List<double>();
        foreach (var box in boxes)
        {
            var center = (box.Top + box.Bottom) * .5;
            if (rows.Count == 0 || Math.Abs(center - centers[^1]) > tolerance)
            {
                rows.Add([box]); centers.Add(center);
            }
            else
            {
                var row = rows[^1];
                centers[^1] = (centers[^1] * row.Count + center) / (row.Count + 1);
                row.Add(box);
            }
        }
        foreach (var row in rows) row.Sort((a, b) => a.Left.CompareTo(b.Left));
        return rows;
    }

    /// <summary>找出间距均匀的 8 行、每行正好 20 个数字。</summary>
    private static List<Box>[]? FindGridRows(HackFrame frame, HackRect roi)
    {
        var minimumHeight = Math.Max(12, Round(frame.Height * .024));
        var boxes = Components(frame, roi, minimumHeight, minimumHeight * 2, PixelKind.AllInk);
        var maximumHeight = Round(frame.Height * .052);
        var maximumWidth = Round(frame.Height * .040);
        boxes.RemoveAll(box => box.Bottom - box.Top > maximumHeight || box.Right - box.Left > maximumWidth);
        var grouped = GroupRows(boxes, Math.Max(3, Round(frame.Height * .009)));
        var candidates = grouped.Where(row => row.Count == 20).ToList();
        var bestStart = -1;
        var bestError = double.MaxValue;
        for (var start = 0; start + 7 < candidates.Count; start++)
        {
            var spacing = new double[7];
            double average = 0;
            var valid = true;
            for (var i = 0; i < 7; i++)
            {
                spacing[i] = CenterY(candidates[start + i + 1]) - CenterY(candidates[start + i]);
                average += spacing[i];
                valid = valid && spacing[i] >= frame.Height * .040 && spacing[i] <= frame.Height * .075;
            }
            if (!valid) continue;
            average /= spacing.Length;
            var error = spacing.Sum(value => Math.Abs(value - average));
            if (error < bestError) { bestError = error; bestStart = start; }
        }
        if (bestStart < 0) return null;
        var rows = new List<Box>[8];
        for (var i = 0; i < 8; i++) rows[i] = candidates[bestStart + i];
        return rows;
    }

    private static Geometry? LocateGeometry(HackFrame frame)
    {
        var bar = FindBar(frame);
        if (!bar.Found) return null;
        var inset = Math.Max(4, Round(frame.Height * .025));
        var grid = new HackRect(bar.Left + inset, bar.Bottom + Round(frame.Height * .17), bar.Right - inset,
            Math.Min(frame.Height, bar.Bottom + Round(frame.Height * .70)));
        var rows = FindGridRows(frame, grid);
        if (rows is null) return null;
        var target = new HackRect(bar.Left + inset, bar.Bottom + Round(frame.Height * .025), bar.Right - inset,
            Round(CenterY(rows[0]) - frame.Height * .055));
        if (target.Bottom <= target.Top) return null;
        return new Geometry(bar, target, grid, frame.WindowGeneration, frame.Width, frame.Height);
    }

    /// <summary>把一个数字缩放成 20×32 的灰度小图，每格取 4×4 个采样点。</summary>
    private static float[] Normalize(HackFrame frame, Box box, PixelKind kind)
    {
        var glyph = new float[GlyphW * GlyphH];
        int sourceWidth = box.Right - box.Left, sourceHeight = box.Bottom - box.Top;
        for (var oy = 0; oy < GlyphH; oy++)
            for (var ox = 0; ox < GlyphW; ox++)
            {
                var hits = 0;
                for (var sy = 0; sy < 4; sy++)
                    for (var sx = 0; sx < 4; sx++)
                    {
                        var u = (ox + (sx + .5) / 4) / 20.0;
                        var v = (oy + (sy + .5) / 4) / 32.0;
                        var x = Math.Clamp(box.Left + (int)(u * sourceWidth), box.Left, box.Right - 1);
                        var y = Math.Clamp(box.Top + (int)(v * sourceHeight), box.Top, box.Bottom - 1);
                        if (Matches(frame.Bgra[y * frame.Width + x], kind)) hits++;
                    }
                glyph[oy * GlyphW + ox] = hits / 16.0f;
            }
        return glyph;
    }

    private static double Difference(float[] a, float[] b)
    {
        double difference = 0, mass = 0;
        for (var i = 0; i < a.Length; i++)
        {
            difference += Math.Abs(a[i] - b[i]);
            mass += Math.Max(a[i], b[i]);
        }
        return mass > 0 ? difference / mass : 1.0;
    }

    private static int RedPixels(HackFrame frame, Box box)
    {
        var count = 0;
        for (var y = box.Top; y < box.Bottom; y++)
            for (var x = box.Left; x < box.Right; x++)
                if (Red(frame.Bgra[y * frame.Width + x])) count++;
        return count;
    }

    private static Analysis Analyze(HackFrame frame, Geometry geometry)
    {
        var result = new Analysis { CurrentStart = -1, TargetStart = -1 };
        var rows = FindGridRows(frame, geometry.GridRoi);
        if (rows is null) return result;
        var minimumHeight = Math.Max(15, Round(frame.Height * .028));
        var target = Components(frame, geometry.TargetRoi, minimumHeight, minimumHeight * 2, PixelKind.RedOnly);
        var maximumHeight = Round(frame.Height * .068);
        target.RemoveAll(box => box.Bottom - box.Top > maximumHeight);
        target.Sort((a, b) => a.Left.CompareTo(b.Left));
        if (target.Count != 8) return result;
        var targetGlyphs = new float[8][];
        for (var i = 0; i < 8; i++) targetGlyphs[i] = Normalize(frame, target[i], PixelKind.RedOnly);

        var grid = new float[80][][];
        var active = new List<int>();
        for (var row = 0; row < 8; row++)
            for (var column = 0; column < 10; column++)
            {
                var index = row * 10 + column;
                Box first = rows[row][column * 2], second = rows[row][column * 2 + 1];
                grid[index] = [Normalize(frame, first, PixelKind.AllInk), Normalize(frame, second, PixelKind.AllInk)];
                var inkPixels = first.Pixels + second.Pixels;
                var redPixels = RedPixels(frame, first) + RedPixels(frame, second);
                if (redPixels >= Math.Max(10, (int)(inkPixels * .22))) active.Add(index);
            }
        // 选区是连续的 4 组
        var consecutive = active.Count == 4;
        for (var i = 1; i < active.Count; i++) consecutive = consecutive && active[i] == active[0] + i;
        if (!consecutive) return result;
        result.CurrentStart = active[0];

        double bestMean = 1, bestWorst = 1, secondMean = 1;
        for (var start = 0; start <= 76; start++)
        {
            double sum = 0, worst = 0;
            for (var offset = 0; offset < 4; offset++)
                for (var digit = 0; digit < 2; digit++)
                {
                    var difference = Difference(targetGlyphs[offset * 2 + digit], grid[start + offset][digit]);
                    sum += difference;
                    worst = Math.Max(worst, difference);
                }
            var mean = sum / 8;
            if (mean < bestMean)
            {
                secondMean = bestMean; bestMean = mean; bestWorst = worst; result.TargetStart = start;
            }
            else if (mean < secondMean) secondMean = mean;
        }
        // 最像的位置要足够像，而且要明显好过第二像的
        result.Readable = result.TargetStart >= 0 && bestMean <= .30 && bestWorst <= .55 && secondMean - bestMean >= .07;
        result.Match = result.Readable && result.CurrentStart == result.TargetStart;
        return result;
    }

    /// <summary>从 start 走到 target 的最短按键序列：上下一次移一行（10 组），左右一次移一组。</summary>
    public static List<int> PlanDirections(int start, int target)
    {
        const int lastStart = 76;
        var keys = new List<int>();
        if (start < 0 || start > lastStart || target < 0 || target > lastStart) return keys;
        var previous = new int[lastStart + 1];
        var previousKey = new int[lastStart + 1];
        Array.Fill(previous, -2);
        previous[start] = -1;
        var pending = new Queue<int>();
        pending.Enqueue(start);
        (int delta, int key)[] moves = [(-10, 0x26), (10, 0x28), (-1, 0x25), (1, 0x27)];
        while (pending.Count > 0 && previous[target] == -2)
        {
            var current = pending.Dequeue();
            foreach (var (delta, key) in moves)
            {
                var next = current + delta;
                if (next < 0 || next > lastStart || previous[next] != -2) continue;
                previous[next] = current; previousKey[next] = key; pending.Enqueue(next);
            }
        }
        if (previous[target] == -2) return keys;
        for (var at = target; at != start; at = previous[at]) keys.Add(previousKey[at]);
        keys.Reverse();
        return keys;
    }

    private static bool GeometryMatchesFrame(Geometry geometry, HackFrame frame) =>
        geometry.WindowGeneration == frame.WindowGeneration && geometry.FrameWidth == frame.Width && geometry.FrameHeight == frame.Height;

    public bool DetectInGame()
    {
        var frame = host.Capture();
        _detected = frame is null ? null : LocateGeometry(frame);
        return _detected is not null;
    }

    public void ResetInGameCache() => _detected = null;

    /// <summary>运行到骇入完成（返回 true）、小游戏消失或被要求停止（返回 false）。</summary>
    public bool RunSession(Func<bool> stopRequested, Action<string> status)
    {
        var geometry = _detected;
        HackJob? inputJob = null;
        var submitting = false;
        var absentFrames = 0;
        long analyzeAfter = 0;
        var lastStatus = "";
        void SetStatus(string value)
        {
            if (lastStatus == value) return;
            lastStatus = value;
            status(value);
        }
        void Cleanup()
        {
            host.CancelAll();
            ResetInGameCache();
        }

        SetStatus("find_number: locating");
        while (!stopRequested())
        {
            var frameStarted = Now;
            var frame = host.Capture();
            if (frame is null)
            {
                geometry = null;
                _detected = null;
                if (++absentFrames >= 3) break;
                WaitForFrame(stopRequested, frameStarted);
                continue;
            }
            if (geometry is not null && (!GeometryMatchesFrame(geometry, frame) || !ValidateBar(frame, geometry.Bar)))
            {
                geometry = null;
                _detected = null;
            }
            if (geometry is null)
            {
                geometry = LocateGeometry(frame);
                if (geometry is null)
                {
                    if (++absentFrames >= 3)
                    {
                        SetStatus("find_number: minigame exited");
                        break;
                    }
                    WaitForFrame(stopRequested, frameStarted);
                    continue;
                }
                _detected = geometry;
            }
            absentFrames = 0;

            if (inputJob is not null)
            {
                if (inputJob.Pending)
                {
                    WaitForFrame(stopRequested, frameStarted);
                    continue;
                }
                if (!inputJob.Succeeded)
                {
                    inputJob = null;
                    submitting = false;
                    analyzeAfter = Now + Ms(80);
                    SetStatus("find_number: analyzing");
                    WaitForFrame(stopRequested, frameStarted);
                    continue;
                }
                inputJob = null;
                if (submitting)
                {
                    SetStatus("find_number: completed");
                    Cleanup();
                    return true;
                }
                analyzeAfter = Now + Ms(100);
                SetStatus("find_number: verifying position");
                WaitForFrame(stopRequested, frameStarted);
                continue;
            }
            if (Now < analyzeAfter)
            {
                WaitForFrame(stopRequested, frameStarted);
                continue;
            }
            SetStatus("find_number: analyzing");
            var result = Analyze(frame, geometry);
            if (!result.Readable)
            {
                WaitForFrame(stopRequested, frameStarted);
                continue;
            }
            var foreground = host.ForegroundGameWindow(frame);
            if (foreground == 0)
            {
                WaitForFrame(stopRequested, frameStarted);
                continue;
            }
            var keys = new List<HackKey>();
            if (result.Match)
            {
                keys.Add(new HackKey(0x0D));
                submitting = true;
                SetStatus("find_number: submitting");
            }
            else
            {
                foreach (var key in PlanDirections(result.CurrentStart, result.TargetStart)) keys.Add(new HackKey(key));
                if (keys.Count == 0)
                {
                    WaitForFrame(stopRequested, frameStarted);
                    continue;
                }
                submitting = false;
                SetStatus("find_number: moving");
            }
            inputJob = host.QueueSequence(keys, foreground);
            WaitForFrame(stopRequested, frameStarted);
        }
        Cleanup();
        return false;
    }
}
