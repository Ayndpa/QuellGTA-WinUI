using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static QuellGTA.Native.OverlayNative;

namespace QuellGTA.Services;

public enum FingerprintKind { Diamond = 0, Perico = 1 }

/// <summary>
/// 自动指纹：截取游戏画面，认出名钻赌场 / 佩里科岛的指纹破解小游戏该选哪几项，然后替你按方向键。
/// 移植自仓库里的 GTAFingerprinterCore（扩展插件/自动骇入指纹，原版把它编译成一个单独的程序随游戏启停）：
/// 画面先统一缩放到 1280×720，再按固定坐标切出大图和 8 张小图，用差值哈希（dHash）和内置模板比对。
/// 只识别 16:9 的画面；窗口模式下截的是游戏窗口的客户区。
/// </summary>
public sealed class FingerprintService(Settings settings, GameService game, LogService log, SoundService sound) : IDisposable
{
    private readonly record struct Region(int X, int Y, int Width, int Height, int Space);

    // appsettings.json 里的坐标（基于 1280×720）
    private static readonly Region DiamondMain = new(624, 104, 271, 350, 0), DiamondSub = new(315, 179, 81, 81, 15);
    private static readonly Region PericoMain = new(670, 230, 305, 405, 0), PericoSub = new(275, 238, 273, 41, 10);

    private Dictionary<Bitmap, List<Bitmap>>? _diamond, _perico;
    private int _busy;

    public bool Enabled
    {
        get => settings.GetBool("扩展", "指纹使能", false);
        set => settings.SetBool("扩展", "指纹使能", value);
    }
    public FingerprintKind Kind
    {
        get => (FingerprintKind)Math.Clamp(settings.GetInt("扩展", "指纹类型", 0), 0, 1);
        set => settings.SetInt("扩展", "指纹类型", (int)value);
    }
    /// <summary>游戏全屏模式：截整个主屏幕；关闭则只截游戏窗口。</summary>
    public bool FullScreen
    {
        get => settings.GetBool("扩展", "指纹全屏", true);
        set => settings.SetBool("扩展", "指纹全屏", value);
    }
    /// <summary>按键延迟（毫秒），原版默认 60。帧率低时调大。</summary>
    public int KeyDelay
    {
        get => Math.Clamp(settings.GetInt("扩展", "指纹按键延迟", 60), 10, 500);
        set => settings.SetInt("扩展", "指纹按键延迟", Math.Clamp(value, 10, 500));
    }
    /// <summary>图案相似度（百分比），原版默认 85。</summary>
    public int Similarity
    {
        get => Math.Clamp(settings.GetInt("扩展", "指纹相似度", 85), 50, 100);
        set => settings.SetInt("扩展", "指纹相似度", Math.Clamp(value, 50, 100));
    }

    public void Dispose()
    {
        foreach (var map in new[] { _diamond, _perico })
        {
            if (map is null) continue;
            foreach (var (main, subs) in map)
            {
                main.Dispose();
                foreach (var sub in subs) sub.Dispose();
            }
        }
    }

    // ───────── 模板 ─────────

    /// <summary>模板文件：N.png 是大图，N.1.png、N.2.png… 是它对应的正确小图。</summary>
    private static Dictionary<Bitmap, List<Bitmap>> LoadTemplates(string folder, int mains, int subsPerMain)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fingerprint", folder);
        var map = new Dictionary<Bitmap, List<Bitmap>>();
        for (var i = 1; i <= mains; i++)
        {
            var mainFile = Path.Combine(dir, $"{i}.png");
            if (!File.Exists(mainFile)) continue;
            var subs = new List<Bitmap>();
            for (var j = 1; j <= subsPerMain; j++)
            {
                var subFile = Path.Combine(dir, $"{i}.{j}.png");
                if (File.Exists(subFile)) subs.Add(new Bitmap(subFile));
            }
            map[new Bitmap(mainFile)] = subs;
        }
        return map;
    }

    private Dictionary<Bitmap, List<Bitmap>> Templates(FingerprintKind kind) => kind == FingerprintKind.Diamond
        ? _diamond ??= LoadTemplates("Diamond", 4, 4)
        : _perico ??= LoadTemplates("Perico", 7, 8);

    // ───────── 图像处理 ─────────

    /// <summary>差值哈希：缩到 9×8 的灰度图，每个像素和右边相邻的比较，得到 64 位指纹。</summary>
    private static ulong Hash(Bitmap image)
    {
        const int size = 8;
        using var small = new Bitmap(size + 1, size);
        using (var g = Graphics.FromImage(small))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix(
            [
                [.3f, .3f, .3f, 0, 0], [.59f, .59f, .59f, 0, 0], [.11f, .11f, .11f, 0, 0], [0, 0, 0, 1, 0], [0, 0, 0, 0, 1],
            ]));
            // 原版先缩小再转灰度，这里用同一个矩阵一步完成，结果一致
            using var thumb = new Bitmap(size + 1, size);
            using (var tg = Graphics.FromImage(thumb))
            {
                tg.SmoothingMode = SmoothingMode.HighQuality;
                tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                tg.DrawImage(image, 0, 0, size + 1, size);
            }
            g.DrawImage(thumb, new Rectangle(0, 0, size + 1, size), 0, 0, size + 1, size, GraphicsUnit.Pixel, attributes);
        }
        ulong hash = 0;
        for (var row = 0; row < size; row++)
            for (var column = 0; column < size; column++)
                if (small.GetPixel(column, row).R > small.GetPixel(column + 1, row).R)
                    hash |= 1UL << (row * size + column);
        return hash;
    }

    public static double Compare(Bitmap a, Bitmap b) =>
        (64 - System.Numerics.BitOperations.PopCount(Hash(a) ^ Hash(b))) / 64.0;

    /// <summary>切出一块区域，并把最外圈 3 个像素涂黑（原版的 PreProcess，模板也是这样处理过的）。</summary>
    private static Bitmap Cut(Bitmap image, int x, int y, int width, int height)
    {
        var piece = new Bitmap(width, height);
        using (var g = Graphics.FromImage(piece))
        {
            g.DrawImage(image, new Rectangle(0, 0, width, height), new Rectangle(x, y, width, height), GraphicsUnit.Pixel);
            g.FillRectangle(Brushes.Black, 0, 0, width, 3);
            g.FillRectangle(Brushes.Black, 0, height - 3, width, 3);
            g.FillRectangle(Brushes.Black, 0, 0, 3, height);
            g.FillRectangle(Brushes.Black, width - 3, 0, 3, height);
        }
        return piece;
    }

    /// <summary>把截图统一成 1280×720：按高度缩放，宽度不足（或超出）时左右居中补黑边（或裁掉）。</summary>
    private static Bitmap Normalize(Bitmap screen)
    {
        var width = (int)(screen.Width / (screen.Height / 720.0));
        var canvas = new Bitmap(1280, 720);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.Black);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(screen, (1280 - width) / 2, 0, width, 720);
        return canvas;
    }

    private Bitmap? Capture()
    {
        int x = 0, y = 0, width = GetSystemMetrics(0), height = GetSystemMetrics(1);
        if (!FullScreen)
        {
            var hwnd = game.GetWindowHandle();
            if (hwnd == 0 || !GetClientRect(hwnd, out var client) || client.Right <= 0) return null;
            var origin = new POINT();
            ClientToScreen(hwnd, ref origin);
            (x, y, width, height) = (origin.X, origin.Y, client.Right, client.Bottom);
        }
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        return bitmap;
    }

    // ───────── 识别 ─────────

    /// <summary>
    /// 在一张已经统一成 1280×720 的画面上识别。返回要选的位置（名钻：8 格里的 4 个下标；佩岛：8 行各自该转到的图案序号），
    /// 认不出来返回 null。
    /// </summary>
    public List<int>? Recognize(Bitmap frame, FingerprintKind kind, double similarity)
    {
        var templates = Templates(kind);
        var mainRegion = kind == FingerprintKind.Diamond ? DiamondMain : PericoMain;
        var subRegion = kind == FingerprintKind.Diamond ? DiamondSub : PericoSub;

        using var main = Cut(frame, mainRegion.X, mainRegion.Y, mainRegion.Width, mainRegion.Height);
        Bitmap? matched = null;
        var best = 0d;
        foreach (var candidate in templates.Keys)
        {
            var score = Compare(main, candidate);
            if (score >= similarity && score > best) (best, matched) = (score, candidate);
        }
        if (matched is null) return null;
        var answers = templates[matched];

        var subs = new List<Bitmap>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                subs.Add(kind == FingerprintKind.Diamond
                    ? Cut(frame, subRegion.X + i % 2 * (subRegion.Width + subRegion.Space),
                        subRegion.Y + i / 2 * (subRegion.Width + subRegion.Space), subRegion.Width, subRegion.Width)
                    : Cut(frame, subRegion.X, subRegion.Y + i * (subRegion.Height + subRegion.Space), subRegion.Width, subRegion.Height));
            }

            if (kind == FingerprintKind.Diamond)
            {
                // 8 张小图里有 4 张属于这枚指纹，找出它们的位置
                var picks = new List<int>();
                for (var i = 0; i < subs.Count && picks.Count < 4; i++)
                    if (answers.Any(answer => Compare(subs[i], answer) >= similarity)) picks.Add(i);
                return picks;
            }

            // 佩岛：每一行当前显示的是 8 段图案里的哪一段。认不全时逐步放宽相似度，低于 0.5 就放弃
            for (var threshold = similarity; threshold >= 0.5; threshold -= 0.1)
            {
                var rows = new List<int>();
                foreach (var sub in subs)
                {
                    var index = answers.FindIndex(answer => Compare(sub, answer) >= threshold);
                    if (index >= 0) rows.Add(index);
                }
                if (rows.Count == 8) return rows;
            }
            return null;
        }
        finally
        {
            foreach (var sub in subs) sub.Dispose();
        }
    }

    // ───────── 按键 ─────────

    private void Press(int vk, int count = 1)
    {
        for (var i = 0; i < count; i++) InputSim.KeyPress(vk, KeyDelay);
        if (count > 0) Thread.Sleep(KeyDelay);
    }

    private void PressDiamond(List<int> picks)
    {
        const int Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Enter = 0x0D, Tab = 0x09;
        int column = 0, row = 0; // 光标从左上角开始，2 列 × 4 行
        foreach (var pick in picks)
        {
            int targetColumn = pick % 2, targetRow = pick / 2;
            Press(targetColumn >= column ? Right : Left, Math.Abs(targetColumn - column));
            Press(targetRow >= row ? Down : Up, Math.Abs(targetRow - row));
            Press(Enter);
            (column, row) = (targetColumn, targetRow);
        }
        Press(Tab); // 提交
    }

    private void PressPerico(List<int> rows)
    {
        const int Left = 0x25, Right = 0x27, Down = 0x28;
        for (var i = 0; i < rows.Count; i++)
        {
            // 第 i 行应该显示第 i 段；现在显示的是 rows[i]。8 段首尾相接，走近的那一边
            var atRight = rows[i] >= i;
            var distance = Math.Abs(rows[i] - i);
            if (distance >= 4) Press(atRight ? Right : Left, 8 - distance);
            else if (distance > 0) Press(atRight ? Left : Right, distance);
            Press(Down);
        }
    }

    /// <summary>识别当前画面并自动按键。由「指纹键」热键触发；返回给界面看的结果说明。</summary>
    public string Solve()
    {
        if (!Enabled) return Report("自动指纹未启用。", 0);
        if (!game.IsRunning() || !game.IsForeground()) return Report("自动指纹：游戏不在前台。", 0);
        if (Interlocked.Exchange(ref _busy, 1) == 1) return "正在识别…";
        try
        {
            var kind = Kind;
            using var screen = Capture();
            if (screen is null) return Report("自动指纹：截取游戏画面失败。", 2);
            using var frame = Normalize(screen);
            var result = Recognize(frame, kind, Similarity / 100.0);
            var name = kind == FingerprintKind.Diamond ? "名钻指纹" : "佩岛指纹";
            if (result is null || result.Count == 0)
                return Report($"自动指纹：没有认出{name}。请确认正处在指纹破解画面、画面比例是 16:9，并且选对了指纹类型。", 2);

            sound.Play(SoundService.MacroOn);
            if (kind == FingerprintKind.Diamond) PressDiamond(result); else PressPerico(result);
            return Report($"自动指纹：{name}识别完成，已自动按键。", 1);
        }
        catch (Exception ex)
        {
            return Report("自动指纹出错：" + ex.Message, 3);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private string Report(string message, int level)
    {
        log.Write(message, level);
        return message;
    }
}
