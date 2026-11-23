using System.Text;

namespace QuellGTA.Services;

/// <summary>
/// 指纹随任务切换（出自原版的一个变体版本「根据任务启停」，Build 0123 的 指纹模块启停 / 获取当前任务 / 检测游玩内容）：
/// Rockstar 启动器会把游戏里的「正在玩什么」写进 launcher.log 的 PresenceText 行。
/// 最后一条 PresenceText 里提到名钻赌场就把自动指纹切到名钻并开启，提到佩里科岛就切到佩岛并开启，都不是就关闭。
/// 原版每次把整个日志读进来、转成 ANSI 再找；这里只读文件末尾的一段，直接按 UTF-8 匹配（繁体中文也能认出来）。
/// </summary>
public sealed partial class QuellCore
{
    /// <summary>原版「指纹任务启停」。</summary>
    public bool FingerprintFollowMission
    {
        get => Settings.GetBool("扩展", "指纹任务启停", false);
        set => Settings.SetBool("扩展", "指纹任务启停", value);
    }

    private long _presenceOffset = -1;
    private string _lastPresence = "";
    private int _lastMission = -1;

    private static string LauncherLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Rockstar Games", "Launcher", "launcher.log");

    /// <summary>取最后一条 PresenceText 的内容；日志没变时返回 null。</summary>
    private string? ReadLastPresence()
    {
        var path = LauncherLog;
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == _presenceOffset) return null;
        _presenceOffset = stream.Length;
        const int tail = 256 * 1024;
        stream.Seek(Math.Max(0, stream.Length - tail), SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        const string marker = "PresenceText: ";
        var at = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return "";
        var start = at + marker.Length;
        var end = text.IndexOfAny(['\r', '\n'], start);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    }

    /// <summary>获取当前任务：1 名钻赌场，2 佩里科岛，0 其他。</summary>
    public static int ClassifyPresence(string presence)
    {
        if (presence.Contains("赌场") || presence.Contains("Casino", StringComparison.OrdinalIgnoreCase) || presence.Contains("賭場")) return 1;
        if (presence.Contains("科岛") || presence.Contains("Perico", StringComparison.OrdinalIgnoreCase) || presence.Contains("克島")) return 2;
        return 0;
    }

    /// <summary>每两秒左右调用一次（游戏运行时）。</summary>
    private void MissionTick()
    {
        if (!FingerprintFollowMission) return;
        string? presence;
        try { presence = ReadLastPresence(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        if (presence is null || presence == _lastPresence) return;
        _lastPresence = presence;
        if (presence.Length > 0) Log.Write($"当前游玩内容：[{presence}]", 1); // 检测游玩内容

        // 指纹模块启停
        var mission = ClassifyPresence(presence);
        if (mission == _lastMission) return;
        _lastMission = mission;
        switch (mission)
        {
            case 1:
                Fingerprint.Kind = FingerprintKind.Diamond;
                Fingerprint.Enabled = true;
                Log.Write("进入名钻赌场任务，自动指纹已切换到名钻并开启。", 1);
                break;
            case 2:
                Fingerprint.Kind = FingerprintKind.Perico;
                Fingerprint.Enabled = true;
                Log.Write("进入佩里科岛任务，自动指纹已切换到佩岛并开启。", 1);
                break;
            default:
                if (!Fingerprint.Enabled) break;
                Fingerprint.Enabled = false;
                Log.Write("已离开抢劫任务，自动指纹已关闭。", 1);
                break;
        }
        Notify();
    }
}
