using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace QuellGTA.Services;

public enum LaunchPlatform { SteamEnhanced, EpicEnhanced, SteamLegacy, EpicLegacy }

/// <summary>一次文件操作的结果，Message 用于界面提示。</summary>
public readonly record struct FileResult(bool Ok, string Message);

/// <summary>
/// 移植自原版「修复设置 / 修复文件 / 游戏调整选项 / 游戏启动」等菜单：
/// 对 文档\Rockstar Games\{版本}\ 下的设置文件和游戏目录下的 args.txt 做清洗、备份、修改。
/// 与原版不同：凡是「删除」都改为移到回收站，可以找回。
/// </summary>
public sealed class GameFilesService(Settings settings, GameService game, LogService log)
{
    /// <summary>清洗_是否在白名单：pc_settings.bin 里属于「游戏设置」、需要保留的项目 ID。</summary>
    private static readonly HashSet<int> Whitelist =
    [
        0, 1, 2, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 100, 203, 204, 205, 207, 208, 211,
        212, 213, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239,
        240, 241, 242, 243, 244, 245, 251, 260, 261, 262, 300, 301, 302, 303, 304, 305, 306, 307, 308, 310, 311, 312,
        313, 314, 315, 316, 317, 318, 320, 321, 322, 323, 324, 325, 326, 327, 328, 329, 330, 331, 333, 334, 335, 336,
        337, 338, 339, 340, 412, 415, 710, 720, 721, 722, 723, 724, 725, 726, 727, 728, 729, 750, 751, 752, 753, 754,
        755, 756, 757, 758, 759, 760, 761, 762, 800, 801, 802, 803, 804, 805, 806, 807, 810, 811, 900, 950, 951, 952,
        953, 954, 955, 956, 957, 958, 960, 961, 962, 964,
    ];

    // ───────── 选项 ─────────

    /// <summary>「仅处理最近用设置文件」：只处理最近登录过的那个账号的存档目录。</summary>
    public bool OnlyLatestProfile
    {
        get => settings.GetBool("扩展", "仅修复最近", false);
        set => settings.SetBool("扩展", "仅修复最近", value);
    }
    /// <summary>「使用旧版本清洗方式」：保留 ID 小于 850 的全部项目，而不是按白名单。</summary>
    public bool LegacyClean
    {
        get => settings.GetBool("扩展", "旧清洗方式", false);
        set => settings.SetBool("扩展", "旧清洗方式", value);
    }
    public bool CleanOnExit
    {
        get => settings.GetBool("扩展", "退游清洗", false);
        set => settings.SetBool("扩展", "退游清洗", value);
    }
    public bool DeleteOnExit
    {
        get => settings.GetBool("扩展", "退游删除", false);
        set => settings.SetBool("扩展", "退游删除", value);
    }
    public bool ClearShaderOnExit
    {
        get => settings.GetBool("扩展", "退游清着色器", false);
        set => settings.SetBool("扩展", "退游清着色器", value);
    }

    // ───────── 路径 ─────────

    public string DocumentsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games", game.DocumentsDirName);

    public string ProfilesDir => Path.Combine(DocumentsDir, "Profiles");
    public string GraphicsFile => Path.Combine(DocumentsDir, "settings.xml");

    /// <summary>游戏安装目录：取自上一次检测到的游戏进程路径（原版「刷新进程路径」）。没运行过游戏时为空。</summary>
    public string InstallDir
    {
        get
        {
            var path = settings.Get("本地", "进程路径", "");
            return path.Length > 0 ? Path.GetDirectoryName(path) ?? "" : "";
        }
    }

    public string InstallExeName => Path.GetFileName(settings.Get("本地", "进程路径", ""));

    public string[] GetProfiles()
    {
        try
        {
            return Directory.Exists(ProfilesDir)
                ? Directory.GetDirectories(ProfilesDir).Select(d => Path.GetFileName(d)!).ToArray()
                : [];
        }
        catch (Exception) { return []; }
    }

    /// <summary>最近使用的存档：cfg.dat 修改时间最新的那个目录，找不到返回空。</summary>
    public string LatestProfile()
    {
        var best = "";
        var bestTime = DateTime.MinValue;
        foreach (var profile in GetProfiles())
        {
            var cfg = Path.Combine(ProfilesDir, profile, "cfg.dat");
            var time = File.Exists(cfg) ? File.GetLastWriteTime(cfg) : DateTime.MinValue.AddTicks(1);
            if (time > bestTime) (best, bestTime) = (profile, time);
        }
        return best;
    }

    private string[] TargetProfiles()
    {
        if (!OnlyLatestProfile) return GetProfiles();
        var latest = LatestProfile();
        return latest.Length > 0 ? [latest] : [];
    }

    private string SettingsFile(string profile) => Path.Combine(ProfilesDir, profile, "pc_settings.bin");

    private static void Recycle(string file) =>
        FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    private FileResult? RequireGameClosed() =>
        game.IsRunning() ? new FileResult(false, Lang.C(37)) : null;

    // ───────── pc_settings.bin ─────────

    /// <summary>
    /// 仅清洗线上设置文件：pc_settings.bin 由若干条 8 字节记录组成（4 字节项目 ID + 4 字节值），
    /// 只保留属于游戏设置的记录，其余全部丢弃。
    /// </summary>
    public FileResult CleanSettings()
    {
        if (RequireGameClosed() is { } blocked) return blocked;
        var legacy = LegacyClean;
        int ok = 0, failed = 0;
        foreach (var profile in TargetProfiles())
        {
            var file = SettingsFile(profile);
            if (!File.Exists(file)) continue;
            try
            {
                File.WriteAllBytes(file, CleanRecords(File.ReadAllBytes(file), legacy));
                log.Write(Lang.C(184) + profile, 1);
                ok++;
            }
            catch (Exception)
            {
                log.Write(Lang.C(185) + profile + Lang.C(174), 2);
                failed++;
            }
        }
        return Summarize("清洗", ok, failed);
    }

    /// <summary>清洗一份 pc_settings.bin 的内容（换机助手导出时也用它）。</summary>
    public static byte[] CleanRecords(byte[] input) => CleanRecords(input, legacy: false);

    private static byte[] CleanRecords(byte[] input, bool legacy)
    {
        using var output = new MemoryStream(input.Length);
        for (var i = 0; i + 8 <= input.Length; i += 8)
        {
            var id = BitConverter.ToInt32(input, i);
            if (legacy ? id < 850 : Whitelist.Contains(id)) output.Write(input, i, 8);
        }
        return output.ToArray();
    }

    /// <summary>读出 931、932 两项的值；文件读不了或没有这两项时返回 null。</summary>
    public static string? ReadCloudStamp(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            byte[] data;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                data = new byte[stream.Length];
                stream.ReadExactly(data);
            }
            int? high = null, low = null;
            for (var i = 0; i + 8 <= data.Length; i += 8)
            {
                var id = BitConverter.ToInt32(data, i);
                if (id == 931) high ??= BitConverter.ToInt32(data, i + 4);
                else if (id == 932) low ??= BitConverter.ToInt32(data, i + 4);
            }
            return high is null || low is null ? null : $"{high:X8}{low:X8}";
        }
        catch (Exception) { return null; }
    }

    /// <summary>备份后删除设置文件：把 pc_settings.bin 改名为 .bak。</summary>
    public FileResult DeleteSettings()
    {
        if (RequireGameClosed() is { } blocked) return blocked;
        int ok = 0, failed = 0;
        foreach (var profile in TargetProfiles())
        {
            var file = SettingsFile(profile);
            if (!File.Exists(file)) continue;
            try
            {
                if (File.Exists(file + ".bak")) Recycle(file + ".bak");
                File.Move(file, file + ".bak");
                log.Write(Lang.C(26) + profile, 1);
                ok++;
            }
            catch (Exception)
            {
                log.Write(Lang.C(27) + profile + Lang.C(174), 2);
                failed++;
            }
        }
        return Summarize("删除", ok, failed);
    }

    /// <summary>备份设置：复制一份 .bak，已有的 .bak 进回收站。</summary>
    public FileResult BackupSettings() => ForEachSettingsFile("备份", file =>
    {
        if (File.Exists(file + ".bak")) Recycle(file + ".bak");
        File.Copy(file, file + ".bak");
    });

    /// <summary>恢复备份：用 .bak 覆盖当前文件，当前文件进回收站。</summary>
    public FileResult RestoreSettings()
    {
        int ok = 0, failed = 0;
        foreach (var profile in TargetProfiles())
        {
            var file = SettingsFile(profile);
            if (!File.Exists(file + ".bak")) continue;
            try
            {
                if (File.Exists(file)) Recycle(file);
                File.Move(file + ".bak", file);
                ok++;
            }
            catch (Exception) { failed++; }
        }
        return Summarize("恢复", ok, failed);
    }

    /// <summary>设置只读 / 取消只读。只读后游戏无法改写设置文件。</summary>
    public FileResult SetReadOnly(bool readOnly) => ForEachSettingsFile(readOnly ? "设置只读" : "取消只读", file =>
    {
        var attributes = File.GetAttributes(file);
        File.SetAttributes(file, readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
    });

    private FileResult ForEachSettingsFile(string action, Action<string> work)
    {
        int ok = 0, failed = 0;
        foreach (var profile in TargetProfiles())
        {
            var file = SettingsFile(profile);
            if (!File.Exists(file)) continue;
            try { work(file); ok++; }
            catch (Exception) { failed++; }
        }
        return Summarize(action, ok, failed);
    }

    private FileResult Summarize(string action, int ok, int failed)
    {
        if (ok == 0 && failed == 0) return new FileResult(false, $"没有找到可以{action}的设置文件（{ProfilesDir}）。");
        var message = failed == 0
            ? $"已{action} {ok} 个存档的设置文件。"
            : $"{action}完成：成功 {ok} 个，失败 {failed} 个{Lang.C(174)}";
        log.Write(message, failed == 0 ? 1 : 2);
        return new FileResult(failed == 0, message);
    }

    /// <summary>界面上显示的概况：每个存档的设置文件状态。</summary>
    public List<(string profile, bool exists, bool hasBackup, bool readOnly, bool latest)> DescribeProfiles()
    {
        var latest = LatestProfile();
        var list = new List<(string, bool, bool, bool, bool)>();
        foreach (var profile in GetProfiles())
        {
            var file = SettingsFile(profile);
            var exists = File.Exists(file);
            var readOnly = exists && File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly);
            list.Add((profile, exists, File.Exists(file + ".bak"), readOnly, profile == latest));
        }
        return list;
    }

    // ───────── 其他设置文件 ─────────

    /// <summary>删除画质设置文件 settings.xml。</summary>
    public FileResult DeleteGraphicsSettings()
    {
        if (RequireGameClosed() is { } blocked) return blocked;
        try
        {
            if (File.Exists(GraphicsFile)) Recycle(GraphicsFile);
            log.Write(Lang.C(504), 1);
            return new FileResult(true, Lang.C(504));
        }
        catch (Exception) { return new FileResult(false, Lang.C(505)); }
    }

    /// <summary>删除键位设置文件 Profiles\{存档}\control\user.xml。</summary>
    public FileResult DeleteKeyBindings()
    {
        if (RequireGameClosed() is { } blocked) return blocked;
        int ok = 0, failed = 0;
        foreach (var profile in TargetProfiles())
        {
            var file = Path.Combine(ProfilesDir, profile, "control", "user.xml");
            if (!File.Exists(file)) continue;
            try { Recycle(file); ok++; }
            catch (Exception) { failed++; }
        }
        return Summarize("删除键位", ok, failed);
    }

    /// <summary>清理 DX12 着色器缓存：原版的做法是删除文档目录下的 Settings 文件夹。</summary>
    public FileResult ClearShaderCache()
    {
        if (RequireGameClosed() is { } blocked) return blocked;
        var dir = Path.Combine(DocumentsDir, "Settings");
        try
        {
            if (Directory.Exists(dir))
                FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            log.Write(Lang.C(143), 1);
            return new FileResult(true, Lang.C(143));
        }
        catch (Exception)
        {
            log.Write(Lang.C(144), 2);
            return new FileResult(false, Lang.C(144));
        }
    }

    /// <summary>游戏退出时按选项自动处理（原版「退游戏自动清洗 / 自动删除 / 自动清DX12缓存」）。</summary>
    public void OnGameExited()
    {
        if (!CleanOnExit && !DeleteOnExit && !ClearShaderOnExit) return;
        Thread.Sleep(1500); // 等游戏把文件句柄放掉
        if (DeleteOnExit) DeleteSettings();
        else if (CleanOnExit) CleanSettings();
        if (ClearShaderOnExit) ClearShaderCache();
    }

    // ───────── settings.xml 画质调整 ─────────

    /// <summary>「游戏调整选项」里可以改的项目：(标签名, 显示名, 说明)。</summary>
    public static readonly (string tag, string title, string hint)[] GraphicsOptions =
    [
        ("ShadowQuality", "阴影质量", "整数。0 为关闭阴影，数值越大越精细。"),
        ("CityDensity", "人口密度", "0 ~ 1 的小数。调低可以减少车流和行人。"),
        ("PedVarietyMultiplier", "行人种类", "0 ~ 1 的小数。"),
        ("VehicleVarietyMultiplier", "车辆种类", "0 ~ 1 的小数。"),
        ("Raytracing_Enabled", "光线追踪（增强版）", "true 或 false。"),
        ("ResScalingType", "超采样类型（增强版）", "整数。0 为关闭。"),
        ("FrameGenType", "帧生成类型（增强版）", "整数。0 为关闭。"),
    ];

    private static Regex TagPattern(string tag) =>
        new($"(<{Regex.Escape(tag)}\\s+value=\")([^\"]*)(\")", RegexOptions.CultureInvariant);

    /// <summary>读取 settings.xml 里某一项的值；文件或项目不存在时返回 null。</summary>
    public string? GetGraphicsValue(string tag)
    {
        try
        {
            if (!File.Exists(GraphicsFile)) return null;
            var match = TagPattern(tag).Match(File.ReadAllText(GraphicsFile));
            return match.Success ? match.Groups[2].Value : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>settings.xml 里记录的显卡名称，用来让用户确认改的是哪台机器的配置。</summary>
    public string VideoCard()
    {
        try
        {
            if (!File.Exists(GraphicsFile)) return "";
            var match = Regex.Match(File.ReadAllText(GraphicsFile), "<VideoCardDescription>([^<]*)</VideoCardDescription>");
            return match.Success ? match.Groups[1].Value : "";
        }
        catch (Exception) { return ""; }
    }

    public FileResult SetGraphicsValue(string tag, string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Length > 16 || !Regex.IsMatch(value, "^[A-Za-z0-9.+-]+$"))
            return new FileResult(false, "数值格式不正确。");
        if (!File.Exists(GraphicsFile)) return new FileResult(false, Lang.C(274));
        try
        {
            var text = File.ReadAllText(GraphicsFile);
            var pattern = TagPattern(tag);
            if (!pattern.IsMatch(text)) return new FileResult(false, $"settings.xml 里没有 {tag} 这一项（当前游戏版本不支持）。");
            File.WriteAllText(GraphicsFile, pattern.Replace(text, m => m.Groups[1].Value + value + m.Groups[3].Value, 1));
            log.Write($"已把 {tag} 改为 {value}。", 1);
            return new FileResult(true, game.IsRunning() ? "已修改。游戏正在运行，需要重启游戏才能生效。" : "已修改，下次启动游戏生效。");
        }
        catch (Exception ex) { return new FileResult(false, "修改失败：" + ex.Message); }
    }

    // ───────── args.txt 启动选项 ─────────

    /// <summary>原版菜单里的三个启动选项：(参数, 显示名, 说明)。</summary>
    public static readonly (string flag, string title, string hint)[] LaunchFlags =
    [
        ("-scDisableOverlayV3", "旧版 Home 菜单", "禁用新版 Rockstar 覆盖层，改用旧版 Home 菜单。"),
        ("-disableSpatialAudioClient", "禁用环绕音效", "解决部分耳机没有声音或声音异常的问题。"),
        ("-safemode", "安全模式启动", "以最低画质启动一次，用于画质设置导致无法进入游戏时。"),
    ];

    private string ArgsFile => Path.Combine(InstallDir, "args.txt");

    public List<string> GetArgs()
    {
        try
        {
            return InstallDir.Length > 0 && File.Exists(ArgsFile)
                ? File.ReadAllLines(ArgsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                : [];
        }
        catch (Exception) { return []; }
    }

    public FileResult SetArg(string flag, bool enabled)
    {
        if (InstallDir.Length == 0) return new FileResult(false, Lang.C(204));
        try
        {
            var args = GetArgs();
            args.RemoveAll(a => a == flag);
            if (enabled) args.Add(flag);
            File.WriteAllText(ArgsFile, string.Join("\r\n", args));
            return new FileResult(true, Lang.C(360));
        }
        catch (Exception ex) { return new FileResult(false, "写入 args.txt 失败：" + ex.Message); }
    }

    /// <summary>清空命令行：删除 args.txt。</summary>
    public FileResult ClearArgs()
    {
        if (InstallDir.Length == 0) return new FileResult(false, Lang.C(204));
        try
        {
            if (File.Exists(ArgsFile)) Recycle(ArgsFile);
            return new FileResult(true, Lang.C(360));
        }
        catch (Exception ex) { return new FileResult(false, "删除 args.txt 失败：" + ex.Message); }
    }

    // ───────── 启动游戏 ─────────

    public void Launch(LaunchPlatform platform)
    {
        var (name, url) = platform switch
        {
            LaunchPlatform.SteamEnhanced => ("Steam 增强版", "steam://rungameid/3240220"),
            LaunchPlatform.EpicEnhanced => ("Epic 增强版",
                "com.epicgames.launcher://apps/b0cd075465c44f87be3b505ac04a2e46%3A122e5e90b7b8424d930be8bc1a7e05fb%3A8769e24080ea413b8ebca3f1b8c50951?action=launch"),
            LaunchPlatform.SteamLegacy => ("Steam 传承版", "steam://rungameid/271590"),
            _ => ("Epic 传承版",
                "com.epicgames.launcher://apps/0584d2013f0149a791e7b9bad0eec102%3A6e563a2c0f5f46e3b4e88b5f4ed50cca%3A9d2d0eb64d5c44529cece33fe2a46482?action=launch"),
        };
        // 用 explorer 打开，避免游戏平台继承本程序的管理员权限（原版用计划任务达到同样的目的）。
        Shell.Run("explorer.exe", $"\"{url}\"", wait: false);
        log.Write(Lang.C(83) + name, 1);
    }

    // ───────── 客户端文件 ─────────

    private static bool LooksLikeGameDir(string dir) =>
        Directory.Exists(Path.Combine(dir, "x64")) && Directory.EnumerateFiles(dir, "*.rpf").Any();

    /// <summary>
    /// 修复客户端文件：删除游戏目录里除 .rpf 资源包以外的所有文件，以及 x64\data 文件夹（都移到回收站），
    /// 之后要到 Steam / Epic 里「验证文件完整性」把它们重新下载回来。用于错误的命令行、MOD、修改器导致的闪退。
    /// </summary>
    public FileResult RepairClientFiles(string gameDir)
    {
        if (RequireGameClosed() is { } blocked) return blocked;
        gameDir = gameDir.Trim().Trim('"');
        if (gameDir.Length == 0 || !LooksLikeGameDir(gameDir)) return new FileResult(false, Lang.C(425));
        int removed = 0, failed = 0;
        var data = Path.Combine(gameDir, "x64", "data");
        try
        {
            if (Directory.Exists(data))
            {
                FileSystem.DeleteDirectory(data, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                removed++;
            }
        }
        catch (Exception) { log.Write(Lang.C(423) + @"x64\data"); failed++; }

        foreach (var file in Directory.GetFiles(gameDir))
        {
            if (Path.GetExtension(file).Equals(".rpf", StringComparison.OrdinalIgnoreCase)) continue;
            try { Recycle(file); removed++; }
            catch (Exception) { log.Write(Lang.C(423) + Path.GetFileName(file)); failed++; }
        }
        log.Write($"修复客户端文件：已移除 {removed} 项，失败 {failed} 项。", failed == 0 ? 1 : 2);
        return new FileResult(failed == 0, $"已移除 {removed} 项（在回收站里），失败 {failed} 项。\n\n{Lang.C(424)}");
    }

    /// <summary>
    /// 多平台共用资源文件：在 Steam 和 Epic 上都有游戏时，让 B 平台的目录通过链接指向 A 平台已装好的文件，
    /// 只占一份磁盘空间。x64、update 两个文件夹建目录联接，根目录的 .rpf 文件建硬链接（要求两个目录在同一个分区）。
    /// </summary>
    public FileResult LinkSharedResources(string sourceDir, string targetDir)
    {
        if (RequireGameClosed() is { } blocked) return blocked;
        sourceDir = sourceDir.Trim().Trim('"').TrimEnd('\\');
        targetDir = targetDir.Trim().Trim('"').TrimEnd('\\');
        if (sourceDir.Length == 0 || !LooksLikeGameDir(sourceDir)) return new FileResult(false, "A 平台的路径不是已安装的游戏目录。");
        if (targetDir.Length == 0 || !Directory.Exists(targetDir)) return new FileResult(false, "B 平台的目录不存在。");
        if (string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
            return new FileResult(false, "A、B 不能是同一个目录。");
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(sourceDir)), Path.GetPathRoot(Path.GetFullPath(targetDir)), StringComparison.OrdinalIgnoreCase))
            return new FileResult(false, "A、B 必须在同一个磁盘分区上，否则无法创建硬链接。");

        int ok = 0, failed = 0;
        void Link(string kind, string name)
        {
            var target = Path.Combine(targetDir, name);
            if (File.Exists(target) || Directory.Exists(target)) { failed++; log.Write($"已存在，跳过：{target}"); return; }
            var code = Shell.Run("cmd.exe", $"/c mklink {kind} \"{target}\" \"{Path.Combine(sourceDir, name)}\"", wait: true);
            if (code == 0) ok++; else failed++;
        }
        foreach (var folder in new[] { "x64", "update" })
            if (Directory.Exists(Path.Combine(sourceDir, folder))) Link("/j", folder);
        foreach (var file in Directory.GetFiles(sourceDir, "*.rpf")) Link("/h", Path.GetFileName(file));

        log.Write($"多平台共用资源：创建了 {ok} 个链接，{failed} 个失败或已存在。", failed == 0 ? 1 : 2);
        return new FileResult(ok > 0, $"创建了 {ok} 个链接，{failed} 个失败或已存在。\n\n{Lang.C(428)}");
    }

    // ───────── 修复 ─────────

    /// <summary>修复 BattlEye 反作弊：删除 BattlEye 的公共文件，下次启动游戏时会自动重装。</summary>
    public FileResult RepairBattlEye(bool reinstallNow)
    {
        if (RequireGameClosed() is { } blocked) return blocked;
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Common Files", "BattlEye"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BattlEye"),
        };
        var failed = false;
        foreach (var dir in dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception) { failed = true; }
        }
        if (reinstallNow)
        {
            var exe = settings.Get("本地", "进程路径", "");
            var be = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] + "_BE.exe" : "";
            if (be.Length > 0 && File.Exists(be)) Shell.Open(be);
        }
        var message = failed ? "部分 BattlEye 文件删除失败（可能仍在被占用）。" : Lang.C(405);
        log.Write("修复 BattlEye：" + message, failed ? 2 : 1);
        return new FileResult(!failed, message);
    }

    /// <summary>
    /// 修复 Rockstar 启动器：结束启动器相关进程，把启动器和 Social Club 的数据目录改名为 .bak，让它们重新生成。
    /// </summary>
    public FileResult RepairLauncher()
    {
        if (RequireGameClosed() is { } blocked) return blocked;
        foreach (var name in new[]
                 {
                     "SocialClubHelper.exe", "RockstarService.exe", "RockstarErrorHandler.exe",
                     "RockstarSteamHelper.exe", "LauncherPatcher.exe", "Social-Club-Setup.exe",
                 })
            game.Terminate(name);
        game.KillLauncher();
        Thread.Sleep(500);

        var documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rockstar Games");
        var report = new StringBuilder();
        foreach (var dir in new[]
                 {
                     Path.Combine(local, "Launcher"), Path.Combine(documents, "Launcher"), Path.Combine(documents, "Social Club"),
                 })
        {
            if (!Directory.Exists(dir)) { report.AppendLine($"未找到：{dir}"); continue; }
            try
            {
                if (Directory.Exists(dir + ".bak"))
                    FileSystem.DeleteDirectory(dir + ".bak", UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                Directory.Move(dir, dir + ".bak");
                Directory.CreateDirectory(dir);
                report.AppendLine($"已重置：{dir}");
            }
            catch (Exception) { report.AppendLine($"重置失败：{dir}"); }
        }
        log.Write("修复 Rockstar 启动器：已尝试所有修复项目。", 1);
        return new FileResult(true, report + Lang.C(298));
    }
}
