using System.Collections.Specialized;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>概览：状态、最常用的操作、常用开关和最近的活动。</summary>
public sealed partial class DashboardPage : FeaturePage
{
    private static QuellCore Core => App.Core;
    private static readonly SolidColorBrush Green = new(ColorHelper.FromArgb(255, 16, 185, 129));
    private static readonly SolidColorBrush Red = new(ColorHelper.FromArgb(255, 239, 68, 68));
    private static readonly SolidColorBrush Amber = new(ColorHelper.FromArgb(255, 245, 158, 11));
    private static readonly SolidColorBrush Gray = new(ColorHelper.FromArgb(255, 148, 163, 184));

    private readonly (Microsoft.UI.Xaml.Shapes.Ellipse dot, TextBlock title, TextBlock detail)[] _status = new (Microsoft.UI.Xaml.Shapes.Ellipse, TextBlock, TextBlock)[3];
    private readonly StackPanel _recent = new() { Spacing = 2 };
    private TextBlock _soloLabel = null!;

    public DashboardPage() : base("概览", "")
    {
        // ───────── 状态 ─────────
        var statusGrid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        for (var i = 0; i < 3; i++)
        {
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            var detail = new TextBlock
            {
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            var text = new StackPanel();
            text.Children.Add(title);
            text.Children.Add(detail);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(dot);
            row.Children.Add(text);
            var card = Ui.Card(row);
            card.Margin = new Thickness(0);
            Grid.SetColumn(card, i);
            statusGrid.Children.Add(card);
            _status[i] = (dot, title, detail);
        }
        Add(statusGrid);

        // ───────── 快捷操作 ─────────
        Section("快捷操作");
        Add(Tiles.Row(
            Tiles.Action("\uE7E8", "杀掉进程", "杀掉键", () => Run(Core.Kill), out _),
            Tiles.Action("\uE83D", "阻止联网", "阻网键", () => Run(Core.BlockNetwork), out _),
            Tiles.Action("\uEB5E", "禁用网卡", "禁卡键", () => Run(Core.DisableNic), out _),
            Tiles.Action("\uE774", "恢复网络", "复网键", () => Run(Core.RestoreNetwork), out _),
            Tiles.Action("\uE77B", "战局卡单", "卡单键", () => Run(Core.ToggleSolo), out _soloLabel)));

        // ───────── 常用 ─────────
        Section("常用");
        var launch = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        foreach (var (text, platform) in new[] { ("Steam 增强版", LaunchPlatform.SteamEnhanced), ("Epic 增强版", LaunchPlatform.EpicEnhanced),
                     ("Steam 传承版", LaunchPlatform.SteamLegacy), ("Epic 传承版", LaunchPlatform.EpicLegacy) })
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => Core.Files.Launch(platform);
            launch.Items.Add(item);
        }
        Add(Ui.Row("启动游戏", "", new DropDownButton { Content = "选择版本", Flyout = launch }, "\uE768"));
        Add(Ui.Toggle("外置准星", "在屏幕中央显示准星。", () => Core.Overlay.CrosshairEnabled, v => Core.Overlay.CrosshairEnabled = v, "\uE710"));
        var afk = new Button { MinWidth = 100 };
        afk.Click += (_, _) => Run(Core.ToggleAfk);
        var afkCard = Ui.Row("前台挂机", "", afk, "\uE823");
        Add(afkCard);
        Add(Ui.Toggle("自动挂机防踢", "长时间无操作时自动按键。", () => Core.AntiAfkEnabled, v => Core.AntiAfkEnabled = v, "\uE916"));
        Add(Ui.Toggle("自动骇入", "", () => Core.AutoHack.Enabled, v => Core.AutoHack.Enabled = v, "\uE7EF"));
        Add(Ui.Toggle("英语输入锁定", "", () => Core.EnglishLock, v => Core.EnglishLock = v, "\uE8D3"));
        var lockCard = Ui.Link("战局锁", "", "\uE72E", () => App.Window.NavigateTo("session"));
        Add(lockCard);

        // ───────── 最近活动 ─────────
        Section("最近活动");
        var all = new HyperlinkButton { Content = "查看全部", HorizontalAlignment = HorizontalAlignment.Right };
        all.Click += (_, _) => App.Window.NavigateTo("activity");
        var recent = new StackPanel();
        recent.Children.Add(_recent);
        recent.Children.Add(all);
        Add(Ui.Card(recent));

        OnCoreState(() =>
        {
            Refresh();
            afkCard.Description = Core.AfkActive ? "正在挂机" : "把游戏切到前台并替你按住按键。";
            afk.Content = Core.AfkActive ? "停止" : "开始";
            var status = Core.SessionLock.GetStatus();
            lockCard.Description = status.State switch
            {
                LockState.Locked => $"已上锁（{status.Password}）",
                LockState.Unlocked => "未上锁",
                _ => "不可用",
            };
        });
        NotifyCollectionChangedEventHandler logHandler = (_, _) => DispatcherQueue.TryEnqueue(RefreshRecent);
        Loaded += (_, _) => { Core.Log.Entries.CollectionChanged -= logHandler; Core.Log.Entries.CollectionChanged += logHandler; RefreshRecent(); };
        Unloaded += (_, _) => Core.Log.Entries.CollectionChanged -= logHandler;
    }

    private void Refresh()
    {
        Set(0, Core.GameRunning ? Green : Gray, Core.GameRunning ? "游戏运行中" : "游戏未运行", Core.Game.ProcessName);
        var cut = Core.NetBlocked || Core.NicDisabled;
        Set(1, cut ? Red : Green,
            Core.NetBlocked && Core.NicDisabled ? "已断网（规则 + 网卡）" : Core.NetBlocked ? "已阻止联网" : Core.NicDisabled ? "已禁用网卡" : "网络正常",
            cut ? "点「恢复网络」解除" : "没有生效的阻止规则");
        Set(2, Core.SoloActive ? Amber : Gray, Core.SoloActive ? "卡单中" : "未在卡单", Core.SoloMode switch
        {
            SoloMode.SuspendProcess => $"暂停进程 · {Core.SoloDurationMs / 1000.0:0.#} 秒",
            SoloMode.SuspendWindow => $"挂起窗口 · {Core.SoloDurationMs / 1000.0:0.#} 秒",
            SoloMode.SystemMenu => $"右键菜单 · {Core.SoloDurationMs / 1000.0:0.#} 秒",
            SoloMode.BlockP2PPort => $"阻止 P2P 端口 · {Core.SoloDurationMs / 1000.0:0.#} 秒",
            _ => "卡单功能已关闭",
        });
        _soloLabel.Text = Core.SoloActive ? "结束卡单" : "战局卡单";

        void Set(int i, Brush brush, string title, string detail)
        {
            _status[i].dot.Fill = brush;
            _status[i].title.Text = title;
            _status[i].detail.Text = detail;
        }
    }

    private void RefreshRecent()
    {
        _recent.Children.Clear();
        var entries = Core.Log.Entries;
        if (entries.Count == 0)
        {
            _recent.Children.Add(new TextBlock { Text = "还没有记录。", Opacity = 0.6 });
            return;
        }
        foreach (var entry in entries.Skip(Math.Max(0, entries.Count - 5)))
        {
            var row = new Grid { ColumnSpacing = 10, Opacity = entry.Emphasis, Padding = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = entry.Time, FontFamily = new FontFamily("Consolas"), Opacity = 0.7 });
            var icon = new FontIcon { Glyph = entry.Glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);
            var text = new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            _recent.Children.Add(row);
        }
    }
}
