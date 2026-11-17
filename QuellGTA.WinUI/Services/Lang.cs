using System.Text;

namespace QuellGTA.Services;

/// <summary>
/// 原版的语言支持文件（.als）：三段文本，用单独一行的「-」分隔。
/// 第 2 段是界面文本（Wstr），第 3 段是代码里用到的文本（Cstr），均按行号（从 1 开始）索引。
/// Assets/Lang/chs.txt、eng.txt 由原工程内嵌的 Als_CHS / Als_ENG 资源转成 UTF-8 得到。
/// </summary>
public static class Lang
{
    private static string[] _w = [];
    private static string[] _c = [];

    public static string Current { get; private set; } = "chs";

    public static bool Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Lang", name + ".txt");
        if (!File.Exists(path)) return false;
        var parts = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split("\n-\n");
        if (parts.Length != 3) return false;
        _w = parts[1].Split('\n');
        _c = parts[2].Split('\n');
        Current = name;
        return true;
    }

    /// <summary>
    /// 按原版「语言选项」加载：1 简体中文、2 繁体中文（由简体表逐句转换）、3 English。
    /// 只影响取自语言表的文字（日志和大部分提示）；移植版自己新写的界面文字只有简体中文。
    /// </summary>
    public static void Apply(int language)
    {
        if (language == 3 && Load("eng")) return;
        Load("chs");
        if (language != 2) return;
        _w = Array.ConvertAll(_w, ToTraditional);
        _c = Array.ConvertAll(_c, ToTraditional);
        Current = "cht";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        [System.Runtime.InteropServices.Out] char[]? destination, int destinationLength, nint version, nint reserved, nint sortHandle);

    /// <summary>编码_简体到繁体：用系统的简繁字形映射（逐字转换，不换词）。</summary>
    private static string ToTraditional(string text)
    {
        const uint LcmapTraditionalChinese = 0x04000000;
        if (text.Length == 0) return text;
        var buffer = new char[text.Length];
        var length = LCMapStringEx("zh-CN", LcmapTraditionalChinese, text, text.Length, buffer, buffer.Length, 0, 0, 0);
        return length > 0 ? new string(buffer, 0, length) : text;
    }

    /// <summary>代码文本，对应原版 Cstr(索引)。</summary>
    public static string C(int index) => index > 0 && index <= _c.Length ? _c[index - 1] : "<Text404>";

    /// <summary>界面文本，对应原版 Wstr(索引)。</summary>
    public static string W(int index) => index > 0 && index <= _w.Length ? _w[index - 1] : "<Text404>";
}
