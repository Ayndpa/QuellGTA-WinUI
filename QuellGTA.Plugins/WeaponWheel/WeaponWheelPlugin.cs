using Microsoft.UI.Xaml;
using QuellGTA.Plugins;

namespace QuellGTA.Plugin.WeaponWheel;

/// <summary>
/// 武器选单优化器（原版 V1.1）。线上模式现在要把武器轮盘键按住一小段时间才会打开轮盘，轻点一下只会切换武器。
/// 用法：在游戏里把武器轮盘改绑到一个不常用的键（例如 L），这里的「模拟键」填它；「触发键」填你习惯按的键。
/// 按下触发键时立刻按下模拟键；松开触发键时，如果模拟键按住的时间还不到安全时长，就等够了再松开。
/// 原版用 50 毫秒的时钟查询触发键（_时钟1_周期事件 → 按键被按下），这里每 10 毫秒查一次。
/// </summary>
public sealed class WeaponWheelPlugin : IQuellPlugin
{
    private IPluginHost _host = null!;
    private CancellationTokenSource? _cts;

    private bool Enabled
    {
        get => _host.Settings.GetBool("启用", false);
        set => _host.Settings.SetBool("启用", value);
    }
    /// <summary>原版默认 88（X）。</summary>
    private int TriggerKey
    {
        get => _host.Settings.GetInt("触发键", 0x58);
        set => _host.Settings.SetInt("触发键", value);
    }
    /// <summary>原版默认 9（Tab，游戏的默认武器轮盘键）。</summary>
    private int SimulatedKey
    {
        get => _host.Settings.GetInt("模拟键", 0x09);
        set => _host.Settings.SetInt("模拟键", value);
    }
    /// <summary>原版「安全切换」延迟，默认 250 毫秒。</summary>
    private int HoldMs
    {
        get => Math.Clamp(_host.Settings.GetInt("安全切换", 250), 0, 2000);
        set => _host.Settings.SetInt("安全切换", Math.Clamp(value, 0, 2000));
    }
    /// <summary>原版不看前台窗口；移植版默认只在游戏在前台时动作，免得在别的窗口里按出 Tab。</summary>
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
        Task.Run(() => Loop(token));
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task Loop(CancellationToken token)
    {
        var was = false;
        var holding = false;
        var heldKey = 0;
        long pressedAt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(10, token);
                var down = _host.Input.IsDown(TriggerKey);
                if (down == was) continue;
                was = down;
                if (down)
                {
                    // 按键被按下(真)：按下模拟键并记下时刻
                    if (OnlyForeground && !_host.Game.IsForeground) continue;
                    heldKey = SimulatedKey;
                    _host.Input.KeyDown(heldKey);
                    pressedAt = Environment.TickCount64;
                    holding = true;
                }
                else if (holding)
                {
                    // 按键被按下(假)：不够安全时长就等够，再松开模拟键
                    var remaining = HoldMs - (Environment.TickCount64 - pressedAt);
                    if (remaining > 0) await Task.Delay((int)remaining, CancellationToken.None);
                    _host.Input.KeyUp(heldKey);
                    holding = false;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (holding) _host.Input.KeyUp(heldKey); // 停用时别把键留在按下状态
        }
    }

    public UIElement? CreatePage()
    {
        var ui = _host.Ui;
        var page = ui.CreatePage("武器选单优化器",
            "游戏要把武器轮盘键按住约 250 毫秒才会打开轮盘。先在游戏设置里把「武器轮盘」改绑到一个不常用的键，下面的「模拟键」填它；以后按「触发键」，程序会替你把模拟键按足时长。");
        page.Add(ui.Toggle("启用", "", () => Enabled, v =>
        {
            Enabled = v;
            if (v) Start(); else Stop();
        }, ""));
        page.Section("按键");
        page.Add(ui.Key("触发键", "你实际按的键，原版默认 X。按住多久，模拟键就至少按住多久。", () => TriggerKey, v => TriggerKey = v));
        page.Add(ui.Key("模拟键", "游戏里武器轮盘绑定的键，原版默认 Tab。", () => SimulatedKey, v => SimulatedKey = v));
        page.Add(ui.Number("安全时长（毫秒）", "模拟键最少按住多久，默认 250。轮盘偶尔打不开时调大一些。", 0, 2000, () => HoldMs, v => HoldMs = (int)v, 10));
        page.Add(ui.Toggle("仅游戏在前台时生效", "关闭后在任何窗口里按触发键都会按下模拟键（原版的做法）。", () => OnlyForeground, v => OnlyForeground = v));
        page.Add(ui.Buttons("恢复默认", "触发键 X、模拟键 Tab、安全时长 250 毫秒。", ("恢复", () =>
        {
            TriggerKey = 0x58;
            SimulatedKey = 0x09;
            HoldMs = 250;
            _host.Log("已恢复默认设置，重新打开本页即可看到。");
        })));
        return page.Element;
    }

    public void Dispose() => Stop();
}
