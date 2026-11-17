using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QuellGTA.Services;
using Windows.ApplicationModel.DataTransfer;

namespace QuellGTA.Views;

public sealed partial class BotPage : Page
{
    private static BotService Bots => App.Core.Bots;

    private static WaitService Waiting => App.Core.Waiting;
    private readonly Action _waitHandler;

    public BotPage()
    {
        InitializeComponent();
        WaitAutoJoin.IsChecked = Waiting.AutoJoin;
        WaitAutoSolo.IsChecked = Waiting.AutoSolo;
        _waitHandler = () => DispatcherQueue.TryEnqueue(RefreshWaiting);
        Loaded += (_, _) => { Waiting.Changed += _waitHandler; RefreshWaiting(); };
        Unloaded += (_, _) => Waiting.Changed -= _waitHandler;
    }

    // ───────── 差事候车 ─────────

    private void RefreshWaiting()
    {
        var running = Waiting.Running;
        WaitStartButton.IsEnabled = !running;
        WaitStopButton.IsEnabled = running;
        WaitJoinButton.IsEnabled = running;
        WaitUrlBox.IsEnabled = !running;
        if (!running)
        {
            var typed = WaitUrlBox.Text;
            WaitUrlBox.Items.Clear();
            foreach (var url in Waiting.History) WaitUrlBox.Items.Add(url);
            WaitUrlBox.Text = !string.IsNullOrEmpty(typed) ? typed : Waiting.History.FirstOrDefault() ?? "";
        }
        var transcript = Waiting.Transcript;
        WaitLog.Text = transcript.Length > 0 ? transcript : Lang.C(559);
        WaitLog.Select(WaitLog.Text.Length, 0);
    }

    private async void WaitStart_Click(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(this, "开始候车", Lang.C(576))) return;
        var error = Waiting.Start(WaitUrlBox.Text ?? "");
        if (error is not null) await Dialogs.MessageAsync(this, "无法开始候车", error);
        RefreshWaiting();
    }

    private void WaitStop_Click(object sender, RoutedEventArgs e) => Waiting.Stop();

    private async void WaitJoin_Click(object sender, RoutedEventArgs e)
    {
        if (!Waiting.CanJoinNow && !await Dialogs.ConfirmAsync(this, "加入战局", Lang.C(577))) return;
        await Waiting.JoinAsync();
    }

    private void WaitOption_Click(object sender, RoutedEventArgs e)
    {
        Waiting.AutoJoin = WaitAutoJoin.IsChecked == true;
        Waiting.AutoSolo = WaitAutoSolo.IsChecked == true;
        if (Waiting.AutoSolo) App.Core.Log.Write(Lang.C(585));
    }

    private void WaitClear_Click(object sender, RoutedEventArgs e)
    {
        Waiting.ClearHistory();
        WaitUrlBox.Items.Clear();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        RefreshSlots();
        ShowBots(Bots.Bots);
    }

    private void RefreshSlots()
    {
        SlotARow.Description = BotService.DescribeTarget(Bots.GetSlot("甲"));
        SlotBRow.Description = BotService.DescribeTarget(Bots.GetSlot("乙"));
    }

    // ───────── Bot 列表 ─────────

    private void ShowBots(BotList? list)
    {
        BotPanel.Children.Clear();
        if (list is null) return;
        BotInfo.Text = string.Join("　", new[] { list.Date, list.Notice }.Where(s => s.Length > 0));
        if (list.Bots.Count == 0) BotInfo.Text += "　当前没有在线的 Bot。";

        for (var i = 0; i < list.Bots.Count; i++)
        {
            var bot = list.Bots[i];
            var target = "BOT" + (i + 1);
            var join = new Button { Content = "加入", MinWidth = 72 };
            join.Click += (_, _) => Bots.JoinBot(bot);
            var slot = new DropDownButton { Content = "存到槽位" };
            var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
            foreach (var name in new[] { "甲", "乙" })
            {
                var item = new MenuFlyoutItem { Text = $"战局{name}（总是加入第 {i + 1} 个 Bot）" };
                item.Click += (_, _) =>
                {
                    Bots.SetSlot(name, target);
                    RefreshSlots();
                };
                menu.Items.Add(item);
            }
            slot.Flyout = menu;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(join);
            actions.Children.Add(slot);
            BotPanel.Children.Add(new SettingRow { Title = bot.Name, Description = $"第 {i + 1} 个", Action = actions });
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        BotInfo.Text = "正在获取…";
        var list = await Bots.RefreshAsync(force: true);
        RefreshButton.IsEnabled = true;
        if (list is null) BotInfo.Text = Lang.C(339) + "可能是网络问题，或者 Bot 服务暂时没有开。";
        ShowBots(list);
    }

    private async void Status_Click(object sender, RoutedEventArgs e)
    {
        StatusButton.IsEnabled = false;
        var status = await Bots.QueryStatusAsync();
        StatusButton.IsEnabled = true;
        await Dialogs.MessageAsync(this, status is null ? "查询失败" : Lang.C(414), status ?? Lang.C(339));
    }

    // ───────── 槽位 ─────────

    private void JoinSlot_Click(object sender, RoutedEventArgs e) =>
        _ = Bots.JoinSlotAsync((string)((Button)sender).Tag);

    private async void EditSlot_Click(object sender, RoutedEventArgs e)
    {
        var slot = (string)((Button)sender).Tag;
        var current = Bots.GetSlot(slot);
        var isBot = current.StartsWith("BOT", StringComparison.Ordinal) && int.TryParse(current[3..], out _);

        var kind = new RadioButtons { Header = "按下「加入」或热键时" };
        kind.Items.Add("加入在线 Bot");
        kind.Items.Add("加入最近一次加入的战局");
        kind.Items.Add("加入自定义的战局（SteamJVP、Steam 好友，或一个网页地址）");
        kind.Items.Add("不启用");
        var number = new NumberBox
        {
            Header = "第几个 Bot", Minimum = 1, Maximum = 99, Width = 140, HorizontalAlignment = HorizontalAlignment.Left,
            Value = isBot ? int.Parse(current[3..]) : 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var custom = new TextBox
        {
            Header = "SteamJVP、好友的 SteamID64 或网页地址", IsSpellCheckEnabled = false, TextWrapping = TextWrapping.Wrap, MaxHeight = 120,
            PlaceholderText = "粘贴队友发来的 SteamJVP；或填 Steam 好友的 17 位 SteamID64（7656119 开头），加入时查他当前所在的战局；网页地址需以 http:// 或 https:// 开头",
            Text = isBot || current is "LAST1" or "" ? "" : current,
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        kind.SelectionChanged += (_, _) =>
        {
            number.Visibility = kind.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            custom.Visibility = kind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        };
        kind.SelectedIndex = isBot ? 0 : current == "LAST1" ? 1 : current.Length == 0 ? 3 : 2;

        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(kind);
        panel.Children.Add(number);
        panel.Children.Add(custom);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "设置战局" + slot, Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        string? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            switch (kind.SelectedIndex)
            {
                case 0: result = "BOT" + (double.IsNaN(number.Value) ? 1 : (int)number.Value); break;
                case 1: result = "LAST1"; break;
                case 3: result = ""; break;
                default:
                    var text = custom.Text.Trim();
                    var isUrl = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                    if (!isUrl && (text.StartsWith("7656119", StringComparison.Ordinal) ? !SteamFriendService.IsSteamId(text) : !BotService.IsValidJvp(text)))
                    {
                        error.Text = "这不是有效的 SteamJVP、SteamID64 或网页地址。";
                        args.Cancel = true;
                        return;
                    }
                    result = text;
                    break;
            }
        };
        if (await Dialogs.CustomAsync(this, dialog) != ContentDialogResult.Primary || result is null) return;
        Bots.SetSlot(slot, result);
        RefreshSlots();
    }

    // ───────── 工具 ─────────

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        var jvp = BotService.ReadLastJoined(out var error);
        if (jvp is null)
        {
            await Dialogs.MessageAsync(this, "提取失败", error);
            return;
        }
        var package = new DataPackage();
        package.SetText(jvp);
        Clipboard.SetContent(package);
        await Dialogs.MessageAsync(this, "已复制到剪贴板", jvp);
    }
}
