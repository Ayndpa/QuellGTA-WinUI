using System.Diagnostics;
using static QuellGTA.Services.AutoHack.HackUtil;

namespace QuellGTA.Services.AutoHack;

/// <summary>
/// 「弗利萨」骇入（games/fleeca_module.cpp）：电路板迷宫，左右（或上下）各有一个灰色接头。
/// 按回车放出绿色信号后，信号沿当前方向一直前进，要在合适的时刻按方向键转弯，避开绿色的电路走到另一个接头。
/// 做法：先在画面上找到两个接头、把电路画成栅格地图，用 A* 规划一条转弯尽量少、尽量走在通道中间的路线；
/// 然后每帧追踪信号头的位置，按它的速度和测出来的输入延迟提前按下下一个方向键。
/// 一局结束后识别结果画面，按回车进入下一局，直到不再出现新的一局。
/// </summary>
public sealed class FleecaHack(IHackHost host) : IHackGame
{
    public string Name => "弗利萨";

    private readonly record struct Point(int X, int Y);
    private readonly record struct Box(int X, int Y, int W, int H, int Area);
    private readonly record struct Connectors(Point First, Point Second, Box FirstBox, Box SecondBox);
    private readonly record struct SearchRegion(int X1, int Y1, int X2, int Y2);
    private sealed record Run(int Dx, int Dy, double Distance, Point Target, SearchRegion Search)
    {
        public double Distance { get; set; } = Distance;
        public Point Target { get; set; } = Target;
        public SearchRegion Search { get; set; } = Search;
    }

    private sealed class Map
    {
        public int X1, Y1, X2, Y2, Width, Height, Grid;
        public bool[] Blocked = [];
        public Point Start, Exit;
        public int StartDirection = 4, ExitDirection = 4;
        public int MinimumStartRun, MinimumExitRun;
        public double SignalHeadRadius, ConnectorLongSide;
    }

    private const int ReferenceGameHeight = 1080;
    private const int ReferenceGrid = 4;
    private const double SignalRadiusToConnectorLong = 0.105;
    private const double WallClearanceToConnectorShort = 0.16;
    private const double SignalCoreToHeadRadius = 0.72;
    private const double SignalCoreMinimumFillRatio = 0.78;
    private const double SignalCoreQuadrantMinimumFillRatio = 0.64;
    private const double SignalOuterProbeToHeadRadius = 1.30;
    private const double SignalMaxFrameTravelToConnectorLong = 0.45;
    private const double AnalysisIntervalSeconds = 1.0 / 30.0;
    private const double TurnMotionToConnectorLong = 0.012;
    private const double LatencySampleMaxSeconds = 0.500;
    private const int HeadLostConsecutiveFrames = 30;
    private const double WallCenteringWeight = 8.0;
    private const double RouteTurnCost = 20.0;
    private const int KeyEnter = 0x0D, KeyLeft = 0x25, KeyUp = 0x26, KeyRight = 0x27, KeyDown = 0x28;

    private const int SessionIngameLost = 2;
    private const int SessionGeometryChanged = 3;
    private const int IngameLostConsecutiveFrames = 3;
    private const int ResultScreenTimeoutMs = 2000;
    private const int ResultEnterDelayMs = 2000;
    private const int NextRoundTimeoutMs = 2000;

    // 识别阶段缓存的接头位置（g_detected_geometry）
    private bool _detectedValid;
    private ulong _detectedGeneration;
    private int _detectedWidth, _detectedHeight;
    private Connectors _detectedConnectors;

    private Func<bool> _stop = () => false;
    private Action<string> _status = _ => { };
    private bool _completedRound;
    private readonly LatencyEstimator _inputLatency = new();

    private bool StopRequested() => _stop();
    private void SetStatus(string value) => _status(value);

    // ───────── 计时 ─────────

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    /// <summary>
    /// 精确等待：原版用 sleep_for / sleep_until。Windows 的 Sleep 精度只有一个时钟周期（可能是 15.6 毫秒），
    /// 转弯时机差一帧就会撞墙，所以这里先粗睡，最后 2 毫秒改为让出时间片等待。
    /// </summary>
    private static void SleepUntil(long deadlineTicks)
    {
        while (true)
        {
            var remaining = (deadlineTicks - Now) * 1000.0 / Stopwatch.Frequency;
            if (remaining <= 0) return;
            if (remaining > 3) Thread.Sleep((int)(remaining - 2));
            else Thread.Yield();
        }
    }

    private static void WaitPrecisely(double seconds)
    {
        if (seconds > 0.0) SleepUntil(Now + (long)(seconds * Stopwatch.Frequency));
    }

    /// <summary>信号沿当前方向的移动速度（像素 / 秒），平滑处理。</summary>
    private sealed class SpeedTracker(Point initial)
    {
        private Point _anchor = initial;
        private long _anchorTime = Now;
        private double _smoothed;
        private bool _hasSpeed;

        public double PixelsPerSecond => _hasSpeed ? _smoothed : 0.0;

        public void BeginSegment(Point current)
        {
            _anchor = current;
            _anchorTime = Now;
        }

        public void Observe(Point current, int axisX, int axisY)
        {
            var now = Now;
            var elapsed = Seconds(now - _anchorTime);
            if (elapsed < 0.080) return;
            var travelled = Math.Abs((double)(current.X - _anchor.X) * axisX + (double)(current.Y - _anchor.Y) * axisY);
            var measured = travelled / elapsed;
            _smoothed = _hasSpeed ? _smoothed * 0.65 + measured * 0.35 : measured;
            _hasSpeed = true;
            _anchor = current;
            _anchorTime = now;
        }
    }

    /// <summary>从「按下方向键」到「画面上信号开始转弯」的延迟，取最近 7 次的中位数再平滑。</summary>
    private sealed class LatencyEstimator
    {
        private readonly List<double> _samples = [];
        private double _seconds;
        private bool _calibrated;

        public void Reset()
        {
            _samples.Clear();
            _seconds = 0.0;
            _calibrated = false;
        }

        public bool Observe(double sampleSeconds)
        {
            if (sampleSeconds <= 0.0 || sampleSeconds > LatencySampleMaxSeconds) return false;
            _samples.Add(sampleSeconds);
            if (_samples.Count > 7) _samples.RemoveAt(0);
            var ordered = _samples.OrderBy(v => v).ToList();
            var median = ordered[ordered.Count / 2];
            _seconds = _calibrated ? _seconds * 0.65 + median * 0.35 : median;
            _calibrated = true;
            return true;
        }

        public double PredictionSeconds(double fallbackSeconds) => _calibrated ? _seconds : fallbackSeconds;
    }

    // ───────── 像素 ─────────

    private static int B(HackFrame f, int x, int y) => (int)(f.Bgra[y * f.Width + x] & 255);
    private static int G(HackFrame f, int x, int y) => (int)((f.Bgra[y * f.Width + x] >> 8) & 255);
    private static int R(HackFrame f, int x, int y) => (int)((f.Bgra[y * f.Width + x] >> 16) & 255);

    private static double Distance(Point a, Point b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>与 OpenCV 相同的 HSV（色相 0–179）。</summary>
    private static void RgbToHsv(int b, int g, int r, out int hue, out int sat, out int value)
    {
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var delta = maximum - minimum;
        value = maximum;
        sat = maximum == 0 ? 0 : 255 * delta / maximum;
        if (delta == 0) { hue = 0; return; }
        var h = r == maximum ? 60.0 * ((double)(g - b) / delta)
              : g == maximum ? 60.0 * (2.0 + (double)(b - r) / delta)
                             : 60.0 * (4.0 + (double)(r - g) / delta);
        if (h < 0) h += 360.0;
        hue = Round(h / 2.0);
    }

    // ───────── 接头 ─────────

    private static Point ConnectorAxisCenter(Box box) => new(box.X + box.W / 2, box.Y + box.H / 2);

    /// <summary>接头是深灰和浅灰相间的纹理。</summary>
    private static bool HasConnectorTexture(HackFrame image, Box box)
    {
        int darkGray = 0, lightGray = 0;
        var pixels = box.W * box.H;
        for (var y = box.Y; y < box.Y + box.H; y++)
            for (var x = box.X; x < box.X + box.W; x++)
            {
                int b = B(image, x, y), g = G(image, x, y), r = R(image, x, y);
                int low = Math.Min(b, Math.Min(g, r)), high = Math.Max(b, Math.Max(g, r));
                if (high - low > 18) continue;
                if (high is >= 55 and <= 105) darkGray++;
                if (high is >= 130 and <= 190) lightGray++;
            }
        return darkGray * 5 >= pixels && lightGray * 12 >= pixels;
    }

    private static bool IsConnectorCandidate(HackFrame image, Box box)
    {
        var longSide = Math.Max(box.W, box.H);
        var shortSide = Math.Min(box.W, box.H);
        var longToHeight = (double)longSide / image.Height;
        var shortToHeight = (double)shortSide / image.Height;
        var aspect = (double)longSide / shortSide;
        var fill = (double)box.Area / (box.W * box.H);
        var center = ConnectorAxisCenter(box);
        var nearOuterEdge = center.X <= image.Width * 0.24 || center.X >= image.Width * 0.76 ||
                            center.Y <= image.Height * 0.24 || center.Y >= image.Height * 0.76;
        return longToHeight is >= 0.065 and <= 0.095 && shortToHeight is >= 0.040 and <= 0.065 &&
               aspect is >= 1.30 and <= 1.75 && fill is >= 0.85 and <= 0.995 && nearOuterEdge &&
               HasConnectorTexture(image, box);
    }

    /// <summary>接头朝向棋盘中心的那一侧开口处。</summary>
    private static Point ConnectorMouth(Box box, HackFrame image)
    {
        var center = ConnectorAxisCenter(box);
        var board = new Point(image.Width / 2, image.Height / 2);
        var offset = Math.Max(10, Round(Math.Max(box.W, box.H) * 0.14));
        if (box.H >= box.W) return new Point(center.X + (board.X > center.X ? offset : -offset), center.Y);
        return new Point(center.X, center.Y + (board.Y > center.Y ? offset : -offset));
    }

    private static readonly int[] Nx = [-1, 0, 1, -1, 1, -1, 0, 1];
    private static readonly int[] Ny = [-1, -1, -1, 0, 0, 1, 1, 1];

    private static Connectors? FindConnectors(HackFrame image)
    {
        var total = image.Width * image.Height;
        var mask = new bool[total];
        var seen = new bool[total];
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                int b = B(image, x, y), g = G(image, x, y), r = R(image, x, y);
                int low = Math.Min(b, Math.Min(g, r)), high = Math.Max(b, Math.Max(g, r));
                mask[y * image.Width + x] = high - low <= 18 && high >= 55 && high <= 190;
            }
        var boxes = new List<Box>();
        var todo = new Queue<int>();
        for (var start = 0; start < total; start++)
        {
            if (!mask[start] || seen[start]) continue;
            todo.Clear();
            todo.Enqueue(start);
            seen[start] = true;
            int area = 0, x1 = image.Width, y1 = image.Height, x2 = 0, y2 = 0;
            while (todo.Count > 0)
            {
                var at = todo.Dequeue();
                int x = at % image.Width, y = at / image.Width;
                area++;
                x1 = Math.Min(x1, x); y1 = Math.Min(y1, y); x2 = Math.Max(x2, x); y2 = Math.Max(y2, y);
                for (var i = 0; i < 8; i++)
                {
                    int nx = x + Nx[i], ny = y + Ny[i];
                    if (nx < 0 || ny < 0 || nx >= image.Width || ny >= image.Height) continue;
                    var next = ny * image.Width + nx;
                    if (mask[next] && !seen[next]) { seen[next] = true; todo.Enqueue(next); }
                }
            }
            var box = new Box(x1, y1, x2 - x1 + 1, y2 - y1 + 1, area);
            if (area >= total * 0.001 && area <= total * 0.008 && IsConnectorCandidate(image, box)) boxes.Add(box);
        }
        double bestScore = -1;
        Connectors? best = null;
        for (var a = 0; a < boxes.Count; a++)
            for (var b = a + 1; b < boxes.Count; b++)
            {
                Point first = ConnectorAxisCenter(boxes[a]), second = ConnectorAxisCenter(boxes[b]);
                var separation = Distance(first, second);
                int firstLong = Math.Max(boxes[a].W, boxes[a].H), secondLong = Math.Max(boxes[b].W, boxes[b].H);
                int firstShort = Math.Min(boxes[a].W, boxes[a].H), secondShort = Math.Min(boxes[b].W, boxes[b].H);
                if (separation < Math.Min(image.Width, image.Height) * 0.50 ||
                    Math.Min(firstLong, secondLong) * 100 < Math.Max(firstLong, secondLong) * 85 ||
                    Math.Min(firstShort, secondShort) * 100 < Math.Max(firstShort, secondShort) * 85 ||
                    Math.Min(boxes[a].Area, boxes[b].Area) * 100 < Math.Max(boxes[a].Area, boxes[b].Area) * 85) continue;
                var score = Math.Min(boxes[a].Area, boxes[b].Area) * separation;
                if (score > bestScore) { bestScore = score; best = new Connectors(first, second, boxes[a], boxes[b]); }
            }
        return best;
    }

    private static bool ValidateConnectors(HackFrame image, Connectors connectors)
    {
        bool ValidBox(Box box) =>
            box.X >= 0 && box.Y >= 0 && box.W > 0 && box.H > 0 && box.X + box.W <= image.Width && box.Y + box.H <= image.Height &&
            IsConnectorCandidate(image, box);
        return ValidBox(connectors.FirstBox) && ValidBox(connectors.SecondBox);
    }

    // ───────── 地图 ─────────

    /// <summary>电路（墙）是暗绿色。</summary>
    private static bool IsWall(HackFrame image, int x, int y)
    {
        int b = B(image, x, y), g = G(image, x, y), r = R(image, x, y);
        RgbToHsv(b, g, r, out var hue, out var sat, out var value);
        return g - r >= 39 && hue is >= 65 and <= 95 && sat >= 175 && value is >= 45 and <= 125;
    }

    private static Map? BuildMap(HackFrame image, Connectors connectors)
    {
        var first = ConnectorMouth(connectors.FirstBox, image);
        var second = ConnectorMouth(connectors.SecondBox, image);
        var total = image.Width * image.Height;
        var wallMask = new bool[total];
        var regionIds = new int[total];
        Array.Fill(regionIds, -1);
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                wallMask[y * image.Width + x] = IsWall(image, x, y);

        // 电路板是几大块连通区域；文字和背景图案是零散的小块，不能让它们把地图范围撑大
        var regions = new List<Box>();
        var todo = new Queue<int>();
        for (var start = 0; start < total; start++)
        {
            if (!wallMask[start] || regionIds[start] >= 0) continue;
            var regionId = regions.Count;
            todo.Clear();
            todo.Enqueue(start);
            regionIds[start] = regionId;
            int area = 0, left = image.Width, top = image.Height, right = 0, bottom = 0;
            while (todo.Count > 0)
            {
                var at = todo.Dequeue();
                int x = at % image.Width, y = at / image.Width;
                area++;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                for (var i = 0; i < 8; i++)
                {
                    int nx = x + Nx[i], ny = y + Ny[i];
                    if (nx < 0 || ny < 0 || nx >= image.Width || ny >= image.Height) continue;
                    var next = ny * image.Width + nx;
                    if (wallMask[next] && regionIds[next] < 0) { regionIds[next] = regionId; todo.Enqueue(next); }
                }
            }
            regions.Add(new Box(left, top, right - left + 1, bottom - top + 1, area));
        }
        if (regions.Count == 0) return null;
        var largestArea = regions.Max(r => r.Area);
        var minimumWallArea = Math.Max(50, largestArea / 100);
        var minimumBorderArea = Math.Max(100, largestArea / 50);
        for (var i = 0; i < total; i++)
            if (wallMask[i] && regions[regionIds[i]].Area < minimumWallArea) wallMask[i] = false;
        int x1 = image.Width, y1 = image.Height, x2 = 0, y2 = 0;
        foreach (var region in regions)
        {
            if (region.Area < minimumBorderArea) continue;
            x1 = Math.Min(x1, region.X); y1 = Math.Min(y1, region.Y);
            x2 = Math.Max(x2, region.X + region.W - 1);
            y2 = Math.Max(y2, region.Y + region.H - 1);
        }
        x1 = Math.Min(x1, Math.Min(first.X, second.X)); y1 = Math.Min(y1, Math.Min(first.Y, second.Y));
        x2 = Math.Max(x2, Math.Max(first.X, second.X)); y2 = Math.Max(y2, Math.Max(first.Y, second.Y));
        if (x2 <= x1 || y2 <= y1) return null;
        x2 = Math.Min(image.Width, x2 + 1);
        y2 = Math.Min(image.Height, y2 + 1);
        var grid = Math.Min(ReferenceGrid, Math.Max(2, Round((double)image.Height * ReferenceGrid / ReferenceGameHeight)));
        var width = (x2 - x1 + grid - 1) / grid;
        var height = (y2 - y1 + grid - 1) / grid;
        var connectorShort = (Math.Min(connectors.FirstBox.W, connectors.FirstBox.H) + Math.Min(connectors.SecondBox.W, connectors.SecondBox.H)) / 2.0;
        var connectorLong = (Math.Max(connectors.FirstBox.W, connectors.FirstBox.H) + Math.Max(connectors.SecondBox.W, connectors.SecondBox.H)) / 2.0;
        var map = new Map
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Width = width, Height = height, Grid = grid, Blocked = new bool[width * height],
            Start = first, Exit = second, SignalHeadRadius = connectorLong * SignalRadiusToConnectorLong, ConnectorLongSide = connectorLong,
        };
        var minimumHits = Math.Max(1, (int)Math.Ceiling(grid * grid * 0.20));
        for (var gy = 0; gy < height; gy++)
            for (var gx = 0; gx < width; gx++)
            {
                var hits = 0;
                for (var y = y1 + gy * grid; y < Math.Min(y1 + (gy + 1) * grid, y2); y++)
                    for (var x = x1 + gx * grid; x < Math.Min(x1 + (gx + 1) * grid, x2); x++)
                        if (wallMask[y * image.Width + x]) hits++;
                map.Blocked[gy * width + gx] = hits >= minimumHits;
            }
        for (var x = 0; x < width; x++)
        {
            map.Blocked[x] = true;
            map.Blocked[(height - 1) * width + x] = true;
        }
        for (var y = 0; y < height; y++)
        {
            map.Blocked[y * width] = true;
            map.Blocked[y * width + width - 1] = true;
        }
        // 墙往外加厚一圈留出余量；两个接头口清空
        void ClearOrInflate(bool clear, Point point)
        {
            var clearance = (int)Math.Ceiling(connectorShort * WallClearanceToConnectorShort);
            int cx = (point.X - x1) / grid, cy = (point.Y - y1) / grid;
            var radius = (clearance + grid - 1) / grid;
            for (var y = Math.Max(0, cy - radius); y <= Math.Min(height - 1, cy + radius); y++)
                for (var x = Math.Max(0, cx - radius); x <= Math.Min(width - 1, cx + radius); x++)
                    map.Blocked[y * width + x] = !clear;
        }
        var original = (bool[])map.Blocked.Clone();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (original[y * width + x]) ClearOrInflate(false, new Point(x1 + x * grid, y1 + y * grid));
        ClearOrInflate(true, first);
        ClearOrInflate(true, second);
        static int InwardDirection(Point mouth, Box box)
        {
            var center = ConnectorAxisCenter(box);
            int dx = mouth.X - center.X, dy = mouth.Y - center.Y;
            return Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? 0 : 1) : (dy >= 0 ? 2 : 3);
        }
        map.StartDirection = InwardDirection(first, connectors.FirstBox);
        map.ExitDirection = InwardDirection(second, connectors.SecondBox);
        map.MinimumStartRun = Math.Min(connectors.FirstBox.W, connectors.FirstBox.H);
        map.MinimumExitRun = Math.Min(connectors.SecondBox.W, connectors.SecondBox.H);
        return map;
    }

    // ───────── 信号头与结果画面 ─────────

    /// <summary>信号头：亮绿色、高饱和度。</summary>
    private static bool IsSignalHeadPixel(int blue, int green, int red)
    {
        if (green < 185 || green < blue || green < red) return false;
        var delta = green - Math.Min(red, blue);
        // 整数形式的「饱和度 ≥ 130 且色相 ≥ 65」（OpenCV 0–179 刻度）
        return 255 * delta >= 130 * green && 60 * (blue - red) >= 9 * delta;
    }

    private static bool IsResultPixel(int blue, int green, int red)
    {
        var successGreen = green >= 150 && green - red >= 55 && green - blue >= 25;
        var failureRed = red >= 150 && red - green >= 55 && red - blue >= 25;
        return successGreen || failureRed;
    }

    /// <summary>结果画面：屏幕中间一排三个红色或绿色的方框。</summary>
    private static bool DetectResultScreen(HackFrame image)
    {
        if (image.Height <= 0 || image.Width <= 0) return false;
        var tolerance = Math.Max(1, Round(image.Height * 0.002));
        var search = Math.Max(2, Round(image.Height * 0.012));
        var expectedY = Round(image.Height * 0.450);
        var spacing = Round(image.Height * 0.072);
        var minimumSize = Math.Max(8, Round(image.Height * 0.047));
        var maximumSize = Math.Max(minimumSize, Round(image.Height * 0.057));
        bool Bright(int x, int y) =>
            x >= 0 && y >= 0 && x < image.Width && y < image.Height && IsResultPixel(B(image, x, y), G(image, x, y), R(image, x, y));
        double VerticalEdgeScore(int x, int y1, int y2)
        {
            int hits = 0, samples = 0;
            for (var y = y1; y <= y2; y += 2)
            {
                samples++;
                var found = false;
                for (var dx = -tolerance; dx <= tolerance && !found; dx++) found = Bright(x + dx, y);
                if (found) hits++;
            }
            return samples > 0 ? (double)hits / samples : 0.0;
        }
        double HorizontalEdgeScore(int y, int x1, int x2)
        {
            int hits = 0, samples = 0;
            for (var x = x1; x <= x2; x += 2)
            {
                samples++;
                var found = false;
                for (var dy = -tolerance; dy <= tolerance && !found; dy++) found = Bright(x, y + dy);
                if (found) hits++;
            }
            return samples > 0 ? (double)hits / samples : 0.0;
        }
        bool SquareNear(int expectedX)
        {
            for (var size = minimumSize; size <= maximumSize; size += 2)
            {
                var half = size / 2;
                for (var cy = expectedY - search; cy <= expectedY + search; cy += 2)
                    for (var cx = expectedX - search; cx <= expectedX + search; cx += 2)
                    {
                        int left = cx - half, right = cx + half, top = cy - half, bottom = cy + half;
                        if (VerticalEdgeScore(left, top, bottom) >= 0.62 && VerticalEdgeScore(right, top, bottom) >= 0.62 &&
                            HorizontalEdgeScore(top, left, right) >= 0.62 && HorizontalEdgeScore(bottom, left, right) >= 0.62) return true;
                    }
            }
            return false;
        }
        var centerX = image.Width / 2;
        return SquareNear(centerX - spacing) && SquareNear(centerX) && SquareNear(centerX + spacing);
    }

    /// <summary>
    /// 找信号头：在地图（或指定的搜索区域）里找一个实心的绿色圆点，要求中心饱满、四个象限都有、外圈大部分是暗的。
    /// 给了 previous 时，离它太远的候选会被排除。
    /// </summary>
    private static Point? DetectHead(HackFrame image, Map map, Point? previous, ref double observedRadius, SearchRegion? searchRegion = null)
    {
        var radius = Math.Max(1, Round(map.SignalHeadRadius));
        var coreRadius = Math.Max(1, Round(map.SignalHeadRadius * SignalCoreToHeadRadius));
        var outerProbe = Math.Max(radius + 1, Round(map.SignalHeadRadius * SignalOuterProbeToHeadRadius));
        var left = Math.Max(map.X1 + outerProbe, searchRegion?.X1 ?? map.X1 + outerProbe);
        var top = Math.Max(map.Y1 + outerProbe, searchRegion?.Y1 ?? map.Y1 + outerProbe);
        var right = Math.Min(map.X2 - outerProbe - 1, searchRegion is { } r1 ? r1.X2 - 1 : map.X2 - outerProbe - 1);
        var bottom = Math.Min(map.Y2 - outerProbe - 1, searchRegion is { } r2 ? r2.Y2 - 1 : map.Y2 - outerProbe - 1);
        if (left > right || top > bottom) return null;
        int maskLeft = left - outerProbe, maskTop = top - outerProbe, maskRight = right + outerProbe, maskBottom = bottom + outerProbe;
        int maskWidth = maskRight - maskLeft + 1, maskHeight = maskBottom - maskTop + 1;
        var signalMask = new bool[maskWidth * maskHeight];
        var signalPixels = new List<Point>();
        for (var y = maskTop; y <= maskBottom; y++)
            for (var x = maskLeft; x <= maskRight; x++)
            {
                var signal = IsSignalHeadPixel(B(image, x, y), G(image, x, y), R(image, x, y));
                signalMask[(y - maskTop) * maskWidth + x - maskLeft] = signal;
                if (signal && x >= left && x <= right && y >= top && y <= bottom) signalPixels.Add(new Point(x, y));
            }
        bool SignalAt(int x, int y) => signalMask[(y - maskTop) * maskWidth + x - maskLeft];

        Point bestCenter = default;
        double bestScore = -1.0;
        var bestHeadPixels = 0;
        var coreRadiusSquared = coreRadius * coreRadius;
        var headRadiusSquared = radius * radius;
        var maximumTravel = map.ConnectorLongSide * SignalMaxFrameTravelToConnectorLong;
        Span<int> quadrantTotal = stackalloc int[4], quadrantHits = stackalloc int[4];
        foreach (var point in signalPixels)
        {
            int x = point.X, y = point.Y;
            if (!SignalAt(x, y)) continue;
            if (previous is { } prev && Distance(point, prev) > maximumTravel) continue;
            int headTotal = 0, headHits = 0, coreTotal = 0, coreHits = 0;
            quadrantTotal.Clear();
            quadrantHits.Clear();
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var squaredDistance = dx * dx + dy * dy;
                    if (squaredDistance > headRadiusSquared) continue;
                    var signal = SignalAt(x + dx, y + dy);
                    headTotal++;
                    if (signal) headHits++;
                    if (squaredDistance > coreRadiusSquared) continue;
                    coreTotal++;
                    if (signal) coreHits++;
                    if (dx == 0 || dy == 0) continue;
                    var quadrant = (dy > 0 ? 2 : 0) + (dx > 0 ? 1 : 0);
                    quadrantTotal[quadrant]++;
                    if (signal) quadrantHits[quadrant]++;
                }
            var coreFill = coreTotal > 0 ? (double)coreHits / coreTotal : 0.0;
            if (coreFill < SignalCoreMinimumFillRatio) continue;
            var weakestQuadrant = 1.0;
            for (var q = 0; q < 4; q++)
                weakestQuadrant = Math.Min(weakestQuadrant, quadrantTotal[q] > 0 ? (double)quadrantHits[q] / quadrantTotal[q] : 0.0);
            if (weakestQuadrant < SignalCoreQuadrantMinimumFillRatio) continue;
            var darkOuterProbes = 0;
            if (!SignalAt(x - outerProbe, y)) darkOuterProbes++;
            if (!SignalAt(x + outerProbe, y)) darkOuterProbes++;
            if (!SignalAt(x, y - outerProbe)) darkOuterProbes++;
            if (!SignalAt(x, y + outerProbe)) darkOuterProbes++;
            if (darkOuterProbes < 3) continue;
            var headFill = headTotal > 0 ? (double)headHits / headTotal : 0.0;
            var shapeScore = headFill + coreFill + weakestQuadrant + darkOuterProbes * 0.05;
            var betterShape = shapeScore > bestScore;
            var equalShape = Math.Abs(shapeScore - bestScore) < 1e-9;
            var nearer = previous is { } p && equalShape && Distance(point, p) < Distance(bestCenter, p);
            if (betterShape || nearer)
            {
                bestCenter = point;
                bestScore = shapeScore;
                bestHeadPixels = headHits;
            }
        }
        if (bestScore < 0.0) return null;
        observedRadius = Math.Sqrt(bestHeadPixels / Math.PI);
        return bestCenter;
    }

    // ───────── 路线 ─────────

    private static int Direction(Point a, Point b) =>
        Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y) ? (b.X >= a.X ? 0 : 1) : (b.Y >= a.Y ? 2 : 3);

    private static readonly int[] DeltaX = [1, -1, 0, 0];
    private static readonly int[] DeltaY = [0, 0, 1, -1];

    /// <summary>
    /// 规划路线（方向 0 右 1 左 2 下 3 上，4 表示无方向）。先沿接头的朝向强制直行一段，
    /// 再用 A* 走到出口：每走一格代价 1，离墙越近越贵（尽量走通道中间），每次转弯额外 20（尽量少转弯），不许掉头。
    /// 最后把路径合并成若干段直线。
    /// </summary>
    private static List<Run>? PlanPath(Map map, Point start, int initialDirection)
    {
        Point Cell(Point p) => new(Math.Clamp((p.X - map.X1) / map.Grid, 0, map.Width - 1), Math.Clamp((p.Y - map.Y1) / map.Grid, 0, map.Height - 1));
        Point PointForCell(Point p) => new(map.X1 + p.X * map.Grid + map.Grid / 2, map.Y1 + p.Y * map.Grid + map.Grid / 2);
        Point source = Cell(start), target = Cell(map.Exit), forcedStart = start, forcedTarget = map.Exit;
        if (initialDirection < 4 && map.MinimumStartRun > 0)
        {
            int fdx = DeltaX[initialDirection], fdy = DeltaY[initialDirection];
            var travelled = 0;
            while (travelled < map.MinimumStartRun)
            {
                source = new Point(source.X + fdx, source.Y + fdy);
                if (source.X < 0 || source.Y < 0 || source.X >= map.Width || source.Y >= map.Height || map.Blocked[source.Y * map.Width + source.X]) return null;
                forcedStart = PointForCell(source);
                travelled = (forcedStart.X - start.X) * fdx + (forcedStart.Y - start.Y) * fdy;
            }
        }
        if (map.ExitDirection < 4 && map.MinimumExitRun > 0)
        {
            int fdx = DeltaX[map.ExitDirection], fdy = DeltaY[map.ExitDirection];
            var travelled = 0;
            while (travelled < map.MinimumExitRun)
            {
                target = new Point(target.X + fdx, target.Y + fdy);
                if (target.X < 0 || target.Y < 0 || target.X >= map.Width || target.Y >= map.Height || map.Blocked[target.Y * map.Width + target.X]) return null;
                forcedTarget = PointForCell(target);
                travelled = (forcedTarget.X - map.Exit.X) * fdx + (forcedTarget.Y - map.Exit.Y) * fdy;
            }
        }

        // 到最近的墙的曼哈顿距离：在这种横平竖直的电路板上，它的脊线就是每条通道的中线
        var cells = map.Width * map.Height;
        var wallDistance = new int[cells];
        Array.Fill(wallDistance, int.MaxValue);
        var distanceOpen = new Queue<int>();
        for (var i = 0; i < cells; i++)
        {
            if (!map.Blocked[i]) continue;
            wallDistance[i] = 0;
            distanceOpen.Enqueue(i);
        }
        while (distanceOpen.Count > 0)
        {
            var at = distanceOpen.Dequeue();
            int x = at % map.Width, y = at / map.Width;
            for (var d = 0; d < 4; d++)
            {
                int nx = x + DeltaX[d], ny = y + DeltaY[d];
                if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height) continue;
                var next = ny * map.Width + nx;
                if (wallDistance[next] <= wallDistance[at] + 1) continue;
                wallDistance[next] = wallDistance[at] + 1;
                distanceOpen.Enqueue(next);
            }
        }

        var states = cells * 5;
        var cost = new double[states];
        Array.Fill(cost, double.PositiveInfinity);
        var parent = new int[states];
        Array.Fill(parent, -1);
        int Id(int x, int y, int d) => (y * map.Width + x) * 5 + d;
        var open = new PriorityQueue<(double cost, int x, int y, int dir), double>();
        var startId = Id(source.X, source.Y, initialDirection);
        cost[startId] = 0;
        open.Enqueue((0, source.X, source.Y, initialDirection), 0);
        var goal = -1;
        while (open.Count > 0)
        {
            var now = open.Dequeue();
            var nowId = Id(now.x, now.y, now.dir);
            if (now.cost != cost[nowId]) continue;
            if (now.x == target.X && now.y == target.Y && (map.ExitDirection >= 4 || now.dir == (map.ExitDirection ^ 1)))
            {
                goal = nowId;
                break;
            }
            for (var d = 0; d < 4; d++)
            {
                int nx = now.x + DeltaX[d], ny = now.y + DeltaY[d];
                if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height || map.Blocked[ny * map.Width + nx] || (now.dir < 4 && d == (now.dir ^ 1))) continue;
                var clearance = Math.Max(1, wallDistance[ny * map.Width + nx]);
                var wallCost = WallCenteringWeight / ((double)clearance * clearance);
                var next = now.cost + 1.0 + wallCost + (now.dir < 4 && d != now.dir ? RouteTurnCost : 0.0);
                var nextId = Id(nx, ny, d);
                if (next >= cost[nextId]) continue;
                cost[nextId] = next;
                parent[nextId] = nowId;
                open.Enqueue((next, nx, ny, d), next + Math.Abs(nx - target.X) + Math.Abs(ny - target.Y));
            }
        }
        if (goal < 0) return null;

        var points = new List<Point>();
        for (var at = goal; at >= 0; at = parent[at])
        {
            var cellId = at / 5;
            points.Add(new Point(map.X1 + cellId % map.Width * map.Grid + map.Grid / 2, map.Y1 + cellId / map.Width * map.Grid + map.Grid / 2));
            if (at == startId) break;
        }
        points.Reverse();
        points[0] = forcedStart;
        points[^1] = forcedTarget;
        if (forcedStart != start) points.Insert(0, start);
        if (forcedTarget != map.Exit) points.Add(map.Exit);

        bool ClearAxisSegment(Point a, Point b)
        {
            if (a.X != b.X && a.Y != b.Y) return false;
            Point firstCell = Cell(a), lastCell = Cell(b);
            var stepX = Math.Sign(lastCell.X - firstCell.X);
            var stepY = Math.Sign(lastCell.Y - firstCell.Y);
            var at = firstCell;
            while (true)
            {
                if (map.Blocked[at.Y * map.Width + at.X]) return false;
                if (at == lastCell) return true;
                at = new Point(at.X + stepX, at.Y + stepY);
            }
        }
        // 能直线走通就跳过中间的点
        var simplified = new List<Point>();
        for (var i = 0; i < points.Count;)
        {
            simplified.Add(points[i]);
            if (i + 1 == points.Count) break;
            var next = i + 1;
            for (var candidate = points.Count - 1; candidate > i + 1; candidate--)
                if (ClearAxisSegment(points[i], points[candidate])) { next = candidate; break; }
            i = next;
        }
        points = simplified;

        var runs = new List<Run>();
        for (var i = 1; i < points.Count; i++)
        {
            var d = Direction(points[i - 1], points[i]);
            int dx = DeltaX[d], dy = DeltaY[d];
            var segment = Distance(points[i - 1], points[i]);
            if (runs.Count > 0 && runs[^1].Dx == dx && runs[^1].Dy == dy)
            {
                runs[^1].Distance += segment;
                runs[^1].Target = points[i];
            }
            else runs.Add(new Run(dx, dy, segment, points[i], default));
        }
        var halfWidth = Math.Max(1, Round(map.ConnectorLongSide));
        var regionStart = start;
        foreach (var run in runs)
        {
            if (run.Dx != 0)
            {
                var centerY = (regionStart.Y + run.Target.Y) / 2;
                run.Search = new SearchRegion(Math.Min(regionStart.X, run.Target.X), centerY - halfWidth,
                    Math.Max(regionStart.X, run.Target.X) + 1, centerY + halfWidth + 1);
            }
            else
            {
                var centerX = (regionStart.X + run.Target.X) / 2;
                run.Search = new SearchRegion(centerX - halfWidth, Math.Min(regionStart.Y, run.Target.Y),
                    centerX + halfWidth + 1, Math.Max(regionStart.Y, run.Target.Y) + 1);
            }
            regionStart = run.Target;
        }
        return runs;
    }

    /// <summary>信号是从另一头出来的：把路线倒过来走。</summary>
    private static List<Run> ReverseRoute(Point originalStart, List<Run> runs)
    {
        var points = new List<Point>(runs.Count + 1) { originalStart };
        points.AddRange(runs.Select(r => r.Target));
        var reversed = new List<Run>(runs.Count);
        for (var i = runs.Count; i > 0; i--)
        {
            var original = runs[i - 1];
            reversed.Add(new Run(-original.Dx, -original.Dy, original.Distance, points[i - 1], original.Search));
        }
        return reversed;
    }

    private bool SendKey(int key)
    {
        host.QueueSequence([new HackKey(key)], 0);
        return true; // 原版的 send_key 只看任务对象是否存在，实际上总是成功
    }

    private static int KeycodeForDirection(int d) => d switch { 0 => KeyRight, 1 => KeyLeft, 2 => KeyDown, _ => KeyUp };

    // ───────── 一局 ─────────

    private HackFrame _frame = new();
    private Connectors _connectors;
    private ulong _geometryGeneration;

    private bool CaptureClient()
    {
        var frame = host.Capture();
        if (frame is null) return false;
        _frame = frame;
        return true;
    }

    private int RunIngameSession()
    {
        var missingIngameFrames = 0;
        long lastPresent = 0, previousPresent = 0;
        var displayFrameSeconds = AnalysisIntervalSeconds;
        var hasDisplayFrameSeconds = false;
        var analysisInterval = (long)(AnalysisIntervalSeconds * Stopwatch.Frequency);
        var nextAnalysisTime = Now;

        void WaitForAnalysisTick()
        {
            if (Now < nextAnalysisTime) SleepUntil(nextAnalysisTime);
            var now = Now;
            nextAnalysisTime += analysisInterval;
            if (nextAnalysisTime <= now) nextAnalysisTime = now + analysisInterval;
        }

        // 每 1/30 秒截一帧；返回 0 正常，1 截图失败，2 小游戏画面消失，3 接头位置变了
        int CaptureIngameFrame()
        {
            WaitForAnalysisTick();
            if (!CaptureClient())
            {
                ResetInGameCache();
                SetStatus("fleeca: capture failed");
                return 1;
            }
            var present = _frame.CaptureTicks;
            if (present > previousPresent && previousPresent > 0)
            {
                var measuredFrame = Seconds(present - previousPresent);
                if (measuredFrame is >= 0.002 and <= 0.100)
                {
                    displayFrameSeconds = hasDisplayFrameSeconds ? displayFrameSeconds * 0.65 + measuredFrame * 0.35 : measuredFrame;
                    hasDisplayFrameSeconds = true;
                }
            }
            if (present > 0) previousPresent = present;
            lastPresent = present;
            if (_frame.WindowGeneration == _geometryGeneration && ValidateConnectors(_frame, _connectors))
            {
                missingIngameFrames = 0;
                return 0;
            }
            var relocated = FindConnectors(_frame);
            if (relocated is { } found)
            {
                var geometryChanged = _frame.WindowGeneration != _geometryGeneration ||
                                      found.FirstBox.X != _connectors.FirstBox.X || found.FirstBox.Y != _connectors.FirstBox.Y ||
                                      found.SecondBox.X != _connectors.SecondBox.X || found.SecondBox.Y != _connectors.SecondBox.Y;
                _connectors = found;
                ResetInGameCache();
                _geometryGeneration = _frame.WindowGeneration;
                missingIngameFrames = 0;
                return geometryChanged ? SessionGeometryChanged : 0;
            }
            missingIngameFrames++;
            return missingIngameFrames >= IngameLostConsecutiveFrames ? SessionIngameLost : 0;
        }

        SetStatus("fleeca: planning route");
        var map = BuildMap(_frame, _connectors);
        if (map is null)
        {
            SetStatus("fleeca: route unavailable");
            return 1;
        }
        var runs = PlanPath(map, map.Start, map.StartDirection);
        if (runs is null || runs.Count == 0)
        {
            SetStatus("fleeca: route unavailable");
            return 1;
        }

        SetStatus("fleeca: starting signal");
        Point? head;
        var trackedHeadRadius = map.SignalHeadRadius;
        if (!SendKey(KeyEnter)) return 1;
        var deadline = Environment.TickCount64 + 2000;
        do
        {
            var frameStatus = CaptureIngameFrame();
            if (frameStatus != 0) return frameStatus;
            head = DetectHead(_frame, map, null, ref trackedHeadRadius);
        }
        while (head is null && !StopRequested() && Environment.TickCount64 < deadline);
        if (StopRequested()) return 1;
        if (head is null)
        {
            SetStatus("fleeca: signal not detected");
            return 1;
        }
        // 信号从离它近的那一头出发
        if (Distance(head.Value, map.Exit) < Distance(head.Value, map.Start))
        {
            runs = ReverseRoute(map.Start, runs);
            (map.Start, map.Exit) = (map.Exit, map.Start);
            (map.StartDirection, map.ExitDirection) = (map.ExitDirection, map.StartDirection);
            (map.MinimumStartRun, map.MinimumExitRun) = (map.MinimumExitRun, map.MinimumStartRun);
        }
        var plannedDirections = runs.Select(r => r.Dx > 0 ? 0 : r.Dx < 0 ? 1 : r.Dy > 0 ? 2 : 3).ToList();
        var speedTracker = new SpeedTracker(head.Value);
        var lastObservedHead = head.Value;
        var lastObservedPresent = lastPresent;
        var pendingTurnDirection = 4;
        long pendingTurnTicks = 0;
        Point pendingTurnPoint = default;
        var pendingTurnSpeed = 0.0;

        SetStatus("fleeca: navigating");
        for (var i = 0; i < runs.Count; i++)
        {
            if (StopRequested()) return 1;
            var run = runs[i];
            var plannedDirection = plannedDirections[i];
            var missedHeadFrames = 0;
            speedTracker.BeginSegment(head.Value);
            var directionRequested = Now;
            if (!SendKey(KeycodeForDirection(plannedDirection))) return 1;
            if (i > 0 && plannedDirection != plannedDirections[i - 1])
            {
                // 记下这次转弯，等看到信号真的转过去了，就能算出一次输入延迟
                pendingTurnDirection = plannedDirection;
                pendingTurnTicks = directionRequested;
                pendingTurnPoint = runs[i - 1].Target;
                pendingTurnSpeed = speedTracker.PixelsPerSecond;
            }
            while (true)
            {
                if (StopRequested()) return 1;
                var frameStatus = CaptureIngameFrame();
                if (frameStatus != 0) return frameStatus;
                var measuredRadius = trackedHeadRadius;
                Point? trackingAnchor = missedHeadFrames >= 2 ? null : head;
                var observed = DetectHead(_frame, map, trackingAnchor, ref measuredRadius, run.Search);
                if (observed is null)
                {
                    missedHeadFrames++;
                    continue;
                }
                missedHeadFrames = 0;
                var currentHead = observed.Value;
                var resetSpeedAnchor = false;
                if (pendingTurnDirection < 4 && pendingTurnTicks > 0 && lastPresent > pendingTurnTicks)
                {
                    int turnX = DeltaX[pendingTurnDirection], turnY = DeltaY[pendingTurnDirection];
                    int movementX = currentHead.X - lastObservedHead.X, movementY = currentHead.Y - lastObservedHead.Y;
                    var forward = (double)(movementX * turnX + movementY * turnY);
                    var perpendicular = Math.Abs((double)(movementX * turnY - movementY * turnX));
                    var minimumMotion = map.ConnectorLongSide * TurnMotionToConnectorLong;
                    if (forward >= minimumMotion && forward * 2.0 >= perpendicular * 3.0)
                    {
                        var transition = lastPresent;
                        if (pendingTurnSpeed > 1.0)
                        {
                            // 按转弯后走过的距离反推转弯发生的时刻
                            var travelledAfterTurn = (double)(currentHead.X - pendingTurnPoint.X) * turnX + (double)(currentHead.Y - pendingTurnPoint.Y) * turnY;
                            if (travelledAfterTurn >= 0.0)
                                transition -= (long)Math.Round(travelledAfterTurn / pendingTurnSpeed * Stopwatch.Frequency, MidpointRounding.AwayFromZero);
                        }
                        else
                        {
                            var lastOldFrame = Math.Max(pendingTurnTicks, lastObservedPresent);
                            transition = lastOldFrame + (lastPresent - lastOldFrame) / 2;
                        }
                        transition = Math.Clamp(transition, pendingTurnTicks, lastPresent);
                        _inputLatency.Observe(Seconds(transition - pendingTurnTicks));
                        pendingTurnDirection = 4;
                        pendingTurnTicks = 0;
                        pendingTurnSpeed = 0.0;
                        resetSpeedAnchor = true;
                    }
                    else if (Seconds(lastPresent - pendingTurnTicks) > LatencySampleMaxSeconds)
                    {
                        pendingTurnDirection = 4;
                        pendingTurnTicks = 0;
                        pendingTurnSpeed = 0.0;
                        resetSpeedAnchor = true;
                    }
                }
                lastObservedHead = currentHead;
                lastObservedPresent = lastPresent;
                head = currentHead;
                if (resetSpeedAnchor) speedTracker.BeginSegment(currentHead);
                else if (pendingTurnDirection >= 4) speedTracker.Observe(currentHead, run.Dx, run.Dy);
                trackedHeadRadius = trackedHeadRadius * 0.75 + measuredRadius * 0.25;
                var remaining = (double)(run.Target.X - currentHead.X) * run.Dx + (double)(run.Target.Y - currentHead.Y) * run.Dy;
                if (i + 1 == runs.Count)
                {
                    if (remaining <= 0.0) break;
                }
                else
                {
                    var speed = speedTracker.PixelsPerSecond;
                    if (speed > 1.0)
                    {
                        // 算出离转弯点还要多久，扣掉这帧画面已经过去的时间和输入延迟；下一次分析之前就要按的话，等到那一刻再按
                        var nowTicks = Now;
                        var frameAge = 0.0;
                        if (lastPresent > 0 && nowTicks >= lastPresent)
                        {
                            frameAge = Seconds(nowTicks - lastPresent);
                            if (frameAge > 0.100) frameAge = 0.0;
                        }
                        var etaFromCapture = Math.Max(0.0, remaining) / speed;
                        var inputLatency = _inputLatency.PredictionSeconds(displayFrameSeconds);
                        var delayUntilKey = etaFromCapture - frameAge - inputLatency;
                        var untilNextAnalysis = Math.Max(0.0, Seconds(nextAnalysisTime - Now));
                        var untilNextDecision = untilNextAnalysis + displayFrameSeconds;
                        if (delayUntilKey <= untilNextDecision)
                        {
                            WaitPrecisely(Math.Max(0.0, delayUntilKey));
                            break;
                        }
                    }
                    else if (remaining <= 0.0) break;
                }
            }
        }
        SetStatus("fleeca: waiting for result");
        while (!StopRequested())
        {
            var frameStatus = CaptureIngameFrame();
            if (frameStatus != 0) return frameStatus;
        }
        return 1;
    }

    private int RunLive()
    {
        if (StopRequested()) return 1;
        if (!CaptureClient()) return 1;
        Connectors? connectors;
        if (_detectedValid && _detectedGeneration == _frame.WindowGeneration && _detectedWidth == _frame.Width &&
            _detectedHeight == _frame.Height && ValidateConnectors(_frame, _detectedConnectors))
        {
            connectors = _detectedConnectors;
            _geometryGeneration = _detectedGeneration;
        }
        else
        {
            connectors = FindConnectors(_frame);
            if (connectors is not null) _geometryGeneration = _frame.WindowGeneration;
        }
        if (connectors is null) return 1;
        _connectors = connectors.Value;

        while (!StopRequested())
        {
            var sessionResult = RunIngameSession();
            if (sessionResult == SessionGeometryChanged)
            {
                SetStatus("fleeca: geometry relocated; rebuilding route");
                continue;
            }
            if (sessionResult != SessionIngameLost) return sessionResult;

            SetStatus("fleeca: detecting result");
            var resultDetected = DetectResultScreen(_frame);
            var resultDeadline = Environment.TickCount64 + ResultScreenTimeoutMs;
            while (!StopRequested() && !resultDetected && Environment.TickCount64 < resultDeadline)
            {
                Thread.Sleep(20);
                if (!CaptureClient()) return 1;
                resultDetected = DetectResultScreen(_frame);
            }
            if (StopRequested()) return 1;
            if (!resultDetected)
            {
                SetStatus("fleeca: result not detected");
                return 1;
            }
            _completedRound = true;
            SetStatus("fleeca: result detected; waiting for game");
            var enterDeadline = Environment.TickCount64 + ResultEnterDelayMs;
            while (!StopRequested() && Environment.TickCount64 < enterDeadline) Thread.Sleep(20);
            if (StopRequested()) return 1;
            SetStatus("fleeca: advancing");
            if (!SendKey(KeyEnter)) return 1;
            SetStatus("fleeca: waiting for next round");
            var nextRoundDeadline = Environment.TickCount64 + NextRoundTimeoutMs;
            Connectors? next;
            do
            {
                Thread.Sleep(20);
                if (!CaptureClient()) return 1;
                next = FindConnectors(_frame);
                if (next is not null) _geometryGeneration = _frame.WindowGeneration;
            } while (!StopRequested() && next is null && Environment.TickCount64 < nextRoundDeadline);
            if (StopRequested()) return 1;
            if (next is null)
            {
                SetStatus("fleeca: completed");
                return 0;
            }
            _connectors = next.Value;
            SetStatus("fleeca: next round");
        }
        return 1;
    }

    public bool DetectInGame()
    {
        var frame = host.Capture();
        var connectors = frame is null ? null : FindConnectors(frame);
        if (connectors is null)
        {
            ResetInGameCache();
            return false;
        }
        _detectedValid = true;
        _detectedGeneration = frame!.WindowGeneration;
        _detectedWidth = frame.Width;
        _detectedHeight = frame.Height;
        _detectedConnectors = connectors.Value;
        return true;
    }

    public void ResetInGameCache() => _detectedValid = false;

    public bool RunSession(Func<bool> stopRequested, Action<string> status)
    {
        _stop = stopRequested;
        _status = status;
        _completedRound = false;
        _inputLatency.Reset();
        var result = RunLive();
        _stop = () => false;
        _status = _ => { };
        ResetInGameCache();
        return result == 0 || _completedRound;
    }
}
