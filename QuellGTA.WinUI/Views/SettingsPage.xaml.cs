using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class SettingsPage : Page
{
    private static QuellCore Core => App.Core;
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        BuildExtras();

        TraySwitch.IsOn = Core.TrayIcon;
        CloseToTraySwitch.IsOn = Core.CloseToTray;
        CloseToTraySwitch.IsEnabled = Core.TrayIcon;
        StartMinimizedSwitch.IsOn = Core.StartMinimized;
        VolumeSlider.Value = Core.Sound.Volume;
        NotifySwitch.IsOn = Core.TrayNotifications;
        KillSwitch.IsOn = Core.KillEnabled;
        PathCheckSwitch.IsOn = Core.Game.SkipPathCheck;
        _ready = true;

        // 查询计划任务要启动 schtasks，放到后台
        Task.Run(() =>
        {
            var enabled = AutoStart.IsEnabled();
            DispatcherQueue.TryEnqueue(() =>
            {
                _ready = false;
                AutoStartSwitch.IsOn = enabled;
                _ready = true;
            });
        });
    }

    private async void AutoStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var enable = AutoStartSwitch.IsOn;
        var dataDir = Core.Settings.DataDirectory;
        var ok = await Task.Run(() => enable ? AutoStart.Enable(dataDir) : AutoStart.Disable());
        if (!ok && enable)
        {
            _ready = false;
            AutoStartSwitch.IsOn = false;
            _ready = true;
            await Dialogs.MessageAsync(this, "设置失败", "创建计划任务失败。");
            return;
        }
        if (enable)
        {
            // 原版：开启自启时顺带开启托盘图标（否则最小化启动后找不到窗口）
            TraySwitch.IsOn = true;
            Core.Log.Write("已设置开机自动运行。移动或删除程序文件后需要重新设置。", 1);
        }
        else
        {
            Core.Log.Write("已取消开机自动运行。", 1);
        }
    }

    private void Tray_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Core.TrayIcon = TraySwitch.IsOn;
        CloseToTraySwitch.IsEnabled = TraySwitch.IsOn;
        App.Window.ApplyTraySetting();
    }

    private void CloseToTray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.CloseToTray = CloseToTraySwitch.IsOn;
    }

    private void StartMinimized_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.StartMinimized = StartMinimizedSwitch.IsOn;
    }

    private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_ready) Core.Sound.Volume = (int)e.NewValue;
    }

    private void TestSound_Click(object sender, RoutedEventArgs e) => Core.Sound.Play(SoundService.Restore);

    private void Notify_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.TrayNotifications = NotifySwitch.IsOn;
    }

    private void Kill_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.KillEnabled = KillSwitch.IsOn;
    }

    private void PathCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.Game.SkipPathCheck = PathCheckSwitch.IsOn;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Core.Settings.DataDirectory);
        Shell.Open(Core.Settings.DataDirectory);
    }
}
