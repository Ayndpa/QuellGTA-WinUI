using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>网络：杀进程、阻止联网、禁用网卡、恢复网络，以及它们各自断什么。</summary>
public sealed partial class NetworkPage : FeaturePage
{
    private static QuellCore Core => App.Core;
    private readonly InfoBar _firewallWarning = Ui.Info("Windows 防火墙已关闭", "防火墙方式的「阻止联网」不会生效。请开启防火墙，或把断网方式改成 WFP。", InfoBarSeverity.Warning);
    private readonly InfoBar _state = Ui.Info("", "");
    private readonly SettingsExpander _nics;
    private readonly SettingsCard _servers;
    private readonly SettingsCard _ipv6;

    public NetworkPage() : base("网络", "游戏卡住、要保进度或要单人战局时，快速断开游戏的网络或直接结束游戏。")
    {
        _firewallWarning.IsOpen = false;
        Add(_state);
        Add(Tiles.Row(
            Tiles.Action("\uE7E8", "杀掉进程", "杀掉键", () => Run(Core.Kill), out _),
            Tiles.Action("\uE83D", "阻止联网", "阻网键", () => Run(Core.BlockNetwork), out _),
            Tiles.Action("\uEB5E", "禁用网卡", "禁卡键", () => Run(Core.DisableNic), out _),
            Tiles.Action("\uE774", "恢复网络", "复网键", () => Run(Core.RestoreNetwork), out _)));

        Section("快捷键");
        Add(Ui.Shortcut(this, "杀掉键"), Ui.Shortcut(this, "阻网键"), Ui.Shortcut(this, "禁卡键"), Ui.Shortcut(this, "复网键"));

        // ───────── 阻止联网 ─────────
        Section("阻止联网", "给游戏加一条出站阻止规则，网卡保持连接，恢复只要一瞬间。");
        Add(_firewallWarning);
        Add(Ui.Choice("断网方式", "WFP 直接向 Windows 筛选平台添加过滤器，防火墙关掉也能用，生效更快；规则在重启系统后自动消失。",
            ["Windows 防火墙", "WFP（Windows 筛选平台）"], () => Core.WfpMode ? 1 : 0, i => { Core.WfpMode = i == 1; CheckFirewall(); },
            glyph: "\uE83D"));
        Add(Ui.Expander("仅断开游戏进程", "只阻止游戏进程联网，其他软件不受影响。关闭后为全局断网。", "\uE7FC",
            Ui.Switch(() => Core.OnlyGameProcess, v => Core.OnlyGameProcess = v),
            Ui.Text("手动指定被断网的程序", "默认自动探测游戏进程。使用加速器时，游戏的流量其实是加速器发出去的，这时要填加速器程序的完整路径。留空恢复自动探测。",
                () => Core.BlockTargetPath, v => Core.BlockTargetPath = v, @"例如 C:\Program Files\加速器\accelerator.exe", 340)));
        _servers = Ui.Row("要阻止的服务器", "", ServerChecks());
        Add(Ui.Choice("阻止范围", "阻联选项用于卡单人战局，阻云选项用于阻止存档上传。",
            ["全部流量", "仅 UDP 协议", "仅 6672 端口（P2P）", "仅 TCP 协议", "仅 80、443 端口", "仅指定的 Rockstar 服务器"],
            () => (int)Core.BlockScope, i => { Core.BlockScope = (BlockScope)i; RefreshScope(); }, 260, "\uE71C"));
        Add(_servers);
        RefreshScope();

        // ───────── 禁用网卡 ─────────
        Section("禁用网卡", "把网卡停掉或解绑协议，所有程序都会断网。阻止联网不起作用时再用它。");
        var refresh = new Button { Content = "刷新" };
        _nics = Ui.Expander("要禁用的网卡", "一个都不勾选时，禁用所有正在使用的网卡。", "\uE839", refresh);
        refresh.Click += (_, _) => LoadNics();
        Add(_nics);
        LoadNics();
        _ipv6 = Ui.Toggle("同时解绑 IPv6", "", () => Core.Nic.IncludeIPv6, v => Core.Nic.IncludeIPv6 = v);
        Add(Ui.Choice("禁用方式", "停用整块网卡最彻底；只解绑 TCP/IP 恢复更快；Dev 硬件模式不经过 PowerShell，比默认方式快一两秒。",
            ["停用整块网卡", "仅解绑 TCP/IP 协议", "Dev 硬件模式"],
            () => Core.Nic.HardwareMode ? 2 : Core.Nic.IpOnly ? 1 : 0,
            i => { Core.Nic.HardwareMode = i == 2; Core.Nic.IpOnly = i == 1; RefreshNicMode(); }, 220, "\uE713"));
        Add(_ipv6);
        RefreshNicMode();

        // ───────── 启动器 ─────────
        Section("Rockstar 启动器");
        Add(Ui.Expander("同时结束启动器", "启动器在后台会继续联网同步存档。勾选的操作执行时，顺带把它结束。", "\uE7E8", null,
            Ui.Row("执行这些操作时", "", LauncherChecks()),
            Ui.Number("延迟（秒）", "操作之后等待多久再结束启动器。", 0, 60, () => Core.LauncherDelayMs / 1000.0,
                v => Core.LauncherDelayMs = (int)Math.Round(v * 1000), 0.1)));

        // ───────── 云存档 ─────────
        Section("云存档提示", "通过游戏设置文件里的「上次线上云存档成功时间」判断游戏是否刚存过档。断网保进度时，如果刚好在存档之后才断，进度其实已经存到云端了。");
        Add(Ui.Expander("云存档提示", "", "\uE753", null,
            Ui.Toggle("保存成功时记录日志", "", () => Core.CloudSaveLog, v => Core.CloudSaveLog = v),
            Ui.Toggle("保存成功时播放声音", "需要同时开启上一项。", () => Core.CloudSaveSound, v => Core.CloudSaveSound = v),
            Ui.Toggle("操作时提示保存成功间隔", "杀进程、断网时，如果 10 秒内刚存过档，在日志里提示距离上次存档过了几秒。",
                () => Core.CloudSaveInterval, v => Core.CloudSaveInterval = v),
            Ui.Toggle("罪神时评估进度保留情况", "联机状态下杀进程时，如果 10 秒内刚存过档，弹出警告：罪神进度可能已经被云端重置。",
                () => Core.CloudSaveHeistWarning, v => Core.CloudSaveHeistWarning = v)));
        Add(Ui.Buttons("查询上次云存档", "", "\uE81C", ("查询", async () =>
        {
            var last = Core.LastCloudSave;
            await Dialogs.MessageAsync(this, "上次云存档", last is null
                ? "本次运行期间还没有观察到云存档。需要先开启上面任意一项，并且游戏在线上模式里存过一次档。"
                : $"{last.Value.ToLocalTime():HH:mm:ss}（{(int)(DateTimeOffset.UtcNow - last.Value).TotalSeconds} 秒前）");
        })));

        // ───────── 故障处理 ─────────
        Section("故障处理");
        Add(Ui.Buttons("修复网络", "断网后网络没有恢复时使用：删除本程序创建的所有阻止规则，启用所有网卡并重新绑定 IPv4 / IPv6 协议。你自己手动禁用的网卡也会被启用。",
            "\uE90F", ("修复网络", RepairNetwork)));

        OnCoreState(RefreshState);
        Loaded += (_, _) => CheckFirewall();
    }

    private void RefreshState()
    {
        var cut = Core.NetBlocked || Core.NicDisabled;
        _state.Severity = cut ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        _state.Title = Core.NetBlocked && Core.NicDisabled ? "已阻止联网，并禁用了网卡"
            : Core.NetBlocked ? "已阻止联网" : Core.NicDisabled ? "已禁用网卡" : "网络正常";
        _state.Message = cut ? "点「恢复网络」或按对应的快捷键解除。" : "没有生效中的阻止规则。";
    }

    private void CheckFirewall()
    {
        if (Core.WfpMode) { _firewallWarning.IsOpen = false; return; }
        Task.Run(() =>
        {
            var enabled = Core.Firewall.IsFirewallEnabled();
            DispatcherQueue.TryEnqueue(() => _firewallWarning.IsOpen = !enabled && !Core.WfpMode);
        });
    }

    private StackPanel ServerChecks()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var (text, get, set) in new (string, Func<bool>, Action<bool>)[]
                 {
                     ("云存档", () => Core.BlockCloudSave, v => Core.BlockCloudSave = v),
                     ("交易", () => Core.BlockTransaction, v => Core.BlockTransaction = v),
                     ("差事", () => Core.BlockJobs, v => Core.BlockJobs = v),
                 })
        {
            var box = new CheckBox { Content = text, IsChecked = get(), MinWidth = 0 };
            box.Click += (_, _) => set(box.IsChecked == true);
            panel.Children.Add(box);
        }
        return panel;
    }

    private void RefreshScope() =>
        _servers.Visibility = Core.BlockScope == BlockScope.Servers ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshNicMode() =>
        _ipv6.Visibility = Core.Nic.IpOnly && !Core.Nic.HardwareMode ? Visibility.Visible : Visibility.Collapsed;

    private StackPanel LauncherChecks()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var (text, get, set) in new (string, Func<bool>, Action<bool>)[]
                 {
                     ("杀掉进程", () => Core.KillLauncherOnKill, v => Core.KillLauncherOnKill = v),
                     ("阻止联网", () => Core.KillLauncherOnBlock, v => Core.KillLauncherOnBlock = v),
                     ("禁用网卡", () => Core.KillLauncherOnNic, v => Core.KillLauncherOnNic = v),
                 })
        {
            var box = new CheckBox { Content = text, IsChecked = get(), MinWidth = 0 };
            box.Click += (_, _) => set(box.IsChecked == true);
            panel.Children.Add(box);
        }
        return panel;
    }

    private void LoadNics()
    {
        _nics.Items.Clear();
        var selected = Core.Nic.SelectedNames;
        var nics = NicService.List();
        foreach (var nic in nics)
        {
            var box = new CheckBox { IsChecked = selected.Contains(nic.Name), MinWidth = 0 };
            box.Click += (_, _) =>
            {
                var names = Core.Nic.SelectedNames.ToList();
                names.Remove(nic.Name);
                if (box.IsChecked == true) names.Add(nic.Name);
                Core.Nic.SelectedNames = names.ToArray();
                UpdateNicSummary();
            };
            _nics.Items.Add(Ui.Row(nic.Name, nic.Description + (nic.IsUp ? "" : "（未连接）"), box));
        }
        if (nics.Count == 0) _nics.Items.Add(Ui.Row("没有找到网卡", "可能已被禁用，请先恢复网络。", null));
        UpdateNicSummary();
    }

    private void UpdateNicSummary()
    {
        var selected = Core.Nic.SelectedNames;
        _nics.Description = selected.Length == 0 ? "未指定：禁用所有正在使用的网卡。" : "已选：" + string.Join("、", selected);
    }

    private async void RepairNetwork()
    {
        if (!await Dialogs.ConfirmAsync(this, "修复网络", Lang.C(262) + Environment.NewLine + Lang.C(265))) return;
        await Task.Run(() =>
        {
            try { Core.Firewall.Remove(FirewallService.BlockRule); } catch (Exception) { }
            try { Core.Firewall.Remove(FirewallService.HoverRule); } catch (Exception) { }
            try { Core.Wfp.Clear(); } catch (Exception) { }
            Core.Nic.RepairAll();
            Core.ResetNetworkState();
        });
        Core.Log.Write("已尝试修复网络：阻止规则已删除，网卡和协议已全部启用。", 2);
        LoadNics();
    }
}
