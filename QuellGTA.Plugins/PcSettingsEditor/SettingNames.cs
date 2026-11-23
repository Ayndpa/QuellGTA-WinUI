using System.Globalization;

namespace QuellGTA.Plugin.PcSettingsEditor;

/// <summary>
/// 项目 ID → 名称（原版资源「备注CSV」，每行「ID,名称」，内嵌为 ProfileSettings.csv）。
/// 原版 取备注：找不到时显示「（未知）」。这里另外给原版用到的几项加上中文说明。
/// </summary>
public static class SettingNames
{
    private static readonly Lazy<(Dictionary<int, string> map, (int id, string name)[] list)> Table = new(Load);

    private static readonly Dictionary<int, string> Chinese = new()
    {
        [212] = "安全区", [213] = "亮度", [231] = "第一人称视野", [300] = "SFX 音量", [301] = "音乐音量",
        [415] = "简易准星大小", [754] = "鼠标灵敏度",
        [909] = "角色 1 任务状态", [910] = "角色 2 任务状态", [914] = "角色 1 抢劫（首轮 / 太平洋）", [915] = "角色 2 抢劫（首轮 / 太平洋）",
        [931] = "上次云存档时间（高 32 位）", [932] = "上次云存档时间（低 32 位）",
    };

    public static IReadOnlyList<(int id, string name)> All => Table.Value.list;

    public static string Describe(int id)
    {
        var name = Table.Value.map.TryGetValue(id, out var n) ? n : "（未知）";
        return Chinese.TryGetValue(id, out var zh) ? $"{name}（{zh}）" : name;
    }

    private static (Dictionary<int, string>, (int, string)[]) Load()
    {
        var map = new Dictionary<int, string>();
        var list = new List<(int, string)>();
        try
        {
            using var stream = typeof(SettingNames).Assembly.GetManifestResourceStream("ProfileSettings.csv");
            if (stream is null) return (map, []);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                // 原版只认恰好两列的行
                var cols = line.Split(',');
                if (cols.Length != 2 || !int.TryParse(cols[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)) continue;
                map.TryAdd(id, cols[1]);
                list.Add((id, cols[1]));
            }
        }
        catch (Exception) { }
        return (map, list.ToArray());
    }
}

/// <summary>换机列表里的一个存档（原版 刷新存档列表t）。</summary>
public sealed record ProfileEntry(string Edition, string Id, DateTime? LastUsed, string Directory)
{
    public string SettingsFile => Path.Combine(Directory, "pc_settings.bin");
    public string LastUsedText => LastUsed is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "从未启动过";

    /// <summary>
    /// 扫描 文档\Rockstar Games\GTAV Enhanced\Profiles\* 和 GTA V\Profiles\*（先增强版后传承版）。
    /// 最近一次使用 = cfg.dat 的修改时间，没有 cfg.dat 时为「从未启动过」。
    /// </summary>
    public static List<ProfileEntry> Scan()
    {
        var list = new List<ProfileEntry>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games");
        foreach (var (folder, edition) in new[] { ("GTAV Enhanced", "增强版"), ("GTA V", "传承版") })
        {
            var profiles = Path.Combine(root, folder, "Profiles");
            string[] dirs;
            try { dirs = System.IO.Directory.Exists(profiles) ? System.IO.Directory.GetDirectories(profiles) : []; }
            catch (Exception) { continue; }
            foreach (var dir in dirs)
            {
                var cfg = Path.Combine(dir, "cfg.dat");
                DateTime? time = null;
                try { if (File.Exists(cfg)) time = File.GetLastWriteTime(cfg); }
                catch (Exception) { }
                list.Add(new ProfileEntry(edition, Path.GetFileName(dir), time, dir));
            }
        }
        return list;
    }

    /// <summary>自动选择最近的：时间最晚的那个；都没启动过时选第一个（原版 文本日期是否更新）。</summary>
    public static int Newest(IReadOnlyList<ProfileEntry> list)
    {
        if (list.Count == 0) return -1;
        var best = 0;
        for (var i = 1; i < list.Count; i++)
            if ((list[i].LastUsed ?? DateTime.MinValue) > (list[best].LastUsed ?? DateTime.MinValue)) best = i;
        return best;
    }
}
