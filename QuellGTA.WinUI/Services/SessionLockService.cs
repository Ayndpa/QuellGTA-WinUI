using System.Text;

namespace QuellGTA.Services;

public enum LockState
{
    /// <summary>没有记录过游戏路径（游戏从未在本程序运行期间启动过）。</summary>
    NoGamePath,
    /// <summary>游戏目录里没有 x64\data，文件不完整或不是游戏进程。</summary>
    BadGameDir,
    /// <summary>自定义进程不支持战局锁。</summary>
    Unsupported,
    Unlocked,
    Locked,
    /// <summary>有锁文件，但读不出密码。</summary>
    Corrupted,
}

public readonly record struct LockStatus(LockState State, string Password, string Message);

/// <summary>
/// 战局锁（增强版锁文件由 k7Ysh5A_41 制作）：在游戏目录 x64\data\startup.meta 里写入一段带「密码」的内容，
/// 只有使用相同密码的玩家才会匹配进同一个战局。移植自「_战局锁锁定按钮s_被单击 / _战局锁解锁按钮_被单击 /
/// _刷新战局锁状态_被选择」。
/// </summary>
public sealed class SessionLockService(Settings settings, GameFilesService files, LogService log)
{
    private const string Placeholder = "QuellGTA";
    private const string EnhancedEnd = "</CDataFileMgr__ContentsOfDataFileXml>";

    private string MetaFile => Path.Combine(files.InstallDir, "x64", "data", "startup.meta");

    /// <summary>记住用过的密码，用「|」分隔，最近用的在前。</summary>
    public string[] SavedPasswords
    {
        get => settings.Get("扩展", "战局密码", "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        private set => settings.Set("扩展", "战局密码", string.Join('|', value));
    }

    public void ClearSavedPasswords() => SavedPasswords = [];

    private bool? IsEnhanced() => files.InstallExeName.ToLowerInvariant() switch
    {
        "gta5_enhanced.exe" => true,
        "gta5.exe" => false,
        _ => null,
    };

    /// <summary>刷新战局锁状态</summary>
    public LockStatus GetStatus()
    {
        if (files.InstallDir.Length == 0) return new(LockState.NoGamePath, "", Lang.C(200));
        if (!Directory.Exists(Path.Combine(files.InstallDir, "x64", "data"))) return new(LockState.BadGameDir, "", Lang.C(201));
        if (IsEnhanced() is not { } enhanced) return new(LockState.Unsupported, "", Lang.C(332));
        if (!File.Exists(MetaFile)) return new(LockState.Unlocked, "", Lang.C(202));

        string text;
        try { text = File.ReadAllText(MetaFile, Encoding.UTF8); }
        catch (Exception) { return new(LockState.Corrupted, "", Lang.C(548)); }

        string? password = null;
        if (enhanced)
        {
            // 增强版：密码追加在 XML 结束标签之后
            var end = text.IndexOf(EnhancedEnd, StringComparison.Ordinal);
            if (end >= 0) password = text[(end + EnhancedEnd.Length)..];
        }
        else
        {
            // 传承版：密码放在第一个 XML 注释里
            var open = text.IndexOf("<!--", StringComparison.Ordinal);
            var close = open >= 0 ? text.IndexOf("-->", open, StringComparison.Ordinal) : -1;
            if (close > open) password = text[(open + 4)..close];
        }
        password = password?.Trim();
        return string.IsNullOrEmpty(password)
            ? new(LockState.Corrupted, "", Lang.C(548))
            : new(LockState.Locked, password, Lang.C(203) + password);
    }

    /// <summary>创建战局锁，重启游戏后生效。</summary>
    public FileResult Lock(string password)
    {
        password = password.Trim();
        if (password.Length == 0) return new(false, Lang.C(206));
        if (password.Contains('|')) return new(false, Lang.C(205));
        // 密码会原样写进 XML 文件，这几种字符会破坏文件结构
        if (password.Contains('<') || password.Contains('>') || password.Contains("--") || password.Contains('&'))
            return new(false, "密码中不能包含 < > & 和连续的减号。");

        var status = GetStatus();
        if (status.State is LockState.NoGamePath) return new(false, Lang.C(204));
        if (status.State is LockState.BadGameDir or LockState.Unsupported) return new(false, status.Message);

        var enhanced = IsEnhanced() == true;
        var template = Path.Combine(AppContext.BaseDirectory, "Assets", "SessionLock",
            enhanced ? "startup_enhanced.meta" : "startup_legacy.meta");
        try
        {
            var content = File.ReadAllText(template, Encoding.UTF8).Replace(Placeholder, password);
            File.WriteAllText(MetaFile, content, new UTF8Encoding(false));
        }
        catch (Exception)
        {
            return new(false, Lang.C(209));
        }

        SavedPasswords = SavedPasswords.Where(p => p != password).Prepend(password).Take(10).ToArray();
        log.Write(Lang.C(208) + password, 1);
        return new(true, Lang.C(208) + password + (enhanced ? "\n\n" + Lang.C(333) : ""));
    }

    /// <summary>删除战局锁，重启游戏后生效。</summary>
    public FileResult Unlock()
    {
        if (files.InstallDir.Length == 0) return new(false, Lang.C(210));
        try
        {
            if (File.Exists(MetaFile)) File.Delete(MetaFile);
        }
        catch (Exception) { }
        if (File.Exists(MetaFile)) return new(false, Lang.C(211));
        log.Write(Lang.C(212), 1);
        return new(true, Lang.C(212));
    }
}
