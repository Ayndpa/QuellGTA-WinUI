using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>
/// 用代码拼设置行的小工具。后加的功能很多都是「一个开关 / 一个数字 / 几个按钮」，
/// 用这些方法直接绑到读写设置的委托上，不用为每一项写 XAML 和事件处理。
/// </summary>
public static class Ui
{
    public static TextBlock Header(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["SectionHeaderStyle"],
    };

    public static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 2, 0, 4),
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    public static SettingRow Row(string title, string description, object action) =>
        new() { Title = title, Description = description, Action = action };

    public static SettingRow Toggle(string title, string description, Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = get() };
        toggle.Toggled += (_, _) => set(toggle.IsOn);
        return Row(title, description, toggle);
    }

    public static SettingRow Number(string title, string description, double min, double max,
        Func<double> get, Action<double> set, double step = 1)
    {
        var box = new NumberBox
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, Width = 140,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = get(),
        };
        box.ValueChanged += (_, _) => { if (!double.IsNaN(box.Value)) set(box.Value); };
        return Row(title, description, box);
    }

    public static SettingRow Choice(string title, string description, string[] options, Func<int> get, Action<int> set, double width = 220)
    {
        var combo = new ComboBox { Width = width };
        foreach (var option in options) combo.Items.Add(option);
        combo.SelectedIndex = Math.Clamp(get(), 0, options.Length - 1);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) set(combo.SelectedIndex); };
        return Row(title, description, combo);
    }

    /// <summary>文本框：失去焦点或按回车时保存。</summary>
    public static SettingRow Text(string title, string description, Func<string> get, Action<string> set,
        string placeholder = "", double width = 280)
    {
        var box = new TextBox { Text = get(), PlaceholderText = placeholder, Width = width, IsSpellCheckEnabled = false };
        void Commit()
        {
            set(box.Text);
            box.Text = get();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Commit(); };
        return Row(title, description, box);
    }

    public static SettingRow Buttons(string title, string description, params (string text, Action click)[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (text, click) in buttons)
        {
            var button = new Button { Content = text };
            button.Click += (_, _) => click();
            panel.Children.Add(button);
        }
        return Row(title, description, panel);
    }

    /// <summary>选一个按键（不带组合键）：点按钮后按下要用的键，Esc 取消。</summary>
    public static SettingRow Key(FrameworkElement owner, string title, string description, Func<int> get, Action<int> set)
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
        return Row(title, description, button);
    }

    public static Border Card(UIElement content) => new()
    {
        Style = (Style)Application.Current.Resources["CardStyle"],
        Margin = new Thickness(0, 0, 0, 4),
        Child = content,
    };
}
