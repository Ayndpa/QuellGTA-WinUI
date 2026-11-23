using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuellGTA.Services;

/// <summary>移植版的一个发布：版本号、说明、下载地址。</summary>
public sealed record PortRelease(Version Version, string Tag, string Notes, string AssetName, string DownloadUrl, long Size);

/// <summary>
/// 移植版自己的原地更新（对应原版「下载新版本并替换自身」）。移植版发布在 GitHub Releases：
/// 取最新一个发布，版本比当前新就下载其中的 .zip，解压到数据目录，然后启动一个小脚本——
/// 等本程序退出后把新文件盖到程序目录，再把程序启动起来。
/// </summary>
public sealed class SelfUpdateService(Settings settings, LogService log)
{
    public const string Repository = "Ayndpa/QuellGTA-WinUI";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";
    private const string LatestApi = $"https://api.github.com/repos/{Repository}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QuellGTA-WinUI-Updater");
        return http;
    }

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    private string UpdateDir => Path.Combine(settings.DataDirectory, "Update");

    /// <summary>查最新发布。没有发布、取不到或解析失败时返回 null，error 给出原因。</summary>
    public async Task<(PortRelease? release, string error)> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, "移植版还没有发布任何版本。");
            // 未登录的 API 每个 IP 每小时只能查 60 次，用完返回 403 / 429；这时改从网页上取
            if ((int)response.StatusCode is 403 or 429) return await CheckFromWebAsync();
            if (!response.IsSuccessStatusCode) return (null, $"GitHub 返回 {(int)response.StatusCode}。");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out var version)) return (null, $"无法识别发布的版本号「{tag}」。");
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                return (new PortRelease(version, tag, notes.Trim(), name,
                    asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64()), "");
            }
            return (null, $"发布 {tag} 里没有 .zip 安装包。");
        }
        catch (Exception ex) { return (null, "检查失败：" + ex.Message); }
    }

    /// <summary>
    /// 不走 API 的查法：releases/latest 会跳转到最新发布的 tag 页面，再从该发布的附件列表页里找 .zip 链接。
    /// 取不到更新说明和文件大小。
    /// </summary>
    private static async Task<(PortRelease? release, string error)> CheckFromWebAsync()
    {
        using var latest = await Http.GetAsync($"https://github.com/{Repository}/releases/latest");
        var final = latest.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        const string marker = "/releases/tag/";
        var at = final.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return latest.IsSuccessStatusCode ? (null, "移植版还没有发布任何版本。") : (null, $"GitHub 返回 {(int)latest.StatusCode}。");
        var tag = Uri.UnescapeDataString(final[(at + marker.Length)..]);
        if (!TryParseVersion(tag, out var version)) return (null, $"无法识别发布的版本号「{tag}」。");
        var html = await Http.GetStringAsync($"https://github.com/{Repository}/releases/expanded_assets/{Uri.EscapeDataString(tag)}");
        var pattern = "href=\"(/" + Regex.Escape(Repository) + "/releases/download/[^\"]+?\\.zip)\"";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return (null, $"发布 {tag} 里没有 .zip 安装包。");
        var path = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
        var name = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        return (new PortRelease(version, tag, "", name, "https://github.com" + path, 0), "");
    }

    /// <summary>v4.22.216.1、4.22.216 之类的标签都认。</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+', ' ']);
        if (dash > 0) text = text[..dash];
        if (Version.TryParse(text, out var parsed))
        {
            // 补齐成四段，方便和程序集版本比较
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    public static bool IsNewer(PortRelease release) => release.Version > CurrentVersion;

    /// <summary>
    /// 下载并解压新版本，然后启动替换脚本。返回 null 表示一切就绪，调用方应立刻退出程序；否则返回错误说明。
    /// </summary>
    public async Task<string?> DownloadAndStageAsync(PortRelease release, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exe = Environment.ProcessPath;
        if (exe is null) return "取不到本程序的路径。";
        try
        {
            if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true);
            Directory.CreateDirectory(UpdateDir);
            var zip = Path.Combine(UpdateDir, "package.zip");

            using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.Size;
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = File.Create(zip);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            var staged = Path.Combine(UpdateDir, "files");
            ZipFile.ExtractToDirectory(zip, staged);
            // 压缩包里多包了一层文件夹时，往下找到放着主程序的那一层
            var exeName = Path.GetFileName(exe);
            var root = staged;
            while (!File.Exists(Path.Combine(root, exeName)))
            {
                var dirs = Directory.GetDirectories(root);
                if (dirs.Length != 1 || Directory.GetFiles(root).Length > 0) return $"安装包里找不到 {exeName}，没有更新。";
                root = dirs[0];
            }

            // 替换脚本：等本进程退出 → 复制新文件 → 启动新版本。robocopy 的返回值小于 8 表示成功
            var script = Path.Combine(UpdateDir, "apply.cmd");
            var pid = Environment.ProcessId;
            File.WriteAllText(script, $"""
                @echo off
                chcp 65001 >nul
                :wait
                tasklist /fi "PID eq {pid}" | find "{pid}" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)
                robocopy "{root}" "{appDir}" /E /R:5 /W:1 /NFL /NDL /NJH /NJS >nul
                if errorlevel 8 (echo robocopy failed > "{Path.Combine(UpdateDir, "failed.txt")}" & start "" "{exe}" & exit /b 1)
                start "" "{exe}"
                """, new System.Text.UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = UpdateDir,
            });
            log.Write($"已下载移植版 {release.Tag}，程序即将退出并完成更新。", 1);
            return null;
        }
        catch (OperationCanceledException) { return "已取消。"; }
        catch (Exception ex) { return "更新失败：" + ex.Message; }
    }

    /// <summary>启动时清理上一次更新留下的临时文件。</summary>
    public void CleanUp()
    {
        if (!Directory.Exists(UpdateDir)) return;
        if (File.Exists(Path.Combine(UpdateDir, "failed.txt")))
            log.Write($"上次更新没有完成：新文件无法覆盖到程序目录。可以到 {ReleasesPage} 手动下载。", 2);
        try { Directory.Delete(UpdateDir, true); } catch (Exception) { }
    }
}
