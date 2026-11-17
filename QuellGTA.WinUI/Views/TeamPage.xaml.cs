using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class TeamPage : Page
{
    private static TeamService Team => App.Core.Team;
    private readonly bool _ready;
    private readonly Action _changedHandler;
    private List<PublicServer>? _servers;
    private bool _loadingServers;

    public TeamPage()
    {
        InitializeComponent();

        NicknameBox.Text = Team.Nickname;
        TeamBox.Text = Team.Team;
        AddressBox.Text = Team.Address;
        PortBox.Value = Team.Port;
        KillCheck.IsChecked = Team.BroadcastKill;
        BlockCheck.IsChecked = Team.BroadcastBlock;
        NicCheck.IsChecked = Team.BroadcastNic;
        SoloCheck.IsChecked = Team.BroadcastSolo;
        PublicMenu.Items.Add(new MenuFlyoutItem { Text = "正在获取…", IsEnabled = false });
        _ready = true;

        _changedHandler = () => DispatcherQueue.TryEnqueue(Refresh);
        Loaded += (_, _) => { Team.Changed += _changedHandler; Refresh(); };
        Unloaded += (_, _) => Team.Changed -= _changedHandler;
    }

    private void Refresh()
    {
        var mode = Team.Mode;
        StateBar.Severity = mode switch
        {
            TeamMode.Client or TeamMode.Host => InfoBarSeverity.Success,
            TeamMode.Connecting => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        StateBar.Title = mode switch
        {
            TeamMode.Client => "已加入房间",
            TeamMode.Host => "正在当房主",
            TeamMode.Connecting => "正在连接",
            _ => "未联机",
        };
        StateBar.Message = mode == TeamMode.Offline ? "加入一个房间，或者自己建房。" : Team.Status;
        LeaveButton.Visibility = mode == TeamMode.Offline ? Visibility.Collapsed : Visibility.Visible;
        LeaveButton.Content = mode == TeamMode.Host ? "停止服务器" : mode == TeamMode.Connecting ? "取消" : "离开";

        var offline = mode == TeamMode.Offline;
        JoinButton.IsEnabled = offline;
        HostButton.IsEnabled = offline;
        PublicButton.IsEnabled = offline;
        PortBox.IsEnabled = offline;
        AddressBox.IsEnabled = offline;
        SendButton.IsEnabled = mode is TeamMode.Client or TeamMode.Host;

        MemberCard.Visibility = mode == TeamMode.Host ? Visibility.Visible : Visibility.Collapsed;
        if (mode == TeamMode.Host) MemberList.ItemsSource = Team.GetMembers();
        if (TeamBox.FocusState == FocusState.Unfocused) TeamBox.Text = Team.Team;
        if (NicknameBox.FocusState == FocusState.Unfocused) NicknameBox.Text = Team.Nickname;
    }

    // ───────── 身份 ─────────

    private void Nickname_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Team.Nickname = NicknameBox.Text.Replace("<", "").Replace(">", "");
        NicknameBox.Text = Team.Nickname;
    }

    private void GenerateNickname_Click(object sender, RoutedEventArgs e) => NicknameBox.Text = Team.GenerateNickname();

    private void ApplyTeam_Click(object sender, RoutedEventArgs e)
    {
        Team.SetTeam(TeamBox.Text);
        TeamBox.Text = Team.Team;
        App.Core.Log.Write($"小队口令已设为 {Team.Team}。", 1);
    }

    private void PublicTeam_Click(object sender, RoutedEventArgs e)
    {
        Team.SetTeam("Public");
        TeamBox.Text = Team.Team;
    }

    // ───────── 加入 / 建房 ─────────

    private async Task JoinAsync(string address)
    {
        if (Team.Team == "Public" && !await Dialogs.ConfirmAsync(this, "还在公开小队",
                Lang.C(181) + "。\n\n可以先取消，设置好小队口令再加入。", "仍然加入"))
            return;
        var error = Team.Join(address);
        if (error is not null) await Dialogs.MessageAsync(this, "无法加入", error);
    }

    private async void Join_Click(object sender, RoutedEventArgs e) => await JoinAsync(AddressBox.Text);

    private void Host_Click(object sender, RoutedEventArgs e) => Task.Run(() => Team.StartHost());

    private void Leave_Click(object sender, RoutedEventArgs e) => Task.Run(() => Team.Leave());

    private void Port_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !double.IsNaN(sender.Value)) Team.Port = (int)sender.Value;
    }

    private async void PublicMenu_Opening(object? sender, object e)
    {
        if (_servers is not null || _loadingServers) return;
        _loadingServers = true;
        var (servers, _) = await App.Core.Bots.GetPublicServersAsync();
        _loadingServers = false;
        _servers = servers;
        PublicMenu.Items.Clear();
        foreach (var server in servers)
        {
            var item = new MenuFlyoutItem { Text = $"{server.Name}（{server.Address}）", Tag = server.Address };
            item.Click += async (s, _) =>
            {
                AddressBox.Text = (string)((MenuFlyoutItem)s).Tag;
                await JoinAsync(AddressBox.Text);
            };
            PublicMenu.Items.Add(item);
        }
        PublicMenu.Items.Add(new MenuFlyoutSeparator());
        var refresh = new MenuFlyoutItem { Text = "重新获取列表" };
        refresh.Click += (_, _) =>
        {
            _servers = null;
            PublicMenu.Items.Clear();
            PublicMenu.Items.Add(new MenuFlyoutItem { Text = "正在获取…（再点一次展开）", IsEnabled = false });
        };
        PublicMenu.Items.Add(refresh);
    }

    // ───────── 同步内容 / 消息 ─────────

    private void Broadcast_Click(object sender, RoutedEventArgs e)
    {
        Team.BroadcastKill = KillCheck.IsChecked == true;
        Team.BroadcastBlock = BlockCheck.IsChecked == true;
        Team.BroadcastNic = NicCheck.IsChecked == true;
        Team.BroadcastSolo = SoloCheck.IsChecked == true;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var error = Team.SendMessage(MessageBox.Text);
        if (error is null) MessageBox.Text = "";
        else await Dialogs.MessageAsync(this, "没有发出去", error);
    }

    private void Message_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && SendButton.IsEnabled) Send_Click(sender, e);
    }
}
