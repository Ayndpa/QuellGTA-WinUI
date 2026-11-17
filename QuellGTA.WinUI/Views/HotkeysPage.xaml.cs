using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class HotkeysPage : Page
{
    private static QuellCore Core => App.Core;
    private readonly bool _ready;
    private readonly Dictionary<string, (SettingRow row, Button capture)> _rows = [];
    private string? _capturing;

    private static readonly (string name, string title, string description)[] Items =
    [
        ("杀掉键", "杀掉进程", "立即结束游戏进程。"),
        ("阻网键", "阻止联网", "创建防火墙规则，阻止游戏联网。"),
        ("禁卡键", "禁用网卡", "禁用所选网卡。"),
        ("复网键", "恢复网络", "删除防火墙规则并恢复网卡。"),
        ("卡单键", "战局卡单", "开始 / 结束卡单人战局。"),
        ("悬停键", "瞬间悬停", "松开按键时执行，需先在「战局」里选择悬停方式。"),
        ("连击左键", "连击左键", "自动连点鼠标左键。"),
        ("挂机键", "前台挂机", "松开按键时开始 / 停止挂机。"),
        ("计时键", "简易计时", "依次：开始计时 → 结束计时 → 重置。需先在「浮窗」里开启计时器。"),
        ("战局甲键", "加入战局甲", "松开按键时，加入「差传」页里槽位甲设置的战局。"),
        ("战局乙键", "加入战局乙", "松开按键时，加入「差传」页里槽位乙设置的战局。"),
        ("粘贴键", "粘贴文字", "松开按键时，把剪贴板里的文字打进游戏。需先在「宏键」页开启。"),
        ("英语解键", "解除英语锁定", "暂停英语输入锁定，直到按下回车或 Esc。"),
        ("指纹键", "自动指纹", "松开按键时识别指纹并自动按键。需先在「宏键」页开启。"),
        ("骇入键", "自动骇入", "松开按键时开始识别骇入画面并自动操作；运行期间再按一次中止。需先在「宏键」页开启。"),
        ("锁帧键", "RTSS 锁帧", "切到下一个帧数预设。需先在「浮窗」页开启 RTSS 锁帧控制。"),
        ("手柄跑键", "手柄偷速", "把虚拟手柄的左摇杆往前推，实现偷速跑。原版默认用 Caps Lock。需先在「宏键」页开启。"),
    ];

    public HotkeysPage()
    {
        InitializeComponent();
        BuildExtras();

        foreach (var (name, title, description) in Items)
        {
            var capture = new Button { MinWidth = 150, Tag = name };
            capture.Click += Capture_Click;
            var clear = new Button { Content = "停用", Tag = name };
            clear.Click += Clear_Click;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(capture);
            actions.Children.Add(MouseMenu(name));
            actions.Children.Add(clear);

            var row = new SettingRow { Title = title, Description = description, Action = actions };
            _rows[name] = (row, capture);
            HotkeyPanel.Children.Add(row);
        }
        RefreshKeys();

        ForegroundSwitch.IsOn = Core.HotkeysOnlyForeground;
        RunningSwitch.IsOn = Core.HotkeysOnlyRunning;
        RefreshEngine();
        _ready = true;

        Unloaded += (_, _) => CancelCapture();
    }

    private void RefreshKeys()
    {
        foreach (var (name, (_, capture)) in _rows)
            capture.Content = name == _capturing ? "请按下按键…" : Core.GetHotkey(name).ToString();
    }

    private void RefreshEngine() =>
        EngineRow.Title = Core.Hotkeys.IsRunning ? "热键引擎：运行中" : "热键引擎：未运行";

    private void CancelCapture()
    {
        if (_capturing is null) return;
        _capturing = null;
        Core.Hotkeys.CancelCapture();
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        var name = (string)((Button)sender).Tag;
        if (_capturing == name)
        {
            CancelCapture();
            RefreshKeys();
            return;
        }
        _capturing = name;
        RefreshKeys();
        Core.Hotkeys.BeginCapture(hotkey => DispatcherQueue.TryEnqueue(() =>
        {
            if (_capturing != name) return;
            _capturing = null;
            if (hotkey.Vk != 0x1B) // Esc = 取消
            {
                // 同一个按键只能绑定到一个功能上
                foreach (var other in QuellCore.HotkeyNames)
                    if (other != name && Core.GetHotkey(other) == hotkey) Core.SetHotkey(other, Hotkey.None);
                Core.SetHotkey(name, hotkey);
                Core.Log.Write($"热键「{name}」已设置为 {hotkey}。");
            }
            RefreshKeys();
        }));
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        Core.SetHotkey((string)((Button)sender).Tag, Hotkey.None);
        RefreshKeys();
    }

    private void Foreground_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.HotkeysOnlyForeground = ForegroundSwitch.IsOn;
    }

    private void Running_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) Core.HotkeysOnlyRunning = RunningSwitch.IsOn;
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        Core.Hotkeys.Restart();
        RefreshKeys();
        RefreshEngine();
        Core.Log.Write(Core.Hotkeys.IsRunning ? "热键引擎已重启。" : "热键引擎启动失败。", Core.Hotkeys.IsRunning ? 1 : 3);
    }
}
