using System.Collections.Specialized;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class HomePage : Page
{
    private static readonly SolidColorBrush Green = new(ColorHelper.FromArgb(255, 16, 185, 129));
    private static readonly SolidColorBrush Red = new(ColorHelper.FromArgb(255, 239, 68, 68));
    private static readonly SolidColorBrush Amber = new(ColorHelper.FromArgb(255, 245, 158, 11));
    private static readonly SolidColorBrush Gray = new(ColorHelper.FromArgb(255, 148, 163, 184));

    private static QuellCore Core => App.Core;

    public HomePage()
    {
        InitializeComponent();
        LogList.ItemsSource = Core.Log.Entries;
        Core.Log.Entries.CollectionChanged += OnLogChanged;
        Core.StateChanged += () => DispatcherQueue.TryEnqueue(Refresh);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && Core.Log.Entries.Count > 0)
            LogList.ScrollIntoView(Core.Log.Entries[^1]);
    }

    private void Refresh()
    {
        GameDot.Fill = Core.GameRunning ? Green : Gray;
        GameStateText.Text = Core.GameRunning ? "游戏运行中" : "游戏未运行";
        GameNameText.Text = Core.Game.ProcessName;

        var cut = Core.NetBlocked || Core.NicDisabled;
        NetDot.Fill = cut ? Red : Green;
        NetStateText.Text = Core.NetBlocked && Core.NicDisabled ? "已阻止联网 + 禁用网卡"
            : Core.NetBlocked ? "已阻止联网" : Core.NicDisabled ? "已禁用网卡" : "网络正常";
        NetDetailText.Text = cut ? "点击「恢复网络」解除" : "未创建阻止规则";

        SoloDot.Fill = Core.SoloActive ? Amber : Gray;
        SoloStateText.Text = Core.SoloActive ? "卡单中" : "未在卡单";
        SoloDetailText.Text = Core.SoloMode switch
        {
            SoloMode.SuspendProcess => $"暂停进程 · {Core.SoloDurationMs / 1000.0:0.#} 秒",
            SoloMode.SuspendWindow => $"挂起窗口 · {Core.SoloDurationMs / 1000.0:0.#} 秒",
            SoloMode.SystemMenu => $"右键菜单 · {Core.SoloDurationMs / 1000.0:0.#} 秒",
            SoloMode.BlockP2PPort => $"阻止 P2P 端口 · {Core.SoloDurationMs / 1000.0:0.#} 秒",
            _ => "功能已关闭",
        };
        SoloButtonText.Text = Core.SoloActive ? "结束卡单" : "战局卡单";

        KillKeyText.Text = Core.GetHotkey("杀掉键").ToString();
        BlockKeyText.Text = Core.GetHotkey("阻网键").ToString();
        NicKeyText.Text = Core.GetHotkey("禁卡键").ToString();
        RestoreKeyText.Text = Core.GetHotkey("复网键").ToString();
        SoloKeyText.Text = Core.GetHotkey("卡单键").ToString();

        CrosshairToggle.IsChecked = Core.Overlay.CrosshairEnabled;
        AfkToggle.IsChecked = Core.AfkActive;
        TimerButton.Visibility = Core.Overlay.TimerEnabled ? Visibility.Visible : Visibility.Collapsed;
        TimerButton.Content = Core.Overlay.TimerState switch
        {
            TimerState.Idle => Lang.C(241),
            TimerState.Running => Lang.C(242),
            _ => Lang.C(243),
        };
        var status = Core.SessionLock.GetStatus();
        LockLink.Content = status.State switch
        {
            LockState.Locked => $"战局锁：已上锁（{status.Password}）",
            LockState.Unlocked => "战局锁：未上锁",
            _ => "战局锁：不可用",
        };
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (Enum.TryParse<LaunchPlatform>((string)((MenuFlyoutItem)sender).Tag, out var platform))
            Core.Files.Launch(platform);
    }

    private void Crosshair_Click(object sender, RoutedEventArgs e) =>
        Core.Overlay.CrosshairEnabled = CrosshairToggle.IsChecked == true;

    private void Afk_Click(object sender, RoutedEventArgs e)
    {
        AfkToggle.IsChecked = Core.AfkActive; // 实际状态由挂机线程决定，这里先还原，等状态变化再刷新
        Run(Core.ToggleAfk);
    }

    private void Timer_Click(object sender, RoutedEventArgs e) => Core.AdvanceTimer();

    private void LockLink_Click(object sender, RoutedEventArgs e) => App.Window.NavigateTo("session");

    // 这些操作会执行外部命令或 Sleep，放到后台线程，避免卡住界面。
    private static void Run(Action action) => Task.Run(action);

    private void Kill_Click(object sender, RoutedEventArgs e) => Run(Core.Kill);
    private void Block_Click(object sender, RoutedEventArgs e) => Run(Core.BlockNetwork);
    private void Nic_Click(object sender, RoutedEventArgs e) => Run(Core.DisableNic);
    private void Restore_Click(object sender, RoutedEventArgs e) => Run(Core.RestoreNetwork);
    private void Solo_Click(object sender, RoutedEventArgs e) => Run(Core.ToggleSolo);

    private void ClearLog_Click(object sender, RoutedEventArgs e) => Core.Log.Entries.Clear();

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Core.Settings.DataDirectory, "Logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"QuellGTA_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(file, Core.Log.Export(), new UTF8Encoding(true));
            Shell.Open(file);
        }
        catch (Exception ex)
        {
            Core.Log.Write("保存日志失败：" + ex.Message, 3);
        }
    }
}
