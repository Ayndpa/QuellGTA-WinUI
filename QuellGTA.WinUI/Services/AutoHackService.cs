using QuellGTA.Services.AutoHack;

namespace QuellGTA.Services;

/// <summary>
/// 自动骇入的调度（原版内嵌的 AutoHack.exe，对应其 main.cpp 的 WorkerMain）：
/// 找到游戏窗口 → 轮流问各个小游戏模块「当前画面是不是你」→ 交给认出来的那个模块操作到完成。
/// 一次只处理一个小游戏，完成或失败后自动停下；运行期间再按一次热键可以随时中止。
/// </summary>
public sealed class AutoHackService : IDisposable
{
    private readonly Settings _settings;
    private readonly LogService _log;
    private readonly HackHost _host;
    private readonly IHackGame[] _games;
    private readonly MatchHack _match;
    private readonly object _gate = new();
    private Thread? _worker;
    private volatile bool _stop;

    /// <summary>等小游戏画面出现的最长时间。原版会一直等到再按一次热键；这里加了上限，免得忘了关。</summary>
    private const int IdleTimeoutMs = 60000;

    public AutoHackService(Settings settings, GameService game, LogService log)
    {
        _settings = settings;
        _log = log;
        _host = new HackHost(game);
        // 顺序与原版 DetectGame 一致（尚未移植的模块暂缺）
        var match = new MatchHack(_host);
        match.Solved += equation => _log.Write("自动骇入：" + equation, 1);
        _match = match;
        _games = [new FindNumberHack(_host), match];
    }

    public IEnumerable<string> SupportedGames => _games.Select(g => g.Name);

    public bool Enabled
    {
        get => _settings.Get("AutoHack", "enable", "1") == "1";
        set => _settings.Set("AutoHack", "enable", value ? "1" : "0");
    }
    /// <summary>每次点按按住多久（毫秒），原版默认 20，范围 1–250。帧率低、按键丢失时调大。</summary>
    public int TapHoldMs
    {
        get => Math.Clamp(_settings.GetInt("AutoHack", "tap_hold_ms", 20), 1, 250);
        set => _settings.SetInt("AutoHack", "tap_hold_ms", Math.Clamp(value, 1, 250));
    }
    /// <summary>两次点按之间的间隔（毫秒），原版默认 20。</summary>
    public int TapGapMs
    {
        get => Math.Clamp(_settings.GetInt("AutoHack", "tap_gap_ms", 20), 1, 250);
        set => _settings.SetInt("AutoHack", "tap_gap_ms", Math.Clamp(value, 1, 250));
    }

    public bool Running { get { lock (_gate) return _worker is { IsAlive: true }; } }
    /// <summary>当前状态的原文（各模块报告的英文短语），空串表示没在运行。</summary>
    public string Status { get; private set; } = "";
    public event Action? Changed;

    /// <summary>热键：没在运行就开始，正在运行就中止。</summary>
    public void Toggle()
    {
        if (Running) Stop();
        else Start();
    }

    public void Start()
    {
        if (!Enabled) { _log.Write("自动骇入没有启用，请先在「宏键」页开启。"); return; }
        lock (_gate)
        {
            if (_worker is { IsAlive: true }) return;
            _stop = false;
            _host.TapHoldMs = TapHoldMs;
            _host.TapGapMs = TapGapMs;
            _worker = new Thread(WorkerMain) { IsBackground = true, Name = "QuellGTA.AutoHack", Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        _stop = true;
        _host.CancelAll();
    }

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private void WorkerMain()
    {
        var completed = false;
        var windowFound = false;
        IHackGame? game = null;
        try
        {
            SetStatus("searching for game window");
            var started = Environment.TickCount64;
            while (!_stop && Environment.TickCount64 - started < IdleTimeoutMs)
            {
                if (!windowFound && !_host.FindGameWindow())
                {
                    Thread.Sleep(100);
                    continue;
                }
                if (!windowFound)
                {
                    windowFound = true;
                    SetStatus("searching for minigame");
                    _log.Write("自动骇入：已找到游戏窗口，正在等待骇入画面…（再按一次热键取消）", 1);
                }
                foreach (var candidate in _games) candidate.ResetInGameCache();
                game = _games.FirstOrDefault(g => g.DetectInGame());
                if (game is null)
                {
                    Thread.Sleep(30);
                    continue;
                }
                _log.Write("自动骇入：识别到「" + game.Name + "」，开始操作。", 1);
                completed = game.RunSession(() => _stop, SetStatus);
                break;
            }
        }
        catch (Exception ex)
        {
            _log.Write("自动骇入出错：" + ex.Message, 3);
        }
        finally
        {
            _host.CancelAll();
            foreach (var candidate in _games) candidate.ResetInGameCache();
            _host.ClearGameWindow();
            if (completed) _log.Write("自动骇入：「" + game!.Name + "」已完成。", 1);
            else if (_stop) _log.Write("自动骇入：已停止。");
            else if (game is not null)
                _log.Write("自动骇入：「" + game.Name + "」没有完成（" + Status + "）。"
                           + (game == _match && Status == "match: no solution" ? "读到的是：" + _match.LastReading : ""), 2);
            else _log.Write(windowFound ? "自动骇入：没有识别到支持的骇入画面。" : "自动骇入：没有找到游戏窗口。", 2);
            SetStatus("");
        }
    }

    public void Dispose()
    {
        Stop();
        Thread? worker;
        lock (_gate) worker = _worker;
        worker?.Join(3000);
        _host.Dispose();
    }
}
