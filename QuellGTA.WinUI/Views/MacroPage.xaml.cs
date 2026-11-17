using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuellGTA.Services;
using Windows.ApplicationModel.DataTransfer;

namespace QuellGTA.Views;

public sealed partial class MacroPage : Page
{
    private static QuellCore Core => App.Core;
    private readonly bool _ready;
    private readonly Action _stateHandler;

    public MacroPage()
    {
        InitializeComponent();
        BuildExtras();

        foreach (var (name, file) in MacroService.Presets)
        {
            var item = new MenuFlyoutItem { Text = name, Tag = file };
            item.Click += Preset_Click;
            PresetMenu.Items.Add(item);
        }

        CpsBox.Value = Core.ClicksPerSecond;
        ClickModeCombo.SelectedIndex = Core.ClickWhileHeld ? 1 : 0;
        AfkCombo.SelectedIndex = (int)Core.AfkMode;
        RefreshMacros();
        RefreshState();
        _ready = true;

        _stateHandler = () => DispatcherQueue.TryEnqueue(RefreshState);
        Core.StateChanged += _stateHandler;
        Unloaded += (_, _) =>
        {
            Core.StateChanged -= _stateHandler;
            Core.Hotkeys.CancelCapture();
        };
    }

    // ───────── 宏库 ─────────

    private void RefreshMacros()
    {
        MacroPanel.Children.Clear();
        foreach (var macro in Core.Macros.Macros)
        {
            var run = new Button { Tag = macro.Id, MinWidth = 76 };
            run.Click += (_, _) => Task.Run(() => Core.ToggleMacro(macro.Id, bringToFront: true));
            var edit = new Button { Content = "编辑" };
            edit.Click += async (_, _) => await EditAsync(macro, isNew: false);
            var enabled = new ToggleSwitch { IsOn = macro.Enabled, OnContent = "", OffContent = "", MinWidth = 0 };
            enabled.Toggled += (_, _) =>
            {
                macro.Enabled = enabled.IsOn;
                Core.Macros.Save();
                Core.RegisterMacroHotkeys();
            };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(run);
            actions.Children.Add(edit);
            actions.Children.Add(enabled);

            var steps = MacroService.Parse(macro.Script);
            var hotkey = Hotkey.Parse(macro.Hotkey);
            MacroPanel.Children.Add(new SettingRow
            {
                Title = macro.Name,
                Description = $"{steps.Count(s => s.Op != "Delay")} 个操作 · 执行 {macro.Repeat} 次 · 热键：{hotkey}",
                Action = actions,
                Tag = macro.Id,
            });
        }
        EmptyText.Visibility = Core.Macros.Macros.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshState();
    }

    private void RefreshState()
    {
        foreach (var row in MacroPanel.Children.OfType<SettingRow>())
        {
            if (row.Action is StackPanel { Children: [Button run, ..] } && row.Tag is string id)
                run.Content = Core.Macros.IsRunning(id) ? "停止" : "试运行";
        }
        AfkButton.Content = Core.AfkActive ? "停止挂机" : "开始挂机";
        AfkRow.Title = Core.AfkActive ? "正在前台挂机…" : "开始挂机";
    }

    private async void New_Click(object sender, RoutedEventArgs e) =>
        await EditAsync(new MacroDefinition(), isNew: true);

    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        var item = (MenuFlyoutItem)sender;
        await EditAsync(new MacroDefinition { Name = item.Text, Script = MacroService.ReadPreset((string)item.Tag) }, isNew: true);
    }

    /// <summary>宏编辑对话框：左边写脚本，右边实时显示每一步的中文说明。</summary>
    private async Task EditAsync(MacroDefinition macro, bool isNew)
    {
        var hotkey = Hotkey.Parse(macro.Hotkey);
        var nameBox = new TextBox { Header = "名称", Text = macro.Name, MaxLength = 40 };
        var repeatBox = new NumberBox
        {
            Header = "执行次数", Value = macro.Repeat, Minimum = 1, Maximum = 99999,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var hotkeyButton = new Button { Content = hotkey.ToString(), MinWidth = 140 };
        var clearHotkey = new Button { Content = "停用" };
        var scriptBox = new TextBox
        {
            Text = MacroService.Normalize(macro.Script),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            IsSpellCheckEnabled = false,
            Height = 300,
            PlaceholderText = "每行一个操作，例如：\nKeyDn|77\nDelay|30\nKeyUp|77",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(scriptBox, ScrollBarVisibility.Auto);
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true };
        var status = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

        void UpdatePreview()
        {
            var invalid = new List<int>();
            var steps = MacroService.Parse(scriptBox.Text, invalid);
            preview.Text = steps.Count == 0 ? "（空）" : string.Join("\n", steps.Select((s, i) => $"{i + 1,3}. {s.Describe()}"));
            var total = steps.Where(s => s.Op == "Delay").Sum(s => (long)s.Value);
            status.Text = invalid.Count > 0
                ? $"第 {string.Join("、", invalid.Take(8))} 行无法识别，执行时会被跳过。"
                : $"共 {steps.Count} 步，单次约 {total} 毫秒。";
        }
        scriptBox.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        void Append(string lines)
        {
            var text = scriptBox.Text.TrimEnd('\r', '\n');
            scriptBox.Text = text.Length == 0 ? lines : text + "\r\n" + lines;
        }

        hotkeyButton.Click += (_, _) =>
        {
            hotkeyButton.Content = "请按下按键…";
            Core.Hotkeys.BeginCapture(captured => DispatcherQueue.TryEnqueue(() =>
            {
                if (captured.Vk != 0x1B) hotkey = captured;
                hotkeyButton.Content = hotkey.ToString();
            }));
        };
        clearHotkey.Click += (_, _) =>
        {
            Core.Hotkeys.CancelCapture();
            hotkey = Hotkey.None;
            hotkeyButton.Content = hotkey.ToString();
        };

        var insertKey = new Button { Content = "插入按键" };
        insertKey.Click += (_, _) =>
        {
            insertKey.Content = "请按下按键…";
            Core.Hotkeys.BeginCapture(captured => DispatcherQueue.TryEnqueue(() =>
            {
                insertKey.Content = "插入按键";
                if (captured.Vk == 0x1B) return;
                // 鼠标中键 / 侧键不是键盘按键，这里只接受键盘
                if (captured.Vk is 0x04 or 0x05 or 0x06) return;
                Append($"KeyDn|{captured.Vk}\r\nDelay|30\r\nKeyUp|{captured.Vk}\r\nDelay|50");
            }));
        };
        var insertDelay = new Button { Content = "插入等待" };
        insertDelay.Click += (_, _) => Append("Delay|100");
        var insertClick = new Button { Content = "插入左键点击" };
        insertClick.Click += (_, _) => Append("MbDn|1\r\nDelay|30\r\nMbUp|1\r\nDelay|50");
        var paste = new Button { Content = "从剪贴板导入" };
        paste.Click += async (_, _) =>
        {
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text))
                    scriptBox.Text = MacroService.Normalize(await content.GetTextAsync());
            }
            catch (Exception) { }
        };
        var copy = new Button { Content = "复制序列" };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(MacroService.Export(scriptBox.Text));
            Clipboard.SetContent(package);
        };

        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hotkeyPanel = new StackPanel { Spacing = 4 };
        hotkeyPanel.Children.Add(new TextBlock { Text = "热键" });
        var hotkeyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        hotkeyRow.Children.Add(hotkeyButton);
        hotkeyRow.Children.Add(clearHotkey);
        hotkeyPanel.Children.Add(hotkeyRow);
        top.Children.Add(nameBox);
        Grid.SetColumn(repeatBox, 1);
        top.Children.Add(repeatBox);
        Grid.SetColumn(hotkeyPanel, 2);
        top.Children.Add(hotkeyPanel);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in new[] { insertKey, insertDelay, insertClick, paste, copy }) tools.Children.Add(button);

        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(scriptBox);
        var previewScroll = new ScrollViewer { Content = preview, Height = 300, Padding = new Thickness(4, 0, 12, 0) };
        Grid.SetColumn(previewScroll, 1);
        body.Children.Add(previewScroll);

        var content = new StackPanel { Spacing = 12, Width = 640 };
        content.Children.Add(top);
        content.Children.Add(tools);
        content.Children.Add(body);
        content.Children.Add(status);
        content.Children.Add(new TextBlock
        {
            Text = "操作：KeyDn 按住键、KeyUp 松开键（后面是虚拟键码）；Delay 等待毫秒；MbDn / MbUp 按住 / 松开鼠标（1 左键、2 右键、3 中键）。"
                   + "预设序列的延迟是按高帧率调的，帧率低时请适当加大 Delay。",
            FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = isNew ? "新建宏" : "编辑宏",
            Content = content,
            PrimaryButtonText = "保存",
            SecondaryButtonText = isNew ? "" : "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;

        var result = await Dialogs.CustomAsync(this, dialog);
        Core.Hotkeys.CancelCapture();

        if (result == ContentDialogResult.Secondary)
        {
            Core.Macros.Stop(macro.Id);
            Core.Macros.Macros.Remove(macro);
        }
        else if (result == ContentDialogResult.Primary)
        {
            macro.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "未命名" : nameBox.Text.Trim();
            macro.Repeat = double.IsNaN(repeatBox.Value) ? 1 : Math.Clamp((int)repeatBox.Value, 1, 99999);
            macro.Script = MacroService.Normalize(scriptBox.Text);
            macro.Hotkey = hotkey.IsSet ? hotkey.Serialize() : "";
            if (hotkey.IsSet)
            {
                // 同一个按键只留给一个宏
                foreach (var other in Core.Macros.Macros)
                    if (other != macro && other.Hotkey == macro.Hotkey) other.Hotkey = "";
            }
            if (isNew) Core.Macros.Macros.Add(macro);
        }
        else
        {
            return;
        }
        Core.Macros.Save();
        Core.RegisterMacroHotkeys();
        RefreshMacros();
    }

    // ───────── 连击 / 挂机 ─────────

    private void Cps_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !double.IsNaN(sender.Value)) Core.ClicksPerSecond = (int)sender.Value;
    }

    private void ClickMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Core.ClickWhileHeld = ClickModeCombo.SelectedIndex == 1;
    }

    private void Afk_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && AfkCombo.SelectedIndex >= 0) Core.AfkMode = (AfkMode)AfkCombo.SelectedIndex;
    }

    private void Afk_Click(object sender, RoutedEventArgs e) => Task.Run(Core.ToggleAfk);
}
