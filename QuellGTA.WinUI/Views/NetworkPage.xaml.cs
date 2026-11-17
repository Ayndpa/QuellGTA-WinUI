using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class NetworkPage : Page
{
    private static QuellCore Core => App.Core;
    private readonly bool _ready;

    public NetworkPage()
    {
        InitializeComponent();
        BuildExtras();

        OnlyGameSwitch.IsOn = Core.OnlyGameProcess;
        ScopeRadios.SelectedIndex = (int)Core.BlockScope;
        CloudCheck.IsChecked = Core.BlockCloudSave;
        TransactionCheck.IsChecked = Core.BlockTransaction;
        JobsCheck.IsChecked = Core.BlockJobs;
        ServerPanel.Visibility = Core.BlockScope == BlockScope.Servers ? Visibility.Visible : Visibility.Collapsed;

        IpOnlySwitch.IsOn = Core.Nic.IpOnly;
        IPv6Switch.IsOn = Core.Nic.IncludeIPv6;

        LauncherKillCheck.IsChecked = Core.KillLauncherOnKill;
        LauncherBlockCheck.IsChecked = Core.KillLauncherOnBlock;
        LauncherNicCheck.IsChecked = Core.KillLauncherOnNic;
        LauncherDelayBox.Value = Core.LauncherDelayMs / 1000.0;

        LoadNics();
        _ready = true;

        // COM 调用可能较慢，放到后台查询防火墙状态。
        Task.Run(() =>
        {
            var enabled = Core.Firewall.IsFirewallEnabled();
            DispatcherQueue.TryEnqueue(() => FirewallWarning.IsOpen = !enabled);
        });
    }

    private void LoadNics()
    {
        NicPanel.Children.Clear();
        var selected = Core.Nic.SelectedNames;
        var nics = NicService.List();
        foreach (var nic in nics)
        {
            var box = new CheckBox
            {
                Content = $"{nic.Name}    {nic.Description}{(nic.IsUp ? "" : "（未连接）")}",
                Tag = nic.Name,
                IsChecked = selected.Contains(nic.Name),
            };
            box.Click += Nic_Click;
            NicPanel.Children.Add(box);
        }
        if (nics.Count == 0)
            NicPanel.Children.Add(new TextBlock { Text = "没有找到网卡（可能已被禁用，请先恢复网络）。", Opacity = 0.7 });
    }

    private void Nic_Click(object sender, RoutedEventArgs e) =>
        Core.Nic.SelectedNames = NicPanel.Children.OfType<CheckBox>()
            .Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToArray();

    private void RefreshNics_Click(object sender, RoutedEventArgs e) => LoadNics();

    private void OnlyGame_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.OnlyGameProcess = OnlyGameSwitch.IsOn;
    }

    private void Scope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ScopeRadios.SelectedIndex < 0) return;
        Core.BlockScope = (BlockScope)ScopeRadios.SelectedIndex;
        ServerPanel.Visibility = Core.BlockScope == BlockScope.Servers ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Server_Click(object sender, RoutedEventArgs e)
    {
        Core.BlockCloudSave = CloudCheck.IsChecked == true;
        Core.BlockTransaction = TransactionCheck.IsChecked == true;
        Core.BlockJobs = JobsCheck.IsChecked == true;
    }

    private void IpOnly_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.Nic.IpOnly = IpOnlySwitch.IsOn;
    }

    private void IPv6_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.Nic.IncludeIPv6 = IPv6Switch.IsOn;
    }

    private void Launcher_Click(object sender, RoutedEventArgs e)
    {
        Core.KillLauncherOnKill = LauncherKillCheck.IsChecked == true;
        Core.KillLauncherOnBlock = LauncherBlockCheck.IsChecked == true;
        Core.KillLauncherOnNic = LauncherNicCheck.IsChecked == true;
    }

    private async void RepairNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(this, "尝试修复网络", Lang.C(262) + Environment.NewLine + Lang.C(265))) return;
        await Task.Run(() =>
        {
            try { Core.Firewall.Remove(FirewallService.BlockRule); } catch (Exception) { }
            try { Core.Firewall.Remove(FirewallService.HoverRule); } catch (Exception) { }
            try { Core.Wfp.Clear(); } catch (Exception) { }
            Core.Nic.RepairAll();
            Core.ResetNetworkState();
        });
        Core.Log.Write("已尝试修复网络：防火墙规则已删除，网卡和协议已全部启用。", 2);
        LoadNics();
    }

    private void LauncherDelay_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !double.IsNaN(sender.Value)) Core.LauncherDelayMs = (int)Math.Round(sender.Value * 1000);
    }
}
