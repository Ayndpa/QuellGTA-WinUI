using System.Text;

namespace QuellGTA.Services;

/// <summary>原版发布信息：最新版本号、更新类型（0 可选 1 正常 2 重要 3 紧急）、更新内容、下载地址。</summary>
public sealed record ReleaseInfo(string Build, int Kind, string Notes, string DownloadUrl);

/// <summary>
/// 检查更新与公告。移植版没有自己的发布渠道，所以这里查的是「原版」有没有比移植所依据的 build 更新的版本：
/// 有的话说明原版多了新功能或修复，移植版还没跟上。不会像原版那样自动下载并替换程序。
/// </summary>
public sealed class UpdateService(Settings settings, LogService log, SoundService sound)
{
    private static readonly string[] ReleaseSources =
    [
        "http://quellgtadata.mageangela.cn/database/QuellGTA/releases.txt",
        "http://quellgtacode.mageangela.cn/database/QuellGTA/releases.txt",
    ];
    private static readonly string[] NoticeSources =
    [
        "http://quellgtadata.mageangela.cn/database/QuellGTA/notice.txt",
        "http://quellgtacode.mageangela.cn/database/QuellGTA/notice.txt",
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Encoding Gbk;

    static UpdateService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    public ReleaseInfo? Latest { get; private set; }

    /// <summary>启动时自动检查原版更新。</summary>
    public bool CheckOnStartup
    {
        get => settings.GetBool("本地", "启动查更新", true);
        set => settings.SetBool("本地", "启动查更新", value);
    }

    private static async Task<string[]> GetLinesAsync(string[] sources, string header)
    {
        foreach (var source in sources)
        {
            try
            {
                var lines = Gbk.GetString(await Http.GetByteArrayAsync(source)).Replace("\r\n", "\n").Split('\n');
                if (lines.Length > 0 && lines[0].Trim() == header) return lines;
            }
            catch (Exception) { }
        }
        return [];
    }

    /// <summary>
    /// 检查更新。文件格式：去掉 # 开头的注释行之后，各段用单独一行的「-」分隔，依次是版本号、类型、更新内容、下载地址。
    /// </summary>
    public async Task<ReleaseInfo?> CheckAsync()
    {
        log.Write($"Build {TeamService.BuildVersion}{Lang.C(4)}");
        var lines = await GetLinesAsync(ReleaseSources, "# QuellGTA-Releases");
        if (lines.Length == 0)
        {
            log.Write(Lang.C(12), 1);
            return Latest = null;
        }
        var parts = string.Join("\n", lines.Where(l => !l.StartsWith('#'))).Split("\n-\n");
        _ = int.TryParse(parts.ElementAtOrDefault(1)?.Trim(), out var kind);
        Latest = new ReleaseInfo(parts[0].Trim(), kind, parts.ElementAtOrDefault(2)?.Trim() ?? "", parts.ElementAtOrDefault(3)?.Trim() ?? "");

        _ = int.TryParse(TeamService.BuildVersion, out var mine);
        if (int.TryParse(Latest.Build, out var theirs) && theirs > mine)
        {
            var level = Latest.Kind switch { 3 => 3, 2 => 2, _ => 1 };
            log.Write($"原版已发布 Build {Latest.Build}，移植版基于 Build {TeamService.BuildVersion}，可能缺少其中的新功能或修复。", level);
            if (Latest.Kind == 3) sound.Play(10);
        }
        else
        {
            log.Write($"移植所依据的 Build {TeamService.BuildVersion} 仍是原版的最新版本。");
        }
        return Latest;
    }

    /// <summary>
    /// 读取原版的公告。文件分四段：适用的最高版本、最低版本、通知类型、通知内容。
    /// 没有公告、公告不适用于 Build 0216 或取不到时返回空串。
    /// </summary>
    public async Task<string> GetNoticeAsync()
    {
        var lines = await GetLinesAsync(NoticeSources, "# QuellGTA-Notice");
        if (lines.Length == 0) return "";
        var parts = string.Join("\n", lines.Where(l => !l.StartsWith('#'))).Split("\n-\n");
        if (parts.Length < 4) return "";
        _ = int.TryParse(parts[0].Trim(), out var highest);
        _ = int.TryParse(parts[1].Trim(), out var lowest);
        _ = int.TryParse(TeamService.BuildVersion, out var mine);
        var content = parts[3].Trim();
        return mine < lowest || mine > highest || content is "" or "None" ? "" : content;
    }
}
