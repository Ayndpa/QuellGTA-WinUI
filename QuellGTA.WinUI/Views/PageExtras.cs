using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuellGTA.Services;
using Windows.ApplicationModel.DataTransfer;

namespace QuellGTA.Views;

// 各页面后加的功能区。每个页面在 XAML 末尾留了一个 ExtraPanel，这里用 Ui 里的方法往里填。

public sealed partial class GamePage
{
    private void BuildExtras()
    {
        var core = App.Core;
        var panel = ExtraPanel.Children;

        panel.Add(Ui.Header("游戏窗口"));
        var hide = new ToggleSwitch { IsOn = core.GameWindowHidden };
        hide.Toggled += (_, _) =>
        {
            if (hide.IsOn == core.GameWindowHidden) return;
            if (!core.SetGameWindowHidden(hide.IsOn)) hide.IsOn = core.GameWindowHidden;
        };
        panel.Add(Ui.Row("窗口隐藏", "把游戏窗口藏起来，游戏照常运行。关掉开关就显示回来。隐藏期间不要退出本程序，否则只能重启游戏。", hide));
        panel.Add(Ui.Buttons("窗口移动", "把窗口化的游戏移到屏幕的角落，大小不变。",
            ("↖", () => core.MoveGameWindow(-1, -1)), ("↗", () => core.MoveGameWindow(1, -1)),
            ("↙", () => core.MoveGameWindow(-1, 1)), ("↘", () => core.MoveGameWindow(1, 1)),
            ("←", () => core.MoveGameWindow(-1, 0)), ("→", () => core.MoveGameWindow(1, 0))));
        var x = new NumberBox { Header = "X", Width = 110, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var y = new NumberBox { Header = "Y", Width = 110, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var move = new Button { Content = "移动", VerticalAlignment = VerticalAlignment.Bottom };
        move.Click += (_, _) =>
        {
            if (!double.IsNaN(x.Value) && !double.IsNaN(y.Value) && !core.MoveGameWindowTo((int)x.Value, (int)y.Value))
                core.Log.Write("移动失败：游戏未在运行。");
        };
        var position = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        position.Children.Add(x);
        position.Children.Add(y);
        position.Children.Add(move);
        panel.Add(Ui.Row("移到指定位置", "手动输入窗口左上角的屏幕坐标（像素），多显示器时可以填负数。", position));

        panel.Add(Ui.Header("游戏启动和退出时"));
        panel.Add(Ui.Toggle("游戏启动后结束启动器", "检测到游戏启动后，按「断网」页设置的延迟结束 Rockstar 启动器。",
            () => core.KillLauncherOnGameStart, v => core.KillLauncherOnGameStart = v));
        panel.Add(Ui.Toggle("游戏退出后结束启动器", "", () => core.KillLauncherOnGameExit, v => core.KillLauncherOnGameExit = v));
        panel.Add(Ui.Toggle("游戏退出时关闭本程序", "", () => core.ExitWithGame, v => core.ExitWithGame = v));

        panel.Add(Ui.Header("第三方工具"));
        panel.Add(Ui.Note("这些工具由各自的作者开发和发布，本程序只提供入口：已经开着就切过去，记得位置就启动，否则打开下载页。勾选「随游戏启动」后，检测到游戏启动时自动运行。"));
        foreach (var tool in QuellCore.Tools)
        {
            var auto = new CheckBox { Content = "随游戏启动", IsChecked = core.GetToolAutoRun(tool), MinWidth = 0 };
            auto.Click += (_, _) => core.SetToolAutoRun(tool, auto.IsChecked == true);
            var run = new Button { Content = "运行" };
            var row = new SettingRow { Title = tool.Name };
            void Describe() => row.Description = core.GetToolPath(tool) is { Length: > 0 } path ? path : "还没有记录它的位置";
            run.Click += (_, _) =>
            {
                var message = core.RunTool(tool);
                Describe();
                core.Log.Write($"{tool.Name}：{message}");
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            actions.Children.Add(auto);
            actions.Children.Add(run);
            row.Action = actions;
            Describe();
            panel.Add(row);
        }
    }
}

public sealed partial class RepairPage
{
    private void BuildExtras()
    {
        var files = App.Core.Files;
        var panel = ExtraPanel.Children;

        panel.Add(Ui.Header("客户端文件"));
        panel.Add(Ui.Note("「修复无法进线上」就是上面的「备份后删除设置文件」。下面两项会改动游戏安装目录，请先看清说明。"));

        var gameDir = new TextBox { Width = 380, PlaceholderText = "游戏安装目录", Text = files.InstallDir, IsSpellCheckEnabled = false };
        var repair = new Button { Content = "修复" };
        repair.Click += async (_, _) =>
        {
            var dir = gameDir.Text.Trim().Trim('"');
            if (!await Dialogs.ConfirmAsync(this, "修复客户端文件", Lang.C(419) + "\n\n" + Lang.C(420))) return;
            if (!await Dialogs.ConfirmAsync(this, "再次确认",
                    $"将把以下目录里除 .rpf 以外的所有文件，以及 x64\\data 文件夹移到回收站：\n\n{dir}\n\n之后必须到 Steam / Epic 验证文件完整性才能再启动游戏。{Lang.C(421)}", "开始修复"))
                return;
            var result = await Task.Run(() => files.RepairClientFiles(dir));
            await Dialogs.MessageAsync(this, result.Ok ? "操作完成" : "操作未完成", result.Message);
        };
        var repairPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        repairPanel.Children.Add(gameDir);
        repairPanel.Children.Add(repair);
        panel.Add(Ui.Row("修复客户端文件", "错误的命令行、MOD、修改器导致闪退时使用：清掉安装目录里除资源包以外的文件，再让平台重新下载。", repairPanel));

        var source = new TextBox { Header = "A：已安装好的游戏目录", Width = 420, IsSpellCheckEnabled = false };
        var target = new TextBox { Header = "B：另一个平台的（空的）游戏目录", Width = 420, IsSpellCheckEnabled = false };
        var link = new Button { Content = "创建链接" };
        link.Click += async (_, _) =>
        {
            if (!await Dialogs.ConfirmAsync(this, "多平台共用资源文件",
                    $"{Lang.C(426)}\n\nA    {source.Text}\nB    {target.Text}\n\n{Lang.C(432)}", "开始链接"))
                return;
            var result = await Task.Run(() => files.LinkSharedResources(source.Text, target.Text));
            await Dialogs.MessageAsync(this, result.Ok ? "操作完成" : "操作未完成", result.Message);
        };
        var linkPanel = new StackPanel { Spacing = 8 };
        linkPanel.Children.Add(new TextBlock { Text = "多平台共用资源文件" });
        linkPanel.Children.Add(new TextBlock
        {
            Text = "在 Steam 和 Epic 上都有游戏时，让 B 平台通过链接使用 A 平台已经下载好的文件，只占一份磁盘空间。两个目录必须在同一个磁盘分区。创建后到 B 平台取消下载再重新开始，它会直接进入验证。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        linkPanel.Children.Add(source);
        linkPanel.Children.Add(target);
        linkPanel.Children.Add(link);
        panel.Add(Ui.Card(linkPanel));
    }
}

public sealed partial class TotpPage
{
    private void BuildExtras()
    {
        var totp = App.Core.Totp;
        var settings = App.Core.Settings;
        totp.UseNetworkTime = settings.GetBool("扩展", "令牌网络时间", false);

        var toggle = new ToggleSwitch { IsOn = totp.UseNetworkTime };
        var row = Ui.Row("使用网络时间", "电脑时钟不准时验证码会无效。开启后向常用网站发一次请求，用它回应的时间来校准，不改动系统时间。", toggle);
        async Task SyncAsync()
        {
            var ok = await totp.SyncNetworkTimeAsync();
            row.Description = ok
                ? $"已校准：本机时间比网络时间{(totp.NetworkOffset.TotalSeconds >= 0 ? "慢" : "快")} {Math.Abs(totp.NetworkOffset.TotalSeconds):0.0} 秒。"
                : "校准失败（网络不通），暂时仍按本机时间计算。";
            _lastStep = -1;
        }
        toggle.Toggled += async (_, _) =>
        {
            totp.UseNetworkTime = toggle.IsOn;
            settings.SetBool("扩展", "令牌网络时间", toggle.IsOn);
            if (toggle.IsOn) await SyncAsync();
            _lastStep = -1;
        };
        ExtraPanel.Children.Add(row);
        if (totp.UseNetworkTime) _ = SyncAsync();
    }

    /// <summary>迁移到其他验证器：显示标准的 otpauth 链接。</summary>
    private async Task MigrateAsync(TotpToken token)
    {
        if (!await Dialogs.ConfirmAsync(this, "迁移到其他验证器",
                "接下来会显示这个令牌的迁移链接。链接里包含密钥本身，任何拿到它的人都能生成你的验证码。请确认周围没有人在看屏幕，也不要截图发给别人。", "显示"))
            return;
        var uri = TotpService.ExportUri(token);
        var box = new TextBox { Text = uri, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Width = 440 };
        var copy = new Button { Content = "复制链接" };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(uri);
            Clipboard.SetContent(package);
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "在其他验证器里选择「手动输入 / 粘贴链接」，或者把这段链接转成二维码后扫描。本程序的「添加令牌」也可以直接粘贴它，用来在另一台电脑上恢复。",
            TextWrapping = TextWrapping.Wrap, Width = 440,
        });
        content.Children.Add(box);
        content.Children.Add(copy);
        await Dialogs.CustomAsync(this, new ContentDialog { Title = token.Name, Content = content, CloseButtonText = "关闭" });
    }
}
