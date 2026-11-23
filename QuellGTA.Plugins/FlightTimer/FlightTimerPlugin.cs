using Microsoft.UI.Xaml;
using QuellGTA.Plugins;

namespace QuellGTA.Plugin.FlightTimer;

/// <summary>
/// 飞行变强计时工具（原版 V1.1）：用主程序的简易计时给飞行练习掐表。
/// 原版是一个独立程序，通过 UDP 指令遥控主程序的计时器；这里直接调用同样的指令。
/// 每 10 毫秒看一次按键，按「按下的那一刻」切换状态：
/// 待命 + 起飞键 → 开始计时；计时中 + 停止键 → 结束计时；已结束 + 停止键 → 归零回到待命。
/// </summary>
public sealed class FlightTimerPlugin : IQuellPlugin
{
    private enum Phase { Ready, Timing, Stopped }

    private IPluginHost _host = null!;
    private CancellationTokenSource? _cts;
    private volatile Phase _phase = Phase.Ready;

    public event Action? PhaseChanged;

    private bool Enabled
    {
        get => _host.Settings.GetBool("启用", false);
        set => _host.Settings.SetBool("启用", value);
    }
    /// <summary>原版 V1.1 是 W 开始、R 结束（V1.0 用 E 结束）。</summary>
    private int StartKey
    {
        get => _host.Settings.GetInt("开始键", 0x57);
        set => _host.Settings.SetInt("开始键", value);
    }
    private int StopKey
    {
        get => _host.Settings.GetInt("停止键", 0x52);
        set => _host.Settings.SetInt("停止键", value);
    }
    /// <summary>原版不看前台窗口；移植版默认只在游戏在前台时响应，免得在别处打字时误触。</summary>
    private bool OnlyForeground
    {
        get => _host.Settings.GetBool("仅前台", true);
        set => _host.Settings.SetBool("仅前台", value);
    }

    public void Initialize(IPluginHost host)
    {
        _host = host;
        if (Enabled) Start();
    }

    private void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _phase = Phase.Ready;
        _host.RunCommand("timerzero");
        Task.Run(() => Loop(token));
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task Loop(CancellationToken token)
    {
        bool startWas = false, stopWas = false;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(10, token); }
            catch (OperationCanceledException) { return; }
            var startDown = _host.Input.IsDown(StartKey);
            var stopDown = _host.Input.IsDown(StopKey);
            var startEdge = startDown && !startWas;
            var stopEdge = stopDown && !stopWas;
            (startWas, stopWas) = (startDown, stopDown);
            if (!startEdge && !stopEdge) continue;
            if (OnlyForeground && !_host.Game.IsForeground) continue;

            var next = _phase switch
            {
                Phase.Ready when startEdge => Send("timerstart", Phase.Timing),
                Phase.Timing when stopEdge => Send("timerstop", Phase.Stopped),
                Phase.Stopped when stopEdge => Send("timerzero", Phase.Ready),
                _ => _phase,
            };
            if (next != _phase)
            {
                _phase = next;
                PhaseChanged?.Invoke();
            }
        }
    }

    private Phase Send(string command, Phase next) => _host.RunCommand(command) ? next : _phase;

    private string PhaseText => _phase switch
    {
        Phase.Ready => "待命：按开始键起飞计时",
        Phase.Timing => "计时中：按停止键结束",
        _ => "已结束：再按一次停止键归零",
    };

    public UIElement? CreatePage()
    {
        var ui = _host.Ui;
        var page = ui.CreatePage("飞行变强计时", "飞行学校练习时，按开始键（起飞）开始计时，按停止键结束，再按一次归零。读数显示在主程序的计时器浮窗上，请先在「浮窗」页打开计时器浮窗。");
        var state = ui.Info("", "");
        void Refresh()
        {
            state.Title = _cts is null ? "未启用" : PhaseText;
            state.Severity = _cts is null ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational
                : _phase == Phase.Timing ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
            state.Message = "";
        }
        Action handler = () => _host.Dispatcher.TryEnqueue(Refresh);
        page.Shown += () => { PhaseChanged -= handler; PhaseChanged += handler; Refresh(); };
        page.Hidden += () => PhaseChanged -= handler;

        page.Add(state);
        page.Add(ui.Toggle("启用", "开启时先把计时器归零。", () => Enabled, v =>
        {
            Enabled = v;
            if (v) Start(); else Stop();
            Refresh();
        }, ""));
        page.Section("按键");
        page.Add(ui.Key("开始键", "待命时按下开始计时，默认 W（起飞）。", () => StartKey, v => StartKey = v));
        page.Add(ui.Key("停止键", "计时中按下结束计时，结束后再按一次归零，默认 R。", () => StopKey, v => StopKey = v));
        page.Add(ui.Toggle("仅游戏在前台时响应", "关闭后在任何窗口里按键都会触发（原版的做法）。", () => OnlyForeground, v => OnlyForeground = v));
        page.Section("训练图");
        page.Add(ui.Buttons("Social Club 上的飞行训练差事", "原版作者做的两张训练图。",
            ("多点训练图", () => Open("https://socialclub.rockstargames.com/job/gtav/gSJTfgjuK0qizY7z4rfuJw")),
            ("两点训练图", () => Open("https://socialclub.rockstargames.com/job/gtav/wcaIFoCj_0-rkWytjEZXVg"))));
        return page.Element;
    }

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    public void Dispose() => Stop();
}
