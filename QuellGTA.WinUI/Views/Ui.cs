using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>
/// 用代码拼页面的小工具。所有设置项都是 SettingsCard / SettingsExpander（与 Windows 设置、PowerToys 同一套控件），
/// 用这些方法直接绑到读写设置的委托上，不用为每一项写 XAML 和事件处理。
/// </summary>
public static class Ui
{
    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public static IconElement? Icon(string? glyph) => string.IsNullOrEmpty(glyph) ? null : new FontIcon { Glyph = glyph };

    /// <summary>分组标题。</summary>
    public static TextBlock Header(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["SectionHeaderStyle"],
    };

    /// <summary>分组下方的一段说明文字。</summary>
    public static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(1, 0, 0, 8),
        Foreground = Secondary,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
    };

    public static SettingsCard Row(string title, string description, object? action, string? glyph = null) => new()
    {
        Header = title,
        Description = string.IsNullOrEmpty(description) ? null! : description,
        HeaderIcon = Icon(glyph)!,
        Content = action!,
    };

    /// <summary>可点击的卡片（右侧显示箭头），用于跳转或打开对话框。</summary>
    public static SettingsCard Link(string title, string description, string? glyph, Action click, string? actionGlyph = null)
    {
        var card = Row(title, description, null, glyph);
        card.IsClickEnabled = true;
        if (actionGlyph is not null) card.ActionIcon = new FontIcon { Glyph = actionGlyph };
        card.Click += (_, _) => click();
        return card;
    }

    /// <summary>可展开的卡片：标题行右侧放 headerContent，展开后是 items 里的子设置。</summary>
    public static SettingsExpander Expander(string title, string description, string? glyph, object? headerContent, params UIElement[] items)
    {
        var expander = new SettingsExpander
        {
            Header = title,
            Description = string.IsNullOrEmpty(description) ? null! : description,
            HeaderIcon = Icon(glyph)!,
            Content = headerContent!,
        };
        foreach (var item in items) expander.Items.Add(item);
        return expander;
    }

    public static ToggleSwitch Switch(Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = get(), OnContent = "开", OffContent = "关" };
        toggle.Toggled += (_, _) => set(toggle.IsOn);
        return toggle;
    }

    public static SettingsCard Toggle(string title, string description, Func<bool> get, Action<bool> set, string? glyph = null) =>
        Row(title, description, Switch(get, set), glyph);

    public static NumberBox NumberBox(double min, double max, Func<double> get, Action<double> set, double step = 1, double width = 140)
    {
        var box = new NumberBox
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, Width = width,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = get(),
        };
        box.ValueChanged += (_, _) => { if (!double.IsNaN(box.Value)) set(box.Value); };
        return box;
    }

    public static SettingsCard Number(string title, string description, double min, double max,
        Func<double> get, Action<double> set, double step = 1, string? glyph = null) =>
        Row(title, description, NumberBox(min, max, get, set, step), glyph);

    public static ComboBox Combo(string[] options, Func<int> get, Action<int> set, double width = 220)
    {
        var combo = new ComboBox { MinWidth = width };
        foreach (var option in options) combo.Items.Add(option);
        combo.SelectedIndex = Math.Clamp(get(), 0, options.Length - 1);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) set(combo.SelectedIndex); };
        return combo;
    }

    public static SettingsCard Choice(string title, string description, string[] options, Func<int> get, Action<int> set,
        double width = 220, string? glyph = null) =>
        Row(title, description, Combo(options, get, set, width), glyph);

    /// <summary>文本框：失去焦点或按回车时保存。</summary>
    public static SettingsCard Text(string title, string description, Func<string> get, Action<string> set,
        string placeholder = "", double width = 280, string? glyph = null)
    {
        var box = new TextBox { Text = get(), PlaceholderText = placeholder, Width = width, IsSpellCheckEnabled = false };
        void Commit()
        {
            set(box.Text);
            box.Text = get();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Commit(); };
        return Row(title, description, box, glyph);
    }

    public static StackPanel ButtonRow(params (string text, Action click)[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (text, click) in buttons)
        {
            var button = new Button { Content = text };
            button.Click += (_, _) => click();
            panel.Children.Add(button);
        }
        return panel;
    }

    public static SettingsCard Buttons(string title, string description, params (string text, Action click)[] buttons) =>
        Row(title, description, ButtonRow(buttons));

    public static SettingsCard Buttons(string title, string description, string glyph, params (string text, Action click)[] buttons) =>
        Row(title, description, ButtonRow(buttons), glyph);

    /// <summary>选一个按键（不带组合键）：点按钮后按下要用的键，Esc 取消。</summary>
    public static SettingsCard Key(FrameworkElement owner, string title, string description, Func<int> get, Action<int> set, string? glyph = null)
    {
        var button = new Button { Content = Hotkey.KeyName(get()), MinWidth = 120 };
        button.Click += (_, _) =>
        {
            button.Content = "请按下按键…";
            App.Core.Hotkeys.BeginCapture(hotkey => owner.DispatcherQueue.TryEnqueue(() =>
            {
                if (hotkey.Vk != 0x1B && hotkey.Vk <= 254) set(hotkey.Vk);
                button.Content = Hotkey.KeyName(get());
            }));
        };
        return Row(title, description, button, glyph);
    }

    public static Border Card(UIElement content) => new()
    {
        Style = (Style)Application.Current.Resources["CardStyle"],
        Margin = new Thickness(0, 0, 0, 4),
        Child = content,
    };

    public static InfoBar Info(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational) => new()
    {
        Title = title, Message = message, Severity = severity, IsOpen = true, IsClosable = false, Margin = new Thickness(0, 0, 0, 4),
    };

    // ───────── 快捷键 ─────────

    /// <summary>把快捷键画成一排按键图标；没设置时显示「未设置」。</summary>
    public static StackPanel KeyChips(Hotkey hotkey, double fontSize = 13)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (!hotkey.IsSet)
        {
            panel.Children.Add(new TextBlock { Text = "未设置", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, FontSize = fontSize });
            return panel;
        }
        var keys = new List<string>();
        if (hotkey.Mods.HasFlag(HotkeyMods.Ctrl)) keys.Add("Ctrl");
        if (hotkey.Mods.HasFlag(HotkeyMods.Alt)) keys.Add("Alt");
        if (hotkey.Mods.HasFlag(HotkeyMods.Shift)) keys.Add("Shift");
        keys.Add(Hotkey.KeyName(hotkey.Vk));
        foreach (var key in keys)
            panel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["KeyChipStyle"],
                Child = new TextBlock
                {
                    Text = key, FontSize = fontSize, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
                },
            });
        return panel;
    }

    /// <summary>
    /// 快捷键设置卡：右侧显示当前按键，点击弹出录制对话框。name 是 QuellCore.HotkeyNames 里的名字。
    /// 任何地方改了快捷键，所有快捷键卡都会刷新（冲突时另一项会被清空）。
    /// </summary>
    public static SettingsCard Shortcut(FrameworkElement owner, string name, string? description = null)
    {
        var info = Shortcuts.Get(name);
        var host = new ContentControl();
        var card = Row(info.Title, description ?? info.Description, host, info.Glyph);
        card.IsClickEnabled = true;
        card.ActionIcon = new FontIcon { Glyph = "\uE70F" }; // 编辑
        void Refresh() => host.Content = KeyChips(App.Core.GetHotkey(name));
        Refresh();
        card.Click += async (_, _) => await ShortcutDialog.EditAsync(owner, name);
        Action handler = () => owner.DispatcherQueue.TryEnqueue(Refresh);
        Shortcuts.Changed += handler;
        card.Unloaded += (_, _) => Shortcuts.Changed -= handler;
        card.Loaded += (_, _) => { Shortcuts.Changed -= handler; Shortcuts.Changed += handler; Refresh(); };
        return card;
    }
}

/// <summary>大号操作按钮：图标 + 名称 + 快捷键提示，用在概览和网络页顶部。</summary>
public static class Tiles
{
    public static Button Action(string glyph, string title, string? shortcut, Action click, out TextBlock label)
    {
        label = new TextBlock { Text = title, HorizontalAlignment = HorizontalAlignment.Center };
        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new FontIcon { Glyph = glyph, FontSize = 22 });
        stack.Children.Add(label);
        var keyHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85 };
        stack.Children.Add(keyHost);
        void Refresh()
        {
            if (shortcut is null) return;
            var hotkey = App.Core.GetHotkey(shortcut);
            keyHost.Content = hotkey.IsSet ? Ui.KeyChips(hotkey, 11) : null;
        }
        Refresh();
        var button = new Button
        {
            Content = stack, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 104, Padding = new Thickness(8, 12, 8, 12),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        button.Click += (_, _) => click();
        Action handler = () => button.DispatcherQueue.TryEnqueue(Refresh);
        button.Loaded += (_, _) => { Shortcuts.Changed -= handler; Shortcuts.Changed += handler; Refresh(); };
        button.Unloaded += (_, _) => Shortcuts.Changed -= handler;
        return button;
    }

    /// <summary>把几个按钮等宽排成一行。</summary>
    public static Grid Row(params UIElement[] items)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        for (var i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn((FrameworkElement)items[i], i);
            grid.Children.Add(items[i]);
        }
        return grid;
    }
}

/// <summary>全局快捷键的名字、标题、说明和图标，各页面和概览、设置页共用。</summary>
public static class Shortcuts
{
    public sealed record Info(string Name, string Title, string Description, string Glyph);

    public static readonly Info[] All =
    [
        new("杀掉键", "杀掉进程", "立即结束游戏进程。", "\uE7E8"),
        new("阻网键", "阻止联网", "按「阻止联网」里的设置断开游戏的网络。", "\uE83D"),
        new("禁卡键", "禁用网卡", "停用所选网卡。", "\uEB5E"),
        new("复网键", "恢复网络", "删除阻止规则并恢复网卡。", "\uE774"),
        new("卡单键", "战局卡单", "开始卡单；卡单过程中再按一次立即结束。", "\uE77B"),
        new("悬停键", "瞬间悬停", "松开按键时执行（切换角色方式是按住）。", "\uE916"),
        new("连击左键", "连击左键", "按设置的触发方式连点鼠标左键。", "\uE962"),
        new("挂机键", "前台挂机", "松开按键时开始 / 停止挂机。", "\uE823"),
        new("计时键", "简易计时", "依次：开始计时 → 结束计时 → 重置。", "\uE916"),
        new("战局甲键", "加入战局甲", "松开按键时加入槽位甲设置的战局。", "\uE724"),
        new("战局乙键", "加入战局乙", "松开按键时加入槽位乙设置的战局。", "\uE724"),
        new("粘贴键", "粘贴文字", "松开按键时把剪贴板里的文字打进游戏。", "\uE77F"),
        new("英语解键", "解除英语锁定", "暂停英语输入锁定，直到按下回车或 Esc。", "\uE8D3"),
        new("指纹键", "自动指纹", "在指纹破解画面松开按键时识别并自动按键。", "\uE928"),
        new("骇入键", "自动骇入", "松开按键时开始识别骇入画面并自动操作；运行中再按一次中止。", "\uE7EF"),
        new("锁帧键", "切换锁帧预设", "按「一 → 二 → 三」循环切换 RTSS 的帧数预设。", "\uE9E9"),
        new("手柄跑键", "手柄偷速", "把虚拟手柄的左摇杆往前推，实现偷速跑。", "\uE7FC"),
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Info> PluginInfos = new();

    /// <summary>插件注册的快捷键的说明。</summary>
    public static void Register(Info info) => PluginInfos[info.Name] = info;

    /// <summary>主程序的快捷键加上插件的快捷键，「设置」页的全部快捷键列表用。</summary>
    public static IEnumerable<Info> AllWithPlugins => All.Concat(PluginInfos.Values.OrderBy(i => i.Name, StringComparer.Ordinal));

    public static Info Get(string name) =>
        All.FirstOrDefault(i => i.Name == name) ?? (PluginInfos.TryGetValue(name, out var info) ? info : new Info(name, name, "", "\uE765"));

    /// <summary>任何快捷键被修改后触发。</summary>
    public static event Action? Changed;

    public static void RaiseChanged() => Changed?.Invoke();

    /// <summary>保存一个快捷键；同一个按键已被别的功能使用时，把那一项清空。返回被清空的那一项的标题。</summary>
    public static string? Assign(string name, Hotkey hotkey)
    {
        string? replaced = null;
        if (hotkey.IsSet)
            foreach (var other in App.Core.AllHotkeyNames)
                if (other != name && App.Core.GetHotkey(other) == hotkey)
                {
                    App.Core.SetHotkey(other, Hotkey.None);
                    replaced = Get(other).Title;
                }
        App.Core.SetHotkey(name, hotkey);
        RaiseChanged();
        return replaced;
    }

    /// <summary>这个按键现在被哪一项使用（不算 name 自己），没有返回 null。</summary>
    public static string? UsedBy(string name, Hotkey hotkey)
    {
        if (!hotkey.IsSet) return null;
        foreach (var other in App.Core.AllHotkeyNames)
            if (other != name && App.Core.GetHotkey(other) == hotkey) return Get(other).Title;
        return null;
    }
}

/// <summary>录制快捷键的对话框：按下组合键即录入，也可以从菜单里选鼠标键；冲突时提示。</summary>
public static class ShortcutDialog
{
    private static readonly int[] MouseKeys = [0x01, 0x02, 0x04, HotkeyService.WheelUp, HotkeyService.WheelDown, 0x06, 0x05];

    public static async Task EditAsync(FrameworkElement owner, string name)
    {
        var info = Shortcuts.Get(name);
        var core = App.Core;
        var pending = core.GetHotkey(name);
        var chips = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Center };
        var warning = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false };
        var box = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), MinHeight = 96,
            Child = chips,
        };
        void Show()
        {
            var chipsPanel = Ui.KeyChips(pending, 16);
            chipsPanel.HorizontalAlignment = HorizontalAlignment.Center;
            chips.Content = chipsPanel;
            chips.VerticalAlignment = VerticalAlignment.Center;
            var used = Shortcuts.UsedBy(name, pending);
            warning.Title = used is null ? "" : $"这个按键已经用于「{used}」";
            warning.Message = used is null ? "" : "保存后，那一项的快捷键会被清空。";
            warning.IsOpen = used is not null;
        }
        Show();

        ContentDialog? dialog = null;
        void Capture()
        {
            core.Hotkeys.BeginCapture(hotkey => owner.DispatcherQueue.TryEnqueue(() =>
            {
                if (dialog is null) return;
                if (hotkey.Vk == 0x1B) { dialog.Hide(); return; } // Esc：取消
                pending = hotkey;
                Show();
                Capture(); // 继续等下一次按键，直到点保存
            }));
        }

        var mouse = new MenuFlyout();
        foreach (var vk in MouseKeys)
        {
            var item = new MenuFlyoutItem { Text = Hotkey.KeyName(vk) };
            item.Click += (_, _) => { pending = new Hotkey(HotkeyMods.None, vk); Show(); };
            mouse.Items.Add(item);
        }
        var content = new StackPanel { Spacing = 12, Width = 420 };
        content.Children.Add(new TextBlock
        {
            Text = "按下要用的按键，可以同时按住 Ctrl、Alt、Shift。鼠标左右键和滚轮没法这样录入，请从下面的菜单里选。按 Esc 取消。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(box);
        content.Children.Add(new DropDownButton { Content = "使用鼠标按键", Flyout = mouse });
        content.Children.Add(warning);
        if (!string.IsNullOrEmpty(info.Description))
            content.Children.Add(new TextBlock
            {
                Text = info.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12,
            });

        dialog = new ContentDialog
        {
            Title = "快捷键：" + info.Title,
            Content = content,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        Capture();
        var result = await Dialogs.CustomAsync(owner, dialog);
        dialog = null;
        core.Hotkeys.CancelCapture();
        if (result == ContentDialogResult.Primary)
        {
            var replaced = Shortcuts.Assign(name, pending);
            core.Log.Write($"快捷键「{info.Title}」已设置为 {pending}。" + (replaced is null ? "" : $"「{replaced}」的快捷键已清空。"));
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Shortcuts.Assign(name, Hotkey.None);
            core.Log.Write($"快捷键「{info.Title}」已清除。");
        }
    }
}

/// <summary>
/// 设置类页面的公共外壳：可滚动、内容最宽 1000、顶部是标题和一句说明，下面按顺序放设置卡。
/// 新写的页面都直接继承它，在构造函数里往 Panel 里加东西。
/// </summary>
public abstract partial class FeaturePage : Page
{
    protected readonly StackPanel Panel = new() { Spacing = 4 };
    protected readonly StackPanel HeaderPanel = new() { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };

    protected FeaturePage(string title, string description)
    {
        HeaderPanel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        if (!string.IsNullOrEmpty(description))
            HeaderPanel.Children.Add(new TextBlock
            {
                Text = description, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        var stack = new StackPanel { Margin = new Thickness(28, 20, 28, 28) };
        stack.Children.Add(HeaderPanel);
        stack.Children.Add(Panel);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 1000 });
        grid.Children.Add(stack);
        Content = new ScrollViewer { Content = grid };
    }

    protected void Add(params UIElement[] items)
    {
        foreach (var item in items) Panel.Children.Add(item);
    }

    /// <summary>页面显示期间订阅 QuellCore.StateChanged（在界面线程上回调），离开页面时退订。</summary>
    protected void OnCoreState(Action refresh)
    {
        Action handler = () => DispatcherQueue.TryEnqueue(() => refresh());
        Loaded += (_, _) => { App.Core.StateChanged -= handler; App.Core.StateChanged += handler; refresh(); };
        Unloaded += (_, _) => App.Core.StateChanged -= handler;
    }

    /// <summary>后台执行（这些操作会调用外部命令或 Sleep），不卡界面。</summary>
    protected static void Run(Action action) => Task.Run(action);

    protected void Section(string title, string? note = null)
    {
        var header = Ui.Header(title);
        if (Panel.Children.Count == 0) header.Margin = new Thickness(1, 8, 0, 6);
        Panel.Children.Add(header);
        if (!string.IsNullOrEmpty(note)) Panel.Children.Add(Ui.Note(note));
    }
}
