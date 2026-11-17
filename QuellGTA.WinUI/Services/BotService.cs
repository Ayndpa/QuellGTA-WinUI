using System.Diagnostics;
using System.Text;

namespace QuellGTA.Services;

public sealed record BotEntry(string Name, string Jvp);

/// <summary>在线 Bot 列表：日期行、公告行，以及若干个 Bot。</summary>
public sealed record BotList(string Date, string Notice, IReadOnlyList<BotEntry> Bots);

public sealed record PublicServer(string Name, string Address);

/// <summary>
/// Bot 差传与战局直达（原版「战局直达」菜单）：把一段 SteamJVP（Steam 的战局邀请参数）交给 Steam，
/// 让正在运行的增强版直接加入对应的战局。差传 Bot 由社区志愿者提供（傲弗拉、小苦lxcoo、小羊咩咩等），
/// 这里只做 HTTP GET，除了软件版本标识之外不上传任何信息。
/// Bot 列表优先走原版的主线路，失败时改用两条辅助线路（xymm、lxcoo）。
/// 主线路只接受带 QuellGTA 版本标识的请求（原版「网页_访问_对象」的「专用 UA」）。经原作者同意，移植版也使用主线路，
/// 请求格式与原版一致，并在版本标识后面如实注明自己是 WinUI 移植版，方便服务方区分。
/// 原版通过内嵌的 GetJVP.exe 按 Steam 好友 ID 取战局的方式没有移植。
/// </summary>
public sealed class BotService(Settings settings, GameService game, LogService log, SoundService sound)
{
    private const string Header = "# QuellGTA-SteamJVP";
    private const string JoinUrl = "steam://rungame/3240220/76561199074735990/-steamjvp%3D";
    private const string MainSource = "http://quellgtacode.mageangela.cn:52014/botJvp/";
    private const string StatusSource = "http://quellgtacode.mageangela.cn:52014/";
    private const string LxcooIpSource = "http://ip.lxcoo.top/";
    private const string XymmSource = "https://xymmgame.cn/GTA5/bot.txt";
    private static readonly string[] ServerListSources =
    [
        "http://quellgtadata.mageangela.cn/database/QuellGTA/qfslist.txt",
        "http://quellgtacode.mageangela.cn/database/QuellGTA/qfslist.txt",
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Encoding Gbk;

    static BotService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    }

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private long _lastRefresh = long.MinValue / 2;
    private long _lastJoin;

    public BotList? Bots { get; private set; }

    /// <summary>两个快捷槽位（甲 / 乙）各自要加入的目标，见 <see cref="DescribeTarget"/>。原版默认 BOT1。</summary>
    public string GetSlot(string slot) => settings.Get("联机", "加战局" + slot, "BOT1");
    public void SetSlot(string slot, string target) => settings.Set("联机", "加战局" + slot, target.Trim());

    private static async Task<string> GetTextAsync(string url, Encoding encoding)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            return encoding.GetString(bytes);
        }
        catch (Exception) { return ""; }
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// 按原版「网页_访问_对象」的方式请求主线路：同样的 Accept / Referer / Accept-Language，
    /// User-Agent 末尾带「QuellGTA/版本号」，再加上移植版自己的标识。
    /// </summary>
    private static async Task<string> GetFromMainAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var port = typeof(BotService).Assembly.GetName().Version?.ToString(3) ?? "0";
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36 "
                + $"QuellGTA/{TeamService.BuildVersion} QuellGTA-WinUI/{port}");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.8,*/*;q=0.6");
            request.Headers.TryAddWithoutValidation("Referer", url);
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en-US;q=0.7,en;q=0.5");
            using var response = await Http.SendAsync(request);
            return Gbk.GetString(await response.Content.ReadAsByteArrayAsync());
        }
        catch (Exception) { return ""; }
    }

    /// <summary>botJvp解压：服务器为了省流量，把 JVP 里固定重复的三段内容换成了短标记。</summary>
    public static string Decompress(string text) => text
        .Replace("<p>", "AAAAAAEaRd")
        .Replace("<br>", "%2F%2F%2F%2F%2FwAA%2F%2F%2F%2F%2FwAA")
        .Replace("</p>", "%3D%3D");

    /// <summary>刷新 Bot 列表。60 秒内重复调用直接返回缓存（原版同样限制）。主线路失败时改用辅助线路。</summary>
    public async Task<BotList?> RefreshAsync(bool force = false)
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (!force && Bots is not null && Environment.TickCount64 - _lastRefresh < 60000) return Bots;

            var lines = Lines(Decompress(await GetFromMainAsync(MainSource)));
            if (lines.Length < 3 || lines[0] != Header) lines = await AuxiliaryAsync();
            if (lines.Length < 3 || lines[0] != Header)
            {
                Bots = null;
                log.Write(Lang.C(339), 1);
                return null;
            }

            var bots = new List<BotEntry>();
            foreach (var line in lines.Skip(3))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split('|');
                if (parts.Length == 2 && parts[1].Length > 0) bots.Add(new BotEntry(parts[0], parts[1].Trim()));
            }
            Bots = new BotList(lines[1].StartsWith('#') ? "" : lines[1], lines[2].StartsWith('#') ? "" : lines[2], bots);
            _lastRefresh = Environment.TickCount64;
            return Bots;
        }
        finally { _refreshLock.Release(); }
    }

    /// <summary>辅助线路：lxcoo 的服务器（先查它当前的 IP）和 xymm 的 bot.txt，合并成同样的格式。</summary>
    private static async Task<string[]> AuxiliaryAsync()
    {
        var result = new List<string> { Header, "辅助线路", "" };
        var xymm = (await GetTextAsync(XymmSource, Encoding.UTF8)).Trim();
        if (xymm.EndsWith("%3D", StringComparison.Ordinal)) result.Add("XymmBot|" + xymm);

        var ip = (await GetTextAsync(LxcooIpSource, Encoding.UTF8)).Trim();
        if (ip.Length is > 7 and < 15 && System.Net.IPAddress.TryParse(ip, out _))
        {
            var lxcoo = Lines(await GetTextAsync($"http://{ip}:52014/", Encoding.UTF8));
            if (lxcoo.Length >= 3 && lxcoo[0] == Header)
            {
                result.Add("LxcooBot-1|" + lxcoo[1]);
                result.Add("LxcooBot-2|" + lxcoo[2]);
            }
        }
        return result.Count > 3 ? [.. result] : [];
    }

    /// <summary>查询 Bot 运行状态：返回主线路给出的两行说明，失败返回 null。</summary>
    public async Task<string?> QueryStatusAsync()
    {
        var text = await GetTextAsync(StatusSource, Gbk);
        if (!Lines(text).FirstOrDefault("").Equals(Header, StringComparison.Ordinal)) return null;
        var parts = text.Replace("\r\n", "\n").Split("；\n");
        return parts.Length >= 3 ? parts[1].Trim() + "\n" + parts[2].Trim() : null;
    }

    /// <summary>刷新公益服列表（联机广播用的公共服务器）。两条线路都失败时返回原版内置的预设地址。</summary>
    public async Task<(List<PublicServer> servers, bool online)> GetPublicServersAsync()
    {
        foreach (var source in ServerListSources)
        {
            var lines = Lines(await GetTextAsync(source, Gbk));
            if (lines.Length == 0 || lines[0] != "# QuellGTA-qfsList") continue;
            var servers = lines.Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('|'))
                .Where(p => p.Length == 2).Select(p => new PublicServer(p[0], p[1].Trim())).ToList();
            log.Write(Lang.C(378), 1);
            return (servers, true);
        }
        log.Write(Lang.C(377), 1);
        return ([new PublicServer(Lang.C(466), "qfs1.mageangela.cn:12345")], false);
    }

    // ───────── 加入 ─────────

    /// <summary>把槽位里保存的目标翻译成给人看的说明。</summary>
    public static string DescribeTarget(string target)
    {
        if (target.Length == 0) return "未启用";
        if (target.StartsWith("BOT", StringComparison.Ordinal) && int.TryParse(target[3..], out var n)) return $"在线 Bot 第 {n} 个";
        if (target == "LAST1") return "最近一次加入的战局";
        if (SteamFriendService.IsSteamId(target)) return "Steam 好友所在的战局（" + target + "）";
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "从网页获取：" + target;
        return "自定义 SteamJVP（" + (target.Length > 16 ? target[..16] + "…" : target) + "）";
    }

    /// <summary>
    /// Steam 差传只对正在运行的增强版有效：链接里写死了增强版的 Steam 应用编号，而且需要游戏已经登录进线上。
    /// </summary>
    private bool CanJoin()
    {
        if (!game.IsRunning() || !game.ProcessName.Equals("GTA5_Enhanced.exe", StringComparison.OrdinalIgnoreCase))
        {
            log.Write(Lang.C(322));
            return false;
        }
        if (Environment.TickCount64 - Interlocked.Exchange(ref _lastJoin, Environment.TickCount64) < 1000)
        {
            log.Write(Lang.C(327));
            return false;
        }
        return true;
    }

    private void Open(string jvp, string name = "")
    {
        // 用 explorer 打开，避免 Steam 继承本程序的管理员权限
        Shell.Run("explorer.exe", $"\"{JoinUrl}{jvp}\"", wait: false);
        log.Write(Lang.C(324) + name, 1);
    }

    /// <summary>加入某个槽位保存的目标（「甲」或「乙」），对应原版「_加甲战局按钮_被单击」。</summary>
    public async Task JoinSlotAsync(string slot) => await JoinTargetAsync(GetSlot(slot));

    public async Task JoinTargetAsync(string target)
    {
        target = target.Trim();
        if (target.Length == 0) { log.Write(Lang.C(325)); return; }
        if (!CanJoin()) return;
        sound.Play(12);

        if (target.StartsWith("BOT", StringComparison.Ordinal) && int.TryParse(target[3..], out var index))
        {
            var list = await RefreshAsync();
            if (list is null || index < 1 || index > list.Bots.Count) { log.Write(Lang.C(326), 1); return; }
            Open(list.Bots[index - 1].Jvp, list.Bots[index - 1].Name);
        }
        else if (target == "LAST1")
        {
            var jvp = ReadLastJoined(out var error);
            if (jvp is null) { log.Write(error, 1); return; }
            Open(jvp);
        }
        else if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // hJVP_从网页加入：网页里用 <SteamJVP>…</SteamJVP> 标出要加入的战局
            var page = await GetTextAsync(target, Encoding.UTF8);
            var start = page.IndexOf("<SteamJVP>", StringComparison.OrdinalIgnoreCase);
            var end = page.IndexOf("</SteamJVP>", StringComparison.OrdinalIgnoreCase);
            if (start < 0 || end <= start) { log.Write(Lang.C(557), 2); return; }
            var jvp = page[(start + 10)..end].Trim();
            if (!IsValidJvp(jvp)) { log.Write(Lang.C(557), 2); return; }
            log.Write(Lang.C(555), 1);
            Open(jvp);
        }
        else if (target.StartsWith("7656119", StringComparison.Ordinal))
        {
            // sJvp_加入_线程：按 SteamID64 向本机的 Steam 客户端查这位好友现在所在的战局
            if (!SteamFriendService.IsSteamId(target)) { log.Write(Lang.C(409)); return; }
            var (result, connect) = await SteamFriendService.QueryAsync(target);
            const string prefix = "-steamjvp=";
            if (result == FriendLookup.ConnectFail)
            {
                log.Write(Lang.C(552), 2);
                if (SteamFriendService.NeedsCefDebugging()) CefDebuggingNeeded?.Invoke();
            }
            else if (result == FriendLookup.Found && connect.StartsWith(prefix, StringComparison.Ordinal)
                     && Uri.EscapeDataString(connect[prefix.Length..]) is { Length: > 0 } jvp && IsValidJvp(jvp))
            {
                log.Write(Lang.C(555), 1);
                Open(jvp);
            }
            else
            {
                log.Write(Lang.C(554), 2);
            }
        }
        else if (IsValidJvp(target))
        {
            Open(target);
        }
        else
        {
            log.Write(Lang.C(409));
        }
    }

    /// <summary>按好友加入时连不上 Steam，而且 Steam 还没开启 CEF 远程调试：界面收到后询问要不要开启。</summary>
    public event Action? CefDebuggingNeeded;

    /// <summary>直接加入列表里的某个 Bot。</summary>
    public void JoinBot(BotEntry bot)
    {
        if (!CanJoin()) return;
        sound.Play(12);
        Open(bot.Jvp, bot.Name);
    }

    /// <summary>JVP 会被拼进一条命令行里，只允许 URL 编码后的 Base64 字符，防止夹带别的参数。</summary>
    public static bool IsValidJvp(string jvp) =>
        jvp.Length is > 8 and < 4096 && jvp.All(c => char.IsAsciiLetterOrDigit(c) || c is '%' or '-' or '_' or '.' or '+' or '/' or '=');

    /// <summary>
    /// 提取最近加入的 SteamJVP：Steam 会把启动参数写进 logs\console_log.txt，取其中最后一条。
    /// 需要 Steam 正在运行（用来定位它的安装目录）。
    /// </summary>
    public static string? ReadLastJoined(out string error)
    {
        error = "";
        var steam = Process.GetProcessesByName("steam");
        try
        {
            if (steam.Length == 0) { error = Lang.C(334); return null; }
            var exe = GameService.GetProcessPath(steam[0].Id);
            if (exe.Length == 0) { error = Lang.C(334); return null; }
            var logFile = Path.Combine(Path.GetDirectoryName(exe)!, "logs", "console_log.txt");
            if (!File.Exists(logFile)) { error = Lang.C(335); return null; }

            string text;
            using (var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                text = reader.ReadToEnd();
            const string marker = "-steamjvp%3D";
            var start = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0) { error = Lang.C(335); return null; }
            start += marker.Length;
            var end = start;
            while (end < text.Length && text[end] is not ('"' or ' ' or '\r' or '\n')) end++;
            var jvp = text[start..end];
            if (!IsValidJvp(jvp)) { error = Lang.C(335); return null; }
            return jvp;
        }
        catch (Exception)
        {
            error = Lang.C(335);
            return null;
        }
        finally
        {
            foreach (var process in steam) process.Dispose();
        }
    }
}
