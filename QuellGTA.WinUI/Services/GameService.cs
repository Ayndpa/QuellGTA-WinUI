using System.Diagnostics;
using System.Text;
using QuellGTA.Native;
using static QuellGTA.Native.NativeMethods;

namespace QuellGTA.Services;

public enum GameEdition { Legacy = 1, Enhanced = 2, Custom = -1 }

/// <summary>
/// 对应原版的「游戏信息」（进程名称 / 文档目录名 / 游戏目录名）以及一组 进程_xxx 子程序：
/// 查找游戏进程、取窗口句柄、取路径、结束、暂停。
/// </summary>
public sealed class GameService(Settings settings)
{
    public GameEdition Edition { get; private set; }
    public string ProcessName { get; private set; } = "GTA5_Enhanced.exe";
    public string DocumentsDirName { get; private set; } = "GTAV Enhanced";
    public string GameDirName { get; private set; } = "Grand Theft Auto V Enhanced";

    /// <summary>「自动检测」：哪个版本的进程在运行就切到哪个版本。</summary>
    public bool AutoDetect
    {
        get => settings.GetBool("本地", "自动版本", true);
        set => settings.SetBool("本地", "自动版本", value);
    }

    /// <summary>「禁用进程路径校验」：关闭后，结束启动器时不再检查同目录下是否存在 RockstarService.exe。</summary>
    public bool SkipPathCheck
    {
        get => settings.GetBool("扩展", "禁用路径校验", false);
        set => settings.SetBool("扩展", "禁用路径校验", value);
    }

    public string CustomProcessName => settings.Get("本地", "进程名", "GTA5_Enhanced.exe");

    public void LoadFromSettings() =>
        Apply((GameEdition)settings.GetInt("本地", "游戏版本", 2), CustomProcessName, save: false);

    public void Apply(GameEdition edition, string? customProcess = null, bool save = true)
    {
        switch (edition)
        {
            case GameEdition.Legacy:
                ProcessName = "GTA5.exe";
                DocumentsDirName = "GTA V";
                GameDirName = "Grand Theft Auto V";
                break;
            case GameEdition.Custom:
                var name = string.IsNullOrWhiteSpace(customProcess) ? CustomProcessName : customProcess.Trim();
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
                ProcessName = name;
                DocumentsDirName = "GTAV Enhanced"; // 原版：自定义进程默认使用增强版的配置
                GameDirName = "Grand Theft Auto V Enhanced";
                if (save) settings.Set("本地", "进程名", name);
                break;
            default:
                edition = GameEdition.Enhanced;
                ProcessName = "GTA5_Enhanced.exe";
                DocumentsDirName = "GTAV Enhanced";
                GameDirName = "Grand Theft Auto V Enhanced";
                break;
        }
        Edition = edition;
        if (save) settings.SetInt("本地", "游戏版本", (int)edition);
    }

    /// <summary>自动检测正在运行的游戏版本；发生切换时返回 true。</summary>
    public bool DetectRunningEdition()
    {
        if (!AutoDetect || Edition == GameEdition.Custom || IsRunning()) return false;
        var other = Edition == GameEdition.Legacy ? GameEdition.Enhanced : GameEdition.Legacy;
        var otherName = other == GameEdition.Legacy ? "GTA5" : "GTA5_Enhanced";
        if (!AnyProcess(otherName)) return false;
        Apply(other);
        return true;
    }

    // ───────── 查找 ─────────

    private static string Short(string exeName) =>
        exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;

    private static bool AnyProcess(string shortName)
    {
        var list = Process.GetProcessesByName(shortName);
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    public int[] GetProcessIds(string? exeName = null)
    {
        var list = Process.GetProcessesByName(Short(exeName ?? ProcessName));
        var ids = list.Select(p => p.Id).ToArray();
        foreach (var p in list) p.Dispose();
        return ids;
    }

    public bool IsRunning() => GetProcessIds().Length > 0;

    /// <summary>进程_名取句柄：游戏主窗口（可见的顶层窗口）句柄，找不到返回 0。</summary>
    public nint GetWindowHandle()
    {
        var ids = GetProcessIds();
        if (ids.Length == 0) return 0;
        nint found = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!ids.Contains((int)pid) || !IsWindowVisible(hwnd)) return true;
            found = hwnd;
            return false;
        }, 0);
        return found;
    }

    /// <summary>窗口_是否在最顶端：游戏窗口是否是当前前台窗口。</summary>
    public bool IsForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return GetProcessIds().Contains((int)pid);
    }

    /// <summary>窗口_置前台。</summary>
    public void BringToFront()
    {
        var hwnd = GetWindowHandle();
        if (hwnd == 0) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
    }

    /// <summary>进程_取路径：受保护进程用 MainModule 取不到，所以走 QueryFullProcessImageName。</summary>
    public static string GetProcessPath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return "";
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : "";
        }
        finally { CloseHandle(h); }
    }

    public string GetGamePath()
    {
        foreach (var pid in GetProcessIds())
        {
            var path = GetProcessPath(pid);
            if (path.Length > 0) return path;
        }
        return "";
    }

    // ───────── 结束 ─────────

    /// <summary>进程_结束：对所有同名进程 TerminateProcess，全部成功返回 true。</summary>
    public bool Terminate(string? exeName = null)
    {
        var ids = GetProcessIds(exeName);
        if (ids.Length == 0) return false;
        var ok = true;
        foreach (var pid in ids) ok &= TerminatePid(pid);
        return ok;
    }

    public static bool TerminatePid(int pid)
    {
        var h = OpenProcess(PROCESS_TERMINATE, false, pid);
        if (h == 0) return false;
        try { return TerminateProcess(h, 0); }
        finally { CloseHandle(h); }
    }

    /// <summary>进程_结束_Tskl：taskkill /F /IM。</summary>
    public void TerminateWithTaskkill(string? exeName = null) =>
        Shell.Run("taskkill.exe", $"/F /IM \"{exeName ?? ProcessName}\"", wait: true);

    /// <summary>进程_结束_Wmic 的替代：wmic 在新系统已被移除，改用等价的 CIM 调用。</summary>
    public void TerminateWithCim(string? exeName = null) =>
        Shell.PowerShell(
            $"Get-CimInstance Win32_Process -Filter \\\"Name='{exeName ?? ProcessName}'\\\" | Invoke-CimMethod -MethodName Terminate | Out-Null",
            wait: true);

    /// <summary>
    /// 结束启动器进程：结束 Rockstar 的 Launcher.exe。
    /// 默认会校验同目录下存在 RockstarService.exe，避免误杀其他软件的同名进程。
    /// 返回 (找到, 全部结束成功)。
    /// </summary>
    public (bool found, bool allKilled) KillLauncher()
    {
        bool found = false, failed = false;
        foreach (var pid in GetProcessIds("Launcher.exe"))
        {
            if (!SkipPathCheck)
            {
                var path = GetProcessPath(pid);
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "RockstarService.exe"))) continue;
            }
            found = true;
            if (!TerminatePid(pid)) failed = true;
        }
        return (found, found && !failed);
    }

    // ───────── 暂停 ─────────

    /// <summary>进程_暂停：NtSuspendProcess / NtResumeProcess（卡单模式 1）。</summary>
    public bool Suspend(bool suspend)
    {
        var ids = GetProcessIds();
        if (ids.Length == 0) return false;
        var ok = true;
        foreach (var pid in ids)
        {
            var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (h == 0) { ok = false; continue; }
            try { ok &= (suspend ? NtSuspendProcess(h) : NtResumeProcess(h)) == 0; }
            finally { CloseHandle(h); }
        }
        return ok;
    }

    /// <summary>窗口_挂起：只挂起创建游戏窗口的那个线程（卡单模式 2）。返回挂起计数，失败返回 -1。</summary>
    public static int SuspendWindowThread(nint hwnd, bool suspend)
    {
        var tid = GetWindowThreadProcessId(hwnd, out _);
        if (tid == 0) return -1;
        var h = OpenThread(THREAD_SUSPEND_RESUME, false, tid);
        if (h == 0) return -1;
        try { return suspend ? SuspendThread(h) : ResumeThread(h); }
        finally { CloseHandle(h); }
    }
}

/// <summary>对应原版的「运行」命令：隐藏窗口执行外部程序。</summary>
public static class Shell
{
    public static int Run(string file, string args, bool wait)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return -1;
            if (!wait) return 0;
            p.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception) { return -1; }
    }

    public static int PowerShell(string command, bool wait) =>
        Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"&{{{command}}}\"", wait);

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }
}
