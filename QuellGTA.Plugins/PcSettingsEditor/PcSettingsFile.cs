using System.Globalization;

namespace QuellGTA.Plugin.PcSettingsEditor;

/// <summary>pc_settings.bin 里的一条记录：4 字节项目 ID + 4 字节值，都是小端 int32。</summary>
public readonly record struct PcRecord(int Id, int Value);

/// <summary>
/// pc_settings.bin 的读写（不含界面，可以单独测试）。
///
/// 文件就是一串 8 字节的记录。原版「高级编辑」是在整个文件里按字节搜索 2 字节的 ID（例如 212 搜 "D4 00"），
/// 把命中位置 +4 当成值；搜不到就在文件末尾追加 "ID低 ID高 00 00 00 00 00 00"。
/// 这样搜可能会误中某条记录的值部分，这里改为按 8 字节对齐逐条查找，读写的字节和原版相同：
/// 「ID 位置 + 4 + k」就是本类里「值的第 k 个字节」（k = 0 ~ 3）。
/// </summary>
public sealed class PcSettingsFile
{
    private readonly List<PcRecord> _records;

    /// <summary>文件长度不是 8 的倍数时，最后多出来的几个字节（原样保留在末尾）。</summary>
    public byte[] Tail { get; }

    public IReadOnlyList<PcRecord> Records => _records;
    public int Count => _records.Count;

    public PcSettingsFile() : this([], []) { }

    private PcSettingsFile(List<PcRecord> records, byte[] tail)
    {
        _records = records;
        Tail = tail;
    }

    public static PcSettingsFile Parse(ReadOnlySpan<byte> data)
    {
        var count = data.Length / 8;
        var records = new List<PcRecord>(count + 16);
        for (var i = 0; i < count; i++)
        {
            var slice = data.Slice(i * 8, 8);
            records.Add(new PcRecord(BitConverter.ToInt32(slice[..4]), BitConverter.ToInt32(slice[4..])));
        }
        return new PcSettingsFile(records, data[(count * 8)..].ToArray());
    }

    /// <summary>读文件。游戏运行时也能读（共享读写）。</summary>
    public static PcSettingsFile Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return Parse(data);
    }

    public byte[] ToBytes()
    {
        var data = new byte[_records.Count * 8 + Tail.Length];
        for (var i = 0; i < _records.Count; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 8, 4), _records[i].Id);
            BitConverter.TryWriteBytes(data.AsSpan(i * 8 + 4, 4), _records[i].Value);
        }
        Tail.CopyTo(data, _records.Count * 8);
        return data;
    }

    /// <summary>写文件。文件是只读（已锁定）时抛 UnauthorizedAccessException。</summary>
    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    /// <summary>第一条 ID 相同的记录的序号，没有时为 -1（原版「寻找字节集」只取第一个命中）。</summary>
    public int IndexOf(int id) => _records.FindIndex(r => r.Id == id);

    public bool Contains(int id) => IndexOf(id) >= 0;

    public int? Get(int id) => IndexOf(id) is var i and >= 0 ? _records[i].Value : null;

    /// <summary>覆写值；没有这一项时追加到末尾。返回是否原本就有。</summary>
    public bool Set(int id, int value)
    {
        var i = IndexOf(id);
        if (i < 0)
        {
            _records.Add(new PcRecord(id, value));
            return false;
        }
        _records[i] = new PcRecord(id, value);
        return true;
    }

    /// <summary>删除第一条 ID 相同的记录。</summary>
    public bool Remove(int id)
    {
        var i = IndexOf(id);
        if (i < 0) return false;
        _records.RemoveAt(i);
        return true;
    }

    /// <summary>读值的第 offset 个字节起的 count 个字节（原版「取字节集 (位置 + 4 + offset, count)」）；没有这一项时为 null。</summary>
    public byte[]? GetBytes(int id, int offset, int count)
    {
        CheckRange(offset, count);
        if (Get(id) is not { } value) return null;
        return BitConverter.GetBytes(value).AsSpan(offset, count).ToArray();
    }

    /// <summary>
    /// 改写值的第 offset 个字节起的若干字节，其余字节不动（原版「替换字节集 (位置 + 4 + offset, n, 数据)」）。
    /// 没有这一项时先追加一条值为 0 的记录，和原版先补 "ID 00 00 00 00 00 00" 再改写一样。
    /// </summary>
    public void SetBytes(int id, int offset, ReadOnlySpan<byte> bytes)
    {
        CheckRange(offset, bytes.Length);
        var value = BitConverter.GetBytes(Get(id) ?? 0);
        bytes.CopyTo(value.AsSpan(offset));
        Set(id, BitConverter.ToInt32(value));
    }

    private static void CheckRange(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > 4) throw new ArgumentOutOfRangeException(nameof(offset));
    }
}

/// <summary>原版「传统编辑」页里的一个滑块：项目 ID、标题，以及「游戏中设置的：xx %」的换算方式。</summary>
public sealed record PcSlider(int Id, string Title, string Description, int Default, Func<int, double> Percent, string Glyph);

/// <summary>抢劫面板上已经买下的抢劫（记录 A 的第 1、2 字节）。</summary>
public enum EarlyHeist { None, Fleeca, PrisonBreak, HumaneLabs, Other }

/// <summary>首轮募资 / 太平洋标准（记录 B 的第 0 ~ 2 字节）。</summary>
public enum LateHeist { None, SeriesA, PacificStandard, Other }

/// <summary>一个角色的任务状态。Other 表示文件里是原版不认识的值，保存时原样保留。</summary>
public readonly record struct CharacterFlags(bool InMission, EarlyHeist Early, bool ResetBoard, LateHeist Late);

/// <summary>原版各功能用到的项目 ID、字节位置和取值。</summary>
public static class PcLayout
{
    /// <summary>JOB_ACTIVITY_ID_CHAR0 / CHAR1：角色 1、2 的记录 A（原版搜 "8D03" / "8E03"）。</summary>
    public const int Char1A = 909, Char2A = 910;
    /// <summary>FREEMODE_PROLOGUE_COMPLETE_CHAR0 / CHAR1：角色 1、2 的记录 B（原版搜 "9203" / "9303"）。</summary>
    public const int Char1B = 914, Char2B = 915;
    /// <summary>MP_CLOUD_POSIXTIME_HIGH32 / LOW32：上次线上云存档成功的时间（原版搜 "A3030000" / "A4030000"）。</summary>
    public const int CloudHigh = 931, CloudLow = 932;

    /// <summary>任务进行中：记录 A 值的第 0 字节，08 / 00。</summary>
    public const byte InMissionOn = 0x08, InMissionOff = 0x00;
    /// <summary>下次启动重置越狱人道面板：记录 A 值的第 3 字节，03 / 01。</summary>
    public const byte ResetOn = 0x03, ResetOff = 0x01;

    public static int RecordA(int character) => character == 2 ? Char2A : Char1A;
    public static int RecordB(int character) => character == 2 ? Char2B : Char1B;

    /// <summary>
    /// 七个滑块，顺序同原版 _刷新按钮_被单击（缺项时也按这个顺序追加）。
    /// 范围都是 0 ~ 255（原版滑块条的最大位置）；Default 是原版窗体里滑块的初始位置，
    /// 文件里没有这一项时原版保存的就是它。
    /// </summary>
    public static readonly PcSlider[] Sliders =
    [
        new(212, "安全区", "提示、小地图到屏幕边缘的距离。", 3, v => 100 - v * 10, ""),
        new(754, "鼠标灵敏度", "步行时鼠标环顾四周的速度。", 50, v => v / 2.0, ""),
        new(300, "SFX 音量", "载具引擎、枪声等音效的音量。", 9, v => v * 10, ""),
        new(301, "音乐音量", "电台、任务背景音乐的音量。", 9, v => v * 10, ""),
        new(231, "第一人称视野", "超出游戏里的范围可能不生效。", 5, v => v * 10, ""),
        new(415, "简易准星大小", "瞄准时小白点的大小。", 0, v => v * 10, ""),
        new(213, "亮度", "Gamma 校正。", 18, v => Math.Round(v / 3.0 * 10, 1, MidpointRounding.AwayFromZero), ""),
    ];

    /// <summary>「游戏中设置的：xx %」里的数字。</summary>
    public static string PercentText(PcSlider slider, int value) =>
        slider.Percent(value).ToString("0.##", CultureInfo.InvariantCulture) + " %";

    /// <summary>原版只读值的第 0 字节；没有这一项时返回滑块的初始位置。</summary>
    public static int ReadSlider(PcSettingsFile file, PcSlider slider) =>
        file.GetBytes(slider.Id, 0, 1) is { } b ? b[0] : slider.Default;

    /// <summary>原版写回时写满 4 个字节（「整数到十六进制文本_小端」）。</summary>
    public static void WriteSlider(PcSettingsFile file, PcSlider slider, int value) => file.Set(slider.Id, value);

    public static byte[] EarlyBytes(EarlyHeist heist) => heist switch
    {
        EarlyHeist.Fleeca => [0xB0, 0xFF],
        EarlyHeist.PrisonBreak => [0x80, 0xF7],
        EarlyHeist.HumaneLabs => [0x00, 0xF8],
        _ => [0x00, 0x00],
    };

    public static byte[] LateBytes(LateHeist heist) => heist switch
    {
        LateHeist.SeriesA => [0x0A, 0x1F, 0x00],
        LateHeist.PacificStandard => [0x0A, 0xC0, 0x07],
        _ => [0x0A, 0x00, 0x00],
    };

    /// <summary>
    /// 读一个角色的任务状态（原版 _刷新按钮_被单击 的「角色1解析 / 角色2解析」）：
    /// 第 0 字节不为 00 即任务进行中；第 3 字节大于 1 即重置面板；第 1、2 字节和第 B 记录的 0 ~ 2 字节按上面的取值对照。
    /// 原版遇到不认识的值时所有选框都不勾，保存时写成「无」；这里记为 Other，保存时不动它。
    /// </summary>
    public static CharacterFlags ReadFlags(PcSettingsFile file, int character)
    {
        var a = file.GetBytes(RecordA(character), 0, 4);
        var b = file.GetBytes(RecordB(character), 0, 3);
        var early = EarlyHeist.None;
        if (a is not null)
        {
            early = Array.Find([EarlyHeist.None, EarlyHeist.Fleeca, EarlyHeist.PrisonBreak, EarlyHeist.HumaneLabs],
                h => EarlyBytes(h).AsSpan().SequenceEqual(a.AsSpan(1, 2)));
            if (early == EarlyHeist.None && (a[1] != 0 || a[2] != 0)) early = EarlyHeist.Other;
        }
        var late = LateHeist.None;
        if (b is not null)
        {
            late = Array.Find([LateHeist.None, LateHeist.SeriesA, LateHeist.PacificStandard],
                h => LateBytes(h).AsSpan().SequenceEqual(b));
            if (late == LateHeist.None && !LateBytes(LateHeist.None).AsSpan().SequenceEqual(b)) late = LateHeist.Other;
        }
        return new CharacterFlags(a is not null && a[0] != 0, early, a is not null && a[3] > 1, late);
    }

    /// <summary>
    /// 写一个角色的任务状态（原版 _保存按钮_被单击）：A+4 写 08/00，A+7 写 03/01，A+5 写 2 字节，B+4 写 3 字节；
    /// 记录不存在时先追加。Other 的部分不写。
    /// </summary>
    public static void WriteFlags(PcSettingsFile file, int character, CharacterFlags flags)
    {
        var a = RecordA(character);
        file.SetBytes(a, 0, [flags.InMission ? InMissionOn : InMissionOff]);
        file.SetBytes(a, 3, [flags.ResetBoard ? ResetOn : ResetOff]);
        if (flags.Early != EarlyHeist.Other) file.SetBytes(a, 1, EarlyBytes(flags.Early));
        if (flags.Late != LateHeist.Other) file.SetBytes(RecordB(character), 0, LateBytes(flags.Late));
    }

    /// <summary>
    /// 「传统编辑」的保存：七个滑块和两个角色的任务状态全部按当前值写一遍，缺的项目追加到末尾
    /// （原版刷新时就把缺的项目补上了，保存时再逐项写入）。滑块只保留值的低 8 位，和原版一样。
    /// </summary>
    public static void ApplyClassic(PcSettingsFile file)
    {
        foreach (var slider in Sliders) WriteSlider(file, slider, ReadSlider(file, slider));
        for (var c = 1; c <= 2; c++)
        {
            // 原版的追加顺序：角色 1 的 A、B，再角色 2 的 A、B
            var flags = ReadFlags(file, c);
            WriteFlags(file, c, flags);
        }
    }

    /// <summary>
    /// 快捷任务「拥有全福 / 越狱 / 人道」（原版 _全福按钮_被单击 等）：两个角色的 A+5 写抢劫、A+7 写 01。
    /// </summary>
    public static void QuickEarly(PcSettingsFile file, EarlyHeist heist)
    {
        for (var c = 1; c <= 2; c++)
        {
            file.SetBytes(RecordA(c), 1, EarlyBytes(heist));
            file.SetBytes(RecordA(c), 3, [ResetOff]);
        }
    }

    /// <summary>快捷任务「拥有首轮 / 太平洋」（原版 _首轮按钮_被单击 等）：两个角色的 B+4 写 3 字节。</summary>
    public static void QuickLate(PcSettingsFile file, LateHeist heist)
    {
        for (var c = 1; c <= 2; c++) file.SetBytes(RecordB(c), 0, LateBytes(heist));
    }

    /// <summary>931、932 两项合起来的云存档时间戳；缺任意一项时为 null（原版 _存档时钟_周期事件）。</summary>
    public static string? CloudStamp(PcSettingsFile file) =>
        file.Get(CloudHigh) is { } high && file.Get(CloudLow) is { } low ? $"{high:X8}{low:X8}" : null;

    // ───────── 手动编辑的进制显示（原版 进制转换_读取 / 进制转换_保存） ─────────

    /// <summary>0 十进制、1 二进制、2 十六进制。负数在二进制、十六进制下按 32 位补码显示。</summary>
    public static string FormatValue(int value, int radix) => radix switch
    {
        1 => Convert.ToString(value, 2),
        2 => ((uint)value).ToString("X", CultureInfo.InvariantCulture),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>按进制解析；超出 32 位范围时返回 false（原版「您填写的数值溢出！无法保存！」）。</summary>
    public static bool TryParseValue(string text, int radix, out int value)
    {
        value = 0;
        text = text.Trim().Replace(" ", "");
        if (text.Length == 0) return false;
        try
        {
            switch (radix)
            {
                case 1:
                    if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) text = text[2..];
                    if (text.Length is 0 or > 32 || text.Any(c => c is not ('0' or '1'))) return false;
                    value = (int)Convert.ToUInt32(text, 2);
                    return true;
                case 2:
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
                    if (!uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)) return false;
                    value = (int)hex;
                    return true;
                default:
                    return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
            }
        }
        catch (Exception e) when (e is FormatException or OverflowException) { return false; }
    }
}
