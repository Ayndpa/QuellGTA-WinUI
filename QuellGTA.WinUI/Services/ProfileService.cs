using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace QuellGTA.Services;

/// <summary>换机助手要迁移哪些文件。</summary>
[Flags]
public enum MigrationParts { None = 0, Settings = 1, KeyBindings = 2, Graphics = 4 }

/// <summary>
/// 两块和「搬配置」有关的功能：
/// 1. 配置存档（原版「用户」菜单）：把本程序的全部设置存成若干份，随时切换；也可以导出成一段文字发给别人。
/// 2. 换机助手：把游戏的线上设置、键位、画质文件打包成一段文字，在另一台电脑上粘贴还原。
/// 文字格式沿用原版的标签：&lt;qtaPcs s=原始大小&gt;BASE64(ZIP)&lt;/qtaPcs&gt;，键位是 qtaCtrl，画质是 qtaGfx，本程序配置是 qtaIni。
/// 压缩包是标准 ZIP、里面只有一个文件；原版用的是易语言压缩支持库，两边能否互相识别没有验证过。
/// </summary>
public sealed class ProfileService(Settings settings, GameService game, GameFilesService files, LogService log)
{
    private string SlotDir => Path.Combine(settings.DataDirectory, "Ini");
    private string SlotFile(int slot) => Path.Combine(SlotDir, $"Profile{slot}.ini");

    // ───────── 配置存档 ─────────

    /// <summary>当前这份配置的名字，原版默认 Untitled。</summary>
    public string CurrentName
    {
        get => settings.Get("本地", "配置名称", "Untitled");
        set => settings.Set("本地", "配置名称", string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Trim());
    }

    /// <summary>已有的存档：(编号, 存档里记的名字)。</summary>
    public List<(int slot, string name)> ListSlots()
    {
        var list = new List<(int, string)>();
        if (!Directory.Exists(SlotDir)) return list;
        foreach (var file in Directory.GetFiles(SlotDir, "Profile*.ini"))
        {
            var match = Regex.Match(Path.GetFileName(file), @"^Profile(\d+)\.ini$");
            if (!match.Success) continue;
            var name = new Settings(file).Get("本地", "配置名称", "Untitled");
            list.Add((int.Parse(match.Groups[1].Value), name));
        }
        list.Sort();
        return list;
    }

    /// <summary>保存当前存档：把现在的配置文件复制到指定编号。</summary>
    public FileResult SaveSlot(int slot)
    {
        if (slot < 1) return new FileResult(false, Lang.C(355));
        try
        {
            Directory.CreateDirectory(SlotDir);
            File.Copy(settings.FilePath, SlotFile(slot), overwrite: true);
            return new FileResult(true, $"已保存到存档 {slot}。");
        }
        catch (Exception ex) { return new FileResult(false, "保存失败：" + ex.Message); }
    }

    /// <summary>读取指定存档：覆盖现在的配置文件。之后必须重启程序才会生效。</summary>
    public FileResult LoadSlot(int slot)
    {
        if (!File.Exists(SlotFile(slot))) return new FileResult(false, Lang.C(356));
        try
        {
            File.Copy(SlotFile(slot), settings.FilePath, overwrite: true);
            return new FileResult(true, $"已读取存档 {slot}，重启后生效。");
        }
        catch (Exception ex) { return new FileResult(false, "读取失败：" + ex.Message); }
    }

    public FileResult DeleteSlot(int slot)
    {
        if (!File.Exists(SlotFile(slot))) return new FileResult(false, Lang.C(356));
        try
        {
            File.Delete(SlotFile(slot));
            return new FileResult(true, $"已删除存档 {slot}。");
        }
        catch (Exception ex) { return new FileResult(false, "删除失败：" + ex.Message); }
    }

    /// <summary>删除存档并应用默认：删掉现在的配置文件，重启后一切回到默认值。</summary>
    public FileResult ResetCurrent()
    {
        try
        {
            if (File.Exists(settings.FilePath)) File.Delete(settings.FilePath);
            return new FileResult(true, "已恢复默认设置，重启后生效。");
        }
        catch (Exception ex) { return new FileResult(false, "操作失败：" + ex.Message); }
    }

    public string SlotDirectory
    {
        get
        {
            Directory.CreateDirectory(SlotDir);
            return SlotDir;
        }
    }

    // ───────── 打包成文字 ─────────

    private static string Pack(string tag, string entryName, byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
            entry.Write(content);
        }
        return $"<{tag} s={content.Length}>{Convert.ToBase64String(buffer.ToArray())}</{tag}>";
    }

    /// <summary>从一段文字里取出指定标签的内容。找不到返回 null；找到但损坏时 error 给出原因。</summary>
    private static byte[]? Unpack(string text, string tag, out string error)
    {
        error = "";
        var match = Regex.Match(text, $@"<{tag} s=(\d+)>([A-Za-z0-9+/=\s]+)</{tag}>");
        if (!match.Success) return null;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(match.Groups[2].Value)), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault();
            if (entry is null) { error = "压缩包是空的"; return []; }
            if (entry.Length > 16 * 1024 * 1024) { error = "内容过大"; return []; }
            using var stream = entry.Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            var content = output.ToArray();
            if (content.Length != int.Parse(match.Groups[1].Value)) { error = "大小与标注不符"; return []; }
            return content;
        }
        catch (Exception) { error = "无法解压"; return []; }
    }

    /// <summary>复制当前存档代码：把本程序的配置导出成一段文字。</summary>
    public string ExportConfig() => CurrentName + Pack("qtaIni", Path.GetFileName(settings.FilePath), File.ReadAllBytes(settings.FilePath));

    /// <summary>粘贴到当前存档：用一段配置代码覆盖现在的配置文件，重启后生效。</summary>
    public FileResult ImportConfig(string code)
    {
        var content = Unpack(code, "qtaIni", out var error);
        if (content is null) return new FileResult(false, "剪贴板里没有配置代码。");
        if (error.Length > 0) return new FileResult(false, $"配置代码已损坏（{error}）。");
        try
        {
            var text = Encoding.UTF8.GetString(content);
            if (!text.Contains('[')) return new FileResult(false, "配置代码的内容不是配置文件。");
            File.WriteAllBytes(settings.FilePath, content);
            return new FileResult(true, "已导入配置，重启后生效。");
        }
        catch (Exception ex) { return new FileResult(false, "导入失败：" + ex.Message); }
    }

    // ───────── 换机助手 ─────────

    private string ProfileDir(string profile) => Path.Combine(files.ProfilesDir, profile);

    /// <summary>导出：把指定存档目录里选中的文件打包成一段文字。report 里是每一项的结果。</summary>
    public string ExportMigration(string profile, MigrationParts parts, bool cleanSettings, out string report)
    {
        var code = new StringBuilder();
        var notes = new StringBuilder();
        void Add(string label, string tag, string file, Func<byte[], byte[]>? transform = null)
        {
            if (!File.Exists(file)) { notes.AppendLine($"{label}：{Lang.C(160)}"); return; }
            try
            {
                var content = File.ReadAllBytes(file);
                if (transform is not null) content = transform(content);
                code.Append(Pack(tag, Path.GetFileName(file), content));
                notes.AppendLine($"{label}：已包含（{content.Length} 字节）");
            }
            catch (Exception) { notes.AppendLine($"{label}：{Lang.C(159)}"); }
        }

        if (parts.HasFlag(MigrationParts.Settings))
            Add("线上设置 pc_settings.bin", "qtaPcs", Path.Combine(ProfileDir(profile), "pc_settings.bin"),
                cleanSettings ? GameFilesService.CleanRecords : null);
        if (parts.HasFlag(MigrationParts.KeyBindings))
            Add("键位 user.xml", "qtaCtrl", Path.Combine(ProfileDir(profile), "control", "user.xml"));
        if (parts.HasFlag(MigrationParts.Graphics))
            Add("画质 settings.xml", "qtaGfx", files.GraphicsFile);
        report = notes.ToString().TrimEnd();
        return code.ToString();
    }

    /// <summary>导入：把代码里带的文件写到指定存档目录。代码里没有的项目不动。</summary>
    public FileResult ImportMigration(string profile, string code)
    {
        if (game.IsRunning()) return new FileResult(false, Lang.C(37));
        if (profile.Length == 0 || !Directory.Exists(ProfileDir(profile))) return new FileResult(false, "没有选择有效的存档目录。");
        var notes = new StringBuilder();
        var any = false;

        void Apply(string label, string tag, string target, Func<byte[], byte[]>? transform = null)
        {
            var content = Unpack(code, tag, out var error);
            if (content is null) return; // 代码里没有这一项
            if (error.Length > 0) { notes.AppendLine($"{label}：代码已损坏（{error}）"); return; }
            try
            {
                if (transform is not null) content = transform(content);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    File.Copy(target, target + ".bak", overwrite: true); // 覆盖前留一份
                }
                File.WriteAllBytes(target, content);
                notes.AppendLine($"{label}：已还原（原文件备份为 .bak）");
                any = true;
            }
            catch (Exception ex) { notes.AppendLine($"{label}：写入失败（{ex.Message}）"); }
        }

        Apply("线上设置 pc_settings.bin", "qtaPcs", Path.Combine(ProfileDir(profile), "pc_settings.bin"));
        Apply("键位 user.xml", "qtaCtrl", Path.Combine(ProfileDir(profile), "control", "user.xml"));
        Apply("画质 settings.xml", "qtaGfx", files.GraphicsFile, KeepLocalHardware);

        if (notes.Length == 0) return new FileResult(false, "这段文字里没有换机代码。");
        log.Write("换机助手：" + notes.ToString().Replace(Environment.NewLine, "；").TrimEnd('；'), any ? 1 : 2);
        return new FileResult(any, notes.ToString().TrimEnd());
    }

    /// <summary>
    /// GFX_克隆硬件信息：别人的画质文件里记着别人的显卡，直接用会被游戏当成「换了硬件」而重置。
    /// 所以导入时把显卡相关的三项换成本机现有画质文件里的值。
    /// </summary>
    private byte[] KeepLocalHardware(byte[] imported)
    {
        if (!File.Exists(files.GraphicsFile)) return imported;
        var local = File.ReadAllText(files.GraphicsFile);
        var text = Encoding.UTF8.GetString(imported);
        foreach (var tag in new[] { "VideoCardDescription", "VideoDriverDescription", "MinimumVideoDriverDescription" })
        {
            var pattern = new Regex($"(<{tag}>)([^<]*)(</{tag}>)");
            var mine = pattern.Match(local);
            if (mine.Success) text = pattern.Replace(text, m => m.Groups[1].Value + mine.Groups[2].Value + m.Groups[3].Value, 1);
        }
        return Encoding.UTF8.GetBytes(text);
    }
}
