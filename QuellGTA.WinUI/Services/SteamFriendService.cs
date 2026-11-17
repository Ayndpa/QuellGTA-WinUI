using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace QuellGTA.Services;

public enum FriendLookup
{
    /// <summary>连不上 Steam 的调试端口（Steam 没开，或没开 CEF 远程调试）。</summary>
    ConnectFail,
    /// <summary>连上了，但没拿到这个好友的加入信息（不在线上、好友列表没加载等）。</summary>
    NotFound,
    /// <summary>拿到了，Connect 里是好友当前的启动参数（-steamjvp=…）。</summary>
    Found,
}

/// <summary>
/// 通过 SteamID64 查好友当前所在的战局，移植自原版内嵌的 GetJVP.exe（https://github.com/mageangela/QuellGTA_GetJvp）。
/// 做法：Steam 客户端开启 CEF 远程调试后会在本机 8080 端口提供调试接口，
/// 连到其中的 SharedJSContext 页面，读取好友状态里的 connect 字段——也就是好友列表里「加入游戏」按钮用的那段参数。
/// 只和本机的 Steam 客户端通信，不访问任何网络服务。
/// </summary>
public static class SteamFriendService
{
    private const string DebugEndpoint = "http://127.0.0.1:8080/json";
    private const string MarkerFile = ".cef-enable-remote-debugging";

    public static bool IsSteamId(string text) =>
        text.Length == 17 && text.StartsWith("7656119", StringComparison.Ordinal) && text.All(char.IsAsciiDigit);

    private static async Task<string?> GetDebuggerUrlAsync(CancellationToken token)
    {
        // 本机地址，不走系统代理
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        var json = await http.GetStringAsync(DebugEndpoint, token);
        using var document = JsonDocument.Parse(json);
        foreach (var target in document.RootElement.EnumerateArray())
            if (target.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String && title.GetString() == "SharedJSContext"
                && target.TryGetProperty("webSocketDebuggerUrl", out var url) && url.ValueKind == JsonValueKind.String)
                return url.GetString();
        return null;
    }

    /// <summary>只测试能不能连上 Steam 的调试接口。</summary>
    public static async Task<bool> TestAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            return await GetDebuggerUrlAsync(cts.Token) is not null;
        }
        catch (Exception) { return false; }
    }

    /// <summary>查询好友的加入参数。</summary>
    public static async Task<(FriendLookup result, string connect)> QueryAsync(string steamId)
    {
        // steamId 会被拼进一段脚本里，只允许 17 位数字
        if (!IsSteamId(steamId)) return (FriendLookup.NotFound, "");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var url = await GetDebuggerUrlAsync(cts.Token);
            if (url is null) return (FriendLookup.ConnectFail, "");

            using var socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            await socket.ConnectAsync(new Uri(url), cts.Token);

            // 与 GetJVP 的脚本一致：SteamID64 减去固定偏移得到账号编号，再到好友状态里取 connect
            var script = """
                (function() {
                    try {
                        const acc = Number(BigInt("__SID__") - 76561197960265728n);
                        if (typeof friendStore === 'undefined' || !friendStore.GetFriendState) return "ERR|friendStore object not found";
                        const state = friendStore.GetFriendState(acc);
                        if (!state) return "ERR|GetFriendState returned null";
                        const conn = state?.m_persona?.m_mapRichPresence?.data_?.get("connect")?.value_;
                        return conn ? "OK|" + String(conn) : "NOCONN|No connect field currently";
                    } catch (e) { return "ERR|JS Exception"; }
                })()
                """.Replace("__SID__", steamId);
            var request = JsonSerializer.Serialize(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new { expression = script, returnByValue = true },
            });
            await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, cts.Token);

            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, cts.Token);
                    if (received.MessageType == WebSocketMessageType.Close) return (FriendLookup.ConnectFail, "");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);

                using var reply = JsonDocument.Parse(message.ToArray());
                var root = reply.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != 1) continue;
                if (!root.TryGetProperty("result", out var outer) || outer.TryGetProperty("exceptionDetails", out _)
                    || !outer.TryGetProperty("result", out var inner)
                    || !inner.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                    return (FriendLookup.NotFound, "");
                var text = value.GetString() ?? "";
                return text.StartsWith("OK|", StringComparison.Ordinal) ? (FriendLookup.Found, text[3..]) : (FriendLookup.NotFound, "");
            }
            return (FriendLookup.ConnectFail, "");
        }
        catch (Exception) { return (FriendLookup.ConnectFail, ""); }
    }

    private static string? SteamDirectory()
    {
        foreach (var process in Process.GetProcessesByName("steam"))
            using (process)
            {
                var path = GameService.GetProcessPath(process.Id);
                if (path.Length > 0 && Path.GetDirectoryName(path) is { } dir) return dir;
            }
        return null;
    }

    /// <summary>Steam 正在运行，但还没开启 CEF 远程调试（原版「sJvp_访问被拒」里弹窗询问的条件）。</summary>
    public static bool NeedsCefDebugging() =>
        SteamDirectory() is { } dir && !File.Exists(Path.Combine(dir, MarkerFile));

    /// <summary>
    /// 开启 Steam 的 CEF 远程调试：在 Steam 目录里放一个空的标记文件，然后重启 Steam。
    /// 以后想关掉，删除 Steam 目录下的 .cef-enable-remote-debugging 即可。
    /// </summary>
    public static bool EnableCefDebugging()
    {
        var dir = SteamDirectory();
        if (dir is null) return false;
        try
        {
            foreach (var process in Process.GetProcessesByName("steam"))
                using (process)
                {
                    try { process.Kill(); process.WaitForExit(3000); } catch (Exception) { }
                }
            Thread.Sleep(500);
            File.WriteAllBytes(Path.Combine(dir, MarkerFile), []);
            Thread.Sleep(500);
            // 经 explorer 启动，避免 Steam 继承本程序的管理员权限
            Shell.Run("explorer.exe", $"\"{Path.Combine(dir, "steam.exe")}\"", wait: false);
            return true;
        }
        catch (Exception) { return false; }
    }
}
