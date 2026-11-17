using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace QuellGTA.Services;

/// <summary>
/// 进程守护，对应原版内嵌的 UEPcon.exe（源码见仓库 扩展插件/Others/QuellGTA_UEPcon.cpp）。
/// 原版是单独的一个 C++ 小程序；移植版让本程序自己带 guard 参数再启动一份来当守护进程，逻辑相同：
/// 每秒看一眼主程序还在不在，不在了就重新启动；主程序通过命名管道定时发心跳，12 秒没收到就认为界面卡死，结束后重启。
/// </summary>
public sealed class GuardService(Settings settings, LogService log) : IDisposable
{
    /// <summary>管道名特意和原版（QuellGTA_UEP）不同，两个版本同时装着也互不干扰。</summary>
    public const string PipeName = "QuellGTA_WinUI_UEP";
    /// <summary>守护进程重启主程序时带的参数，主程序据此知道自己上次是意外退出的。</summary>
    public const string RecoveredArgument = "recovered";
    private const int HeartbeatTimeoutMs = 12000;

    private Process? _guard;
    private DispatcherQueueTimer? _timer;
    private int _failures, _reinstalls, _sending;

    // ───────── 主程序这一侧 ─────────

    /// <summary>进程结束保护。原版默认开启；移植版默认关闭，需要的话到「设置」页打开。</summary>
    public bool Enabled
    {
        get => settings.GetBool("本地", "守护进程", false);
        set
        {
            settings.SetBool("本地", "守护进程", value);
            Apply();
        }
    }
    /// <summary>进程无响应保护：界面线程超过 12 秒没有动静时，结束并重启本程序。</summary>
    public bool WatchHang
    {
        get => settings.GetBool("本地", "守护无响应", true);
        set => settings.SetBool("本地", "守护无响应", value);
    }
    /// <summary>被守护进程重启之后，用醒目的方式提示（警告声 + 弹出提示）。</summary>
    public bool ShowWarning
    {
        get => settings.GetBool("本地", "守护显警告", false);
        set => settings.SetBool("本地", "守护显警告", value);
    }
    /// <summary>被守护进程重启之后，自动恢复联机状态。</summary>
    public bool AutoRecover
    {
        get => settings.GetBool("本地", "守护自恢复", true);
        set => settings.SetBool("本地", "守护自恢复", value);
    }

    public bool IsRunning => _guard is { HasExited: false };

    /// <summary>
    /// 心跳必须由界面线程的定时器发出：界面卡死时定时器不再触发，心跳自然就停了。
    /// </summary>
    public void AttachUi(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => Heartbeat();
        Apply();
    }

    /// <summary>按当前设置启动或停止守护进程（原版「启动守护程序」）。</summary>
    public void Apply()
    {
        Stop();
        if (!Enabled || _timer is null || Debugger.IsAttached) return; // 调试时断点会让心跳停住，不启动守护
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            _guard = Process.Start(new ProcessStartInfo(exe, $"guard {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
            _failures = 0;
            _timer.Start();
        }
        catch (Exception ex)
        {
            log.Write("进程守护：启动失败：" + ex.Message, 2);
        }
    }

    /// <summary>正常退出、关闭功能时调用：让守护进程自己退出，免得它把我们又拉起来。</summary>
    public void Stop()
    {
        _timer?.Stop();
        Send("STOP", 300);
        var guard = Interlocked.Exchange(ref _guard, null);
        if (guard is null) return;
        try
        {
            if (!guard.WaitForExit(500)) guard.Kill();
        }
        catch (Exception) { }
        guard.Dispose();
    }

    private void Heartbeat()
    {
        if (!Enabled) return;
        if (Interlocked.Exchange(ref _sending, 1) == 1) return;
        var watch = WatchHang;
        // 管道读写放到后台，界面线程只负责「按时触发」这件事
        Task.Run(() =>
        {
            try
            {
                if (!watch)
                {
                    // 原版：没开无响应保护时，只检查守护进程还在不在
                    if (!IsRunning) Apply();
                    return;
                }
                if (Send("PING", 1000)) { _failures = 0; return; }
                if (++_failures < 3) return;
                _failures = 0;
                if (++_reinstalls > 3)
                {
                    _reinstalls = 0;
                    WatchHang = false;
                    log.Write(Lang.C(542), 2);
                    return;
                }
                log.Write(Lang.C(540));
                Apply();
            }
            finally { Volatile.Write(ref _sending, 0); }
        });
    }

    private static bool Send(string message, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(timeoutMs);
            pipe.Write(Encoding.ASCII.GetBytes(message));
            pipe.Flush();
            return true;
        }
        catch (Exception) { return false; }
    }

    public void Dispose() => Stop();

    // ───────── 守护进程这一侧（QuellGTA.exe guard <主程序进程号>） ─────────

    private static long _lastHeartbeat;
    private static volatile bool _heartbeatEnabled, _stop;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    /// <summary>守护进程的入口。不加载任何界面组件。</summary>
    public static int RunGuard(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var pid)) return 0;
        var exe = Environment.ProcessPath;
        if (exe is null) return 0;

        // 已经有一个守护进程了就不再重复
        try
        {
            using var probe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            probe.Connect(100);
            return 0;
        }
        catch (Exception) { }

        Process? target;
        try { target = Process.GetProcessById(pid); }
        catch (Exception) { return 0; }

        _lastHeartbeat = Environment.TickCount64;
        new Thread(PipeServer) { IsBackground = true, Name = "QuellGTA.Guard.Pipe" }.Start();

        var lastLoop = Environment.TickCount64;
        var quickExits = 0;
        while (!_stop)
        {
            var now = Environment.TickCount64;
            // 电脑睡眠 / 休眠醒来后，时间一下子跳了一大截，不能算成主程序卡死
            if (now - lastLoop > 5000) Volatile.Write(ref _lastHeartbeat, now);
            lastLoop = now;

            var exited = HasExited(target);
            var hung = !exited && _heartbeatEnabled && now - Volatile.Read(ref _lastHeartbeat) >= HeartbeatTimeoutMs;
            if (exited || hung)
            {
                if (_stop) break; // 主程序是正常退出的
                if (hung)
                {
                    try { target!.Kill(); target.WaitForExit(5000); } catch (Exception) { }
                }
                if (!File.Exists(exe))
                {
                    MessageBoxW(0, "QuellGTA主程序已经丢失！可能被杀毒软件删除，请调整杀毒软件设置后重新下载运行！\n" +
                                   "QuellGTA main program missing! It might have been deleted by antivirus software. Please adjust your antivirus settings and download it again!",
                        "QuellGTA", 0x10);
                    return 0;
                }
                // 给「正常退出」留一点时间：STOP 可能比进程退出晚到一步
                Thread.Sleep(300);
                if (_stop) break;
                try
                {
                    target?.Dispose();
                    target = Process.Start(new ProcessStartInfo(exe, RecoveredArgument) { UseShellExecute = false });
                    Volatile.Write(ref _lastHeartbeat, Environment.TickCount64);
                    _heartbeatEnabled = false;
                    Thread.Sleep(5000);
                    lastLoop = Environment.TickCount64;
                    // 原版没有这一条：重启后 5 秒内又没了，连续三次就放弃，免得一直反复拉起一个起不来的程序
                    quickExits = HasExited(target) ? quickExits + 1 : 0;
                    if (quickExits >= 3) return 0;
                    continue;
                }
                catch (Exception) { }
            }
            Thread.Sleep(1000);
        }
        return 0;
    }

    private static bool HasExited(Process? process)
    {
        try { return process is null || process.HasExited; }
        catch (Exception) { return true; }
    }

    private static void PipeServer()
    {
        var buffer = new byte[64];
        while (!_stop)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                server.WaitForConnection();
                var count = server.Read(buffer, 0, buffer.Length);
                if (count <= 0) continue;
                var message = Encoding.ASCII.GetString(buffer, 0, count);
                if (message.StartsWith("STOP", StringComparison.Ordinal))
                {
                    _stop = true;
                    Environment.Exit(0);
                }
                Volatile.Write(ref _lastHeartbeat, Environment.TickCount64);
                _heartbeatEnabled = true;
            }
            catch (Exception) { Thread.Sleep(100); }
        }
    }
}
