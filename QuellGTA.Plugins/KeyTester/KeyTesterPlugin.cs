using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QuellGTA.Plugins;
using Windows.System;

namespace QuellGTA.Plugin.KeyTester;

/// <summary>
/// 按键间隔测试（原版测试工具 fptest）：原版是一个铺满屏幕的窗口，每收到一个按键就在标签后面追加键名和
/// 「(距上一个按键的毫秒数)」，连着打出 C L E A R 回车就清空。用来看模拟按键（宏、自动指纹、自动骇入）的节奏对不对。
/// 这里做成一个可以获得焦点的记录区：点一下记录区，再让程序往这个窗口里模拟按键即可。
/// 计时用 Stopwatch，比原版的 GetTickCount（约 16 毫秒精度）准。
/// </summary>
public sealed class KeyTesterPlugin : IQuellPlugin
{
    private IPluginHost _host = null!;

    public void Initialize(IPluginHost host) => _host = host;

    public UIElement? CreatePage()
    {
        var ui = _host.Ui;
        var page = ui.CreatePage("按键间隔测试", "点一下下面的记录区让它获得焦点，然后按键或让程序模拟按键。每个按键后面括号里是距离上一个按键的毫秒数。连续输入 CLEAR 再按回车，或点「清空」，可以清除记录。");

        var log = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 15,
            IsTextSelectionEnabled = true,
        };
        var scroll = new ScrollViewer { Content = log, MinHeight = 320, MaxHeight = 520, Padding = new Thickness(14) };
        var area = new Border
        {
            Child = scroll,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
        // 用一个能获得焦点的控件包住记录区，按键事件才会送到这里
        var focus = new ContentControl
        {
            Content = area, IsTabStop = true, UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0),
        };
        var stats = new TextBlock { Opacity = 0.7, Margin = new Thickness(2, 6, 0, 0) };

        var text = new StringBuilder();
        var intervals = new List<double>();
        var clock = new Stopwatch();
        var typed = "";

        void Clear()
        {
            text.Clear();
            intervals.Clear();
            clock.Reset();
            typed = "";
            log.Text = "（点一下这里，然后按键）";
            stats.Text = "";
        }

        focus.PointerPressed += (_, _) => focus.Focus(FocusState.Pointer);
        focus.KeyDown += (_, e) =>
        {
            e.Handled = true; // 不让 Tab、方向键把焦点带走
            var name = KeyName(e.Key);
            if (clock.IsRunning)
            {
                var ms = clock.Elapsed.TotalMilliseconds;
                intervals.Add(ms);
                text.Append($"{name}({ms:0}) ");
            }
            else text.Append(name + " ");
            clock.Restart();

            // 原版：打出 C L E A R 再回车就清空
            typed = (typed + (e.Key == VirtualKey.Enter ? "\n" : name)).TakeLast(16).Aggregate("", (s, c) => s + c);
            if (typed.EndsWith("CLEAR\n", StringComparison.Ordinal)) { Clear(); return; }

            log.Text = text.ToString();
            scroll.ChangeView(null, scroll.ScrollableHeight + 1000, null, true);
            if (intervals.Count > 0)
            {
                var sorted = intervals.Order().ToArray();
                stats.Text = $"共 {intervals.Count + 1} 个按键　间隔 平均 {intervals.Average():0.0} 毫秒　最短 {sorted[0]:0}　最长 {sorted[^1]:0}　中位 {sorted[sorted.Length / 2]:0}";
            }
        };

        var clear = new Button { Content = "清空" };
        clear.Click += (_, _) => { Clear(); focus.Focus(FocusState.Programmatic); };
        page.Add(ui.Row("记录区", "只记录本窗口收到的按键（和原版一样）。在游戏里测试请改用游戏内的文字输入框。", clear, ""));
        page.Add(focus);
        page.Add(stats);
        Clear();
        return page.Element;
    }

    /// <summary>原版「键代码取键名」的简化版。</summary>
    private static string KeyName(VirtualKey key) => key switch
    {
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)key).ToString(),
        >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => "Num" + (key - VirtualKey.NumberPad0),
        >= VirtualKey.F1 and <= VirtualKey.F24 => "F" + (key - VirtualKey.F1 + 1),
        VirtualKey.Enter => "Enter",
        VirtualKey.Space => "Space",
        VirtualKey.Tab => "Tab",
        VirtualKey.Escape => "Esc",
        VirtualKey.Back => "Backspace",
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => "Shift",
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "Ctrl",
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => "Alt",
        VirtualKey.Left => "←",
        VirtualKey.Right => "→",
        VirtualKey.Up => "↑",
        VirtualKey.Down => "↓",
        _ => key.ToString(),
    };

    public void Dispose() { }
}
