using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class GamePage : Page
{
    private static QuellCore Core => App.Core;
    private bool _ready;

    public GamePage()
    {
        InitializeComponent();
        Loaded += (_, _) => OnShown();
        BuildExtras();
        AutoDetectSwitch.IsOn = Core.Game.AutoDetect;
        DualProfileSwitch.IsOn = Core.Profiles.DualProfile;
        CustomBox.Text = Core.Game.CustomProcessName;
    }

    /// <summary>页面每次显示时刷新（页面由主窗口直接创建，不经过 Frame 导航）。</summary>
    private void OnShown()
    {
        _ready = false;
        EditionCombo.SelectedIndex = Core.Game.Edition switch
        {
            GameEdition.Legacy => 1,
            GameEdition.Custom => 2,
            _ => 0,
        };
        RefreshEdition();
        RefreshArgs();
        RefreshGraphics();
        _ready = true;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (Enum.TryParse<LaunchPlatform>((string)((Button)sender).Tag, out var platform))
            Core.Files.Launch(platform);
    }

    // ───────── 版本 ─────────

    private void RefreshEdition()
    {
        EditionRow.Description = $"当前进程：{Core.Game.ProcessName}　文档目录：{Core.Game.DocumentsDirName}";
        CustomRow.Visibility = Core.Game.Edition == GameEdition.Custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutoDetect_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.Game.AutoDetect = AutoDetectSwitch.IsOn;
    }

    private async void DualProfile_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || DualProfileSwitch.IsOn == Core.Profiles.DualProfile) return;
        if (DualProfileSwitch.IsOn && Core.Game.Edition == GameEdition.Custom)
        {
            DualProfileSwitch.IsOn = false;
            await Dialogs.MessageAsync(this, "自动切换双版本存档", Lang.C(475));
            return;
        }
        Core.Profiles.DualProfile = DualProfileSwitch.IsOn;
        if (DualProfileSwitch.IsOn) await Dialogs.MessageAsync(this, "自动切换双版本存档", Lang.C(474) + "\n\n" + Lang.C(475));
    }

    private void Edition_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var edition = EditionCombo.SelectedIndex switch
        {
            1 => GameEdition.Legacy,
            2 => GameEdition.Custom,
            _ => GameEdition.Enhanced,
        };
        var before = Core.Game.Edition;
        Core.Game.Apply(edition, CustomBox.Text);
        if (edition == GameEdition.Custom && AutoDetectSwitch.IsOn)
            AutoDetectSwitch.IsOn = false; // 原版：自定义进程时自动检测不可用
        if (edition == GameEdition.Custom && DualProfileSwitch.IsOn)
            DualProfileSwitch.IsOn = false; // 自定义进程时双版本存档也不可用
        Core.Log.Write($"游戏版本已切换：{Core.Game.ProcessName}", 1);
        RefreshEdition();
        RefreshGraphics();
        Core.OnEditionChanged(before);
    }

    private void ApplyCustom()
    {
        if (!_ready || Core.Game.Edition != GameEdition.Custom || string.IsNullOrWhiteSpace(CustomBox.Text)) return;
        Core.Game.Apply(GameEdition.Custom, CustomBox.Text);
        CustomBox.Text = Core.Game.ProcessName;
        RefreshEdition();
    }

    private void Custom_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) ApplyCustom();
    }

    private void Custom_LostFocus(object sender, RoutedEventArgs e) => ApplyCustom();

    // ───────── 启动选项（args.txt） ─────────

    private void RefreshArgs()
    {
        var known = Core.Files.InstallDir.Length > 0;
        ArgsInfo.IsOpen = !known;
        ArgsRow.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        ArgsPanel.Children.Clear();
        if (!known) return;

        var args = Core.Files.GetArgs();
        foreach (var (flag, title, hint) in GameFilesService.LaunchFlags)
        {
            var toggle = new ToggleSwitch { IsOn = args.Contains(flag), Tag = flag };
            toggle.Toggled += Arg_Toggled;
            ArgsPanel.Children.Add(new SettingRow { Title = title, Description = $"{hint}（{flag}）", Action = toggle });
        }
        var others = args.Where(a => GameFilesService.LaunchFlags.All(f => f.flag != a)).ToList();
        ArgsRow.Description = others.Count > 0
            ? $"args.txt 里还有其他参数：{string.Join(" ", others)}。清空会把它们一起删除（移到回收站）。"
            : $"删除 {Core.Files.InstallDir}\\args.txt（移到回收站）。";
    }

    private async void Arg_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var toggle = (ToggleSwitch)sender;
        if (Core.GameRunning && !await Dialogs.ConfirmAsync(this, "游戏正在运行", Lang.C(359)))
        {
            _ready = false;
            toggle.IsOn = !toggle.IsOn;
            _ready = true;
            return;
        }
        var result = Core.Files.SetArg((string)toggle.Tag, toggle.IsOn);
        if (!result.Ok) await Dialogs.MessageAsync(this, "修改失败", result.Message);
        _ready = false;
        RefreshArgs();
        _ready = true;
    }

    private async void ClearArgs_Click(object sender, RoutedEventArgs e)
    {
        await Dialogs.RunAsync(this, "清空启动选项", Lang.C(359), Core.Files.ClearArgs);
        _ready = false;
        RefreshArgs();
        _ready = true;
    }

    // ───────── 画质（settings.xml） ─────────

    private void RefreshGraphics()
    {
        GraphicsPanel.Children.Clear();
        if (!File.Exists(Core.Files.GraphicsFile))
        {
            GraphicsHint.Text = Lang.C(274);
            return;
        }
        var card = Core.Files.VideoCard();
        GraphicsHint.Text = "直接修改 settings.xml 里的数值，可以设到游戏菜单里选不到的档位。"
                            + (card.Length > 0 ? $"这份配置对应的显卡：{card}。" : "") + "修改后要重启游戏才生效。";

        foreach (var (tag, title, hint) in GameFilesService.GraphicsOptions)
        {
            var current = Core.Files.GetGraphicsValue(tag);
            if (current is null) continue; // 当前版本的配置文件里没有这一项
            var box = new TextBox { Text = current, Width = 120 };
            var apply = new Button { Content = "应用" };
            apply.Click += async (_, _) =>
            {
                var result = Core.Files.SetGraphicsValue(tag, box.Text);
                if (!result.Ok) await Dialogs.MessageAsync(this, "修改失败", result.Message);
                else Core.Log.Write(result.Message);
                RefreshGraphics();
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(box);
            actions.Children.Add(apply);
            GraphicsPanel.Children.Add(new SettingRow
            {
                Title = title, Description = $"{hint} 当前值：{current}", Action = actions,
            });
        }
    }
}
