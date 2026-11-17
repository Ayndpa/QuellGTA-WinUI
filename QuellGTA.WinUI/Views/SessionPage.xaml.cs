using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class SessionPage : Page
{
    private static QuellCore Core => App.Core;
    private readonly bool _ready;

    public SessionPage()
    {
        InitializeComponent();

        // 单选项顺序：模式 1 ~ 4，最后一项是关闭（模式 0）
        ModeRadios.SelectedIndex = ModeIndex();
        DurationBox.Value = Math.Round(Core.SoloDurationMs / 1000.0);
        IntervalBox.Value = Core.SoloIntervalMs;
        HoverCombo.SelectedIndex = Core.HoverMode switch
        {
            HoverMode.AltF4BlockCloud => 1,
            HoverMode.AltF4Disconnect => 2,
            HoverMode.SwitchCharacter => 3,
            _ => 0,
        };
        RefreshSwitchKey();
        _ready = true;
        Unloaded += (_, _) => Core.Hotkeys.CancelCapture();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => RefreshLock();

    private static int ModeIndex() => Core.SoloMode == SoloMode.Off ? 4 : (int)Core.SoloMode - 1;

    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ModeRadios.SelectedIndex < 0) return;
        if (Core.SoloActive)
        {
            // 卡单过程中不允许换模式，否则会用错误的方式去恢复游戏
            ModeRadios.SelectedIndex = ModeIndex();
            return;
        }
        Core.SoloMode = ModeRadios.SelectedIndex == 4 ? SoloMode.Off : (SoloMode)(ModeRadios.SelectedIndex + 1);
    }

    private void Duration_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // 原版默认值是 10001 毫秒，这里保留多出来的 1 毫秒
        if (_ready && !double.IsNaN(sender.Value)) Core.SoloDurationMs = (int)Math.Round(sender.Value * 1000) + 1;
    }

    private void Interval_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !double.IsNaN(sender.Value)) Core.SoloIntervalMs = (int)sender.Value;
    }

    private void Hover_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Core.HoverMode = HoverCombo.SelectedIndex switch
        {
            1 => HoverMode.AltF4BlockCloud,
            2 => HoverMode.AltF4Disconnect,
            3 => HoverMode.SwitchCharacter,
            _ => HoverMode.Off,
        };
        RefreshSwitchKey();
    }

    private void RefreshSwitchKey()
    {
        SwitchKeyRow.Visibility = Core.HoverMode == HoverMode.SwitchCharacter ? Visibility.Visible : Visibility.Collapsed;
        SwitchKeyButton.Content = Hotkey.KeyName(Core.SwitchCharacterKey);
    }

    private void SwitchKey_Click(object sender, RoutedEventArgs e)
    {
        SwitchKeyButton.Content = "请按下按键…";
        Core.Hotkeys.BeginCapture(hotkey => DispatcherQueue.TryEnqueue(() =>
        {
            if (hotkey.Vk != 0x1B && hotkey.Vk <= 254) Core.SwitchCharacterKey = hotkey.Vk;
            RefreshSwitchKey();
        }));
    }

    // ───────── 战局锁 ─────────

    private void RefreshLock()
    {
        var status = Core.SessionLock.GetStatus();
        LockInfo.Severity = status.State switch
        {
            LockState.Locked => InfoBarSeverity.Success,
            LockState.Unlocked => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Warning,
        };
        LockInfo.Title = status.State switch
        {
            LockState.Locked => "已上锁",
            LockState.Unlocked => "未上锁",
            LockState.Corrupted => "锁文件损坏",
            _ => "无法使用",
        };
        LockInfo.Message = status.Message;

        var usable = status.State is LockState.Locked or LockState.Unlocked or LockState.Corrupted;
        LockButton.IsEnabled = usable;
        UnlockButton.IsEnabled = status.State is LockState.Locked or LockState.Corrupted;
        GameDirRow.Description = Core.Files.InstallDir.Length > 0
            ? Core.Files.InstallDir
            : "还不知道游戏装在哪。请在本程序运行时启动一次游戏。";

        var typed = PasswordBox.Text;
        PasswordBox.Items.Clear();
        foreach (var password in Core.SessionLock.SavedPasswords) PasswordBox.Items.Add(password);
        PasswordBox.Text = status.State == LockState.Locked ? status.Password
            : !string.IsNullOrEmpty(typed) ? typed
            : Core.SessionLock.SavedPasswords.FirstOrDefault() ?? "QuellGTA";
    }

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text ?? "";
        if (Core.GameRunning && !await Dialogs.ConfirmAsync(this, "游戏正在运行", "战局锁要重启游戏后才生效，游戏运行时文件也可能被占用。仍然继续？"))
            return;
        var result = Core.SessionLock.Lock(password);
        RefreshLock();
        await Dialogs.MessageAsync(this, result.Ok ? "战局锁已创建" : "创建失败", result.Message);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var result = Core.SessionLock.Unlock();
        RefreshLock();
        await Dialogs.MessageAsync(this, result.Ok ? "战局锁已删除" : "删除失败", result.Message);
    }

    private void RefreshLock_Click(object sender, RoutedEventArgs e) => RefreshLock();

    private void ClearPasswords_Click(object sender, RoutedEventArgs e)
    {
        Core.SessionLock.ClearSavedPasswords();
        RefreshLock();
    }
}
