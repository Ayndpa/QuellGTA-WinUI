using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using QuellGTA.Services;
using Windows.ApplicationModel.DataTransfer;

namespace QuellGTA.Views;

/// <summary>换机助手和配置存档（原版「换机助手」页和「用户」菜单）。</summary>
public sealed partial class MigratePage : Page
{
    private static QuellCore Core => App.Core;
    private static ProfileService Profiles => App.Core.Profiles;

    private readonly ComboBox _profileBox = new() { Width = 260 };
    private readonly CheckBox _settings = new() { Content = "线上设置（pc_settings.bin）", IsChecked = true };
    private readonly CheckBox _clean = new() { Content = "导出前先清洗（推荐）", IsChecked = true };
    private readonly CheckBox _keys = new() { Content = "键位（user.xml）", IsChecked = true };
    private readonly CheckBox _graphics = new() { Content = "画质（settings.xml）" };
    private readonly TextBlock _slotList = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
    private readonly NumberBox _slot = new()
    {
        Minimum = 1, Maximum = 99, Value = 1, Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };

    public MigratePage()
    {
        InitializeComponent();
        Loaded += (_, _) => OnShown();
        Build();
    }

    /// <summary>页面每次显示时刷新（页面由主窗口直接创建，不经过 Frame 导航）。</summary>
    private void OnShown() => Refresh();

    private void Refresh()
    {
        var latest = Core.Files.LatestProfile();
        _profileBox.Items.Clear();
        foreach (var profile in Core.Files.GetProfiles())
            _profileBox.Items.Add(new ComboBoxItem { Content = profile == latest ? profile + "（最近使用）" : profile, Tag = profile });
        _profileBox.SelectedIndex = Math.Max(0, Core.Files.GetProfiles().ToList().IndexOf(latest));

        var slots = Profiles.ListSlots();
        _slotList.Text = slots.Count == 0 ? "还没有保存过存档。" : string.Join("\n", slots.Select(s => $"存档 {s.slot,2}　{s.name}"));
    }

    private string SelectedProfile => _profileBox.SelectedItem is ComboBoxItem { Tag: string profile } ? profile : "";

    private static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static async Task<string> PasteAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : "";
        }
        catch (Exception) { return ""; }
    }

    private void Build()
    {
        var panel = Panel.Children;

        // ───────── 换机助手 ─────────
        panel.Add(Ui.Header("换机助手"));
        panel.Add(Ui.Note("在旧电脑上「导出」，把得到的一长段文字发到新电脑（聊天软件、记事本都行），在新电脑上「导入」。新电脑需要先进过一次线上模式，让游戏生成存档目录。导入时会把原文件备份为 .bak。"));
        panel.Add(Ui.Row("存档目录", "游戏按 Rockstar 账号分目录存放设置。一般选「最近使用」的那个。", _profileBox));

        var parts = new StackPanel { Spacing = 2 };
        parts.Children.Add(new TextBlock { Text = "要迁移的内容" });
        parts.Children.Add(_settings);
        parts.Children.Add(new StackPanel { Margin = new Thickness(28, 0, 0, 0), Children = { _clean } });
        parts.Children.Add(_keys);
        parts.Children.Add(_graphics);
        parts.Children.Add(new TextBlock
        {
            Text = "画质文件里记着显卡型号。导入别人的画质时，会保留本机现有画质文件里的显卡信息，避免游戏把它当成换了硬件而重置。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Add(Ui.Card(parts));

        panel.Add(Ui.Buttons("导出与导入", "导出：把代码复制到剪贴板。导入：从剪贴板读取代码并还原（需要先退出游戏）。",
            ("导出到剪贴板", Export), ("从剪贴板导入", Import)));

        // ───────── 配置存档 ─────────
        panel.Add(Ui.Header("本程序的配置存档"));
        panel.Add(Ui.Text("当前配置的名称", "", () => Profiles.CurrentName, v => Profiles.CurrentName = v, "Untitled", 220));
        var slots = new StackPanel { Spacing = 8 };
        slots.Children.Add(new TextBlock { Text = "已有的存档" });
        slots.Children.Add(_slotList);
        panel.Add(Ui.Card(slots));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_slot);
        foreach (var (text, action) in new (string, Action)[] { ("保存到此编号", Save), ("读取", Load), ("删除", Delete) })
        {
            var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Bottom };
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
        }
        panel.Add(Ui.Row("存档编号", "保存：把现在的全部设置存到这个编号。读取：用这个编号的设置覆盖现在的，然后重启程序。", buttons));

        panel.Add(Ui.Buttons("配置代码", "把当前配置导出成一段文字，或者用别人给的配置代码覆盖当前配置（之后重启程序）。",
            ("复制当前配置代码", () => { Copy(Profiles.ExportConfig()); Core.Log.Write("配置代码已复制到剪贴板。", 2); }),
            ("从剪贴板粘贴配置", ImportConfig)));
        panel.Add(Ui.Buttons("其他", "",
            ("打开存档目录", () => Shell.Open(Profiles.SlotDirectory)),
            ("删除当前配置并恢复默认", ResetAll)));
    }

    private async void Export()
    {
        if (SelectedProfile.Length == 0)
        {
            await Dialogs.MessageAsync(this, "无法导出", $"没有找到存档目录：{Core.Files.ProfilesDir}");
            return;
        }
        var parts = (_settings.IsChecked == true ? MigrationParts.Settings : 0)
                    | (_keys.IsChecked == true ? MigrationParts.KeyBindings : 0)
                    | (_graphics.IsChecked == true ? MigrationParts.Graphics : 0);
        var code = Profiles.ExportMigration(SelectedProfile, parts, _clean.IsChecked == true, out var report);
        if (code.Length == 0)
        {
            await Dialogs.MessageAsync(this, "没有可导出的内容", report.Length > 0 ? report : "请至少勾选一项。");
            return;
        }
        Copy(code);
        await Dialogs.MessageAsync(this, "已复制到剪贴板", $"{report}\n\n共 {code.Length} 个字符。把它完整地发到新电脑，在那边点「从剪贴板导入」。");
    }

    private async void Import()
    {
        var code = await PasteAsync();
        if (!code.Contains("<qta"))
        {
            await Dialogs.MessageAsync(this, "无法导入", "剪贴板里没有换机代码。请先复制旧电脑导出的那段文字。");
            return;
        }
        if (!await Dialogs.ConfirmAsync(this, "导入到这台电脑",
                $"将把代码里带的文件还原到存档目录「{SelectedProfile}」，原文件会备份为 .bak。是否继续？"))
            return;
        var result = Profiles.ImportMigration(SelectedProfile, code);
        await Dialogs.MessageAsync(this, result.Ok ? "导入完成" : "导入未完成", result.Message);
    }

    private int Slot => double.IsNaN(_slot.Value) ? 0 : (int)_slot.Value;

    private async void Save()
    {
        var result = Profiles.SaveSlot(Slot);
        Refresh();
        await Dialogs.MessageAsync(this, result.Ok ? "已保存" : "保存失败", result.Message);
    }

    private async void Load()
    {
        if (!await Dialogs.ConfirmAsync(this, "读取存档", $"将用存档 {Slot} 覆盖现在的全部设置，然后重启程序。没有另存的当前设置会丢失。", "读取并重启"))
            return;
        var result = Profiles.LoadSlot(Slot);
        if (!result.Ok)
        {
            await Dialogs.MessageAsync(this, "读取失败", result.Message);
            return;
        }
        App.Window.Restart();
    }

    private async void Delete()
    {
        if (!await Dialogs.ConfirmAsync(this, "删除存档", $"删除存档 {Slot}？无法恢复。", "删除")) return;
        var result = Profiles.DeleteSlot(Slot);
        Refresh();
        if (!result.Ok) await Dialogs.MessageAsync(this, "删除失败", result.Message);
    }

    private async void ImportConfig()
    {
        var code = await PasteAsync();
        if (!await Dialogs.ConfirmAsync(this, "粘贴配置", "将用剪贴板里的配置代码覆盖现在的全部设置，然后重启程序。", "覆盖并重启")) return;
        var result = Profiles.ImportConfig(code);
        if (!result.Ok)
        {
            await Dialogs.MessageAsync(this, "导入失败", result.Message);
            return;
        }
        App.Window.Restart();
    }

    private async void ResetAll()
    {
        if (!await Dialogs.ConfirmAsync(this, "恢复默认", "将删除现在的全部设置（包括热键和统计数据）并重启程序。已保存的存档、宏库和令牌不受影响。", "删除并重启"))
            return;
        var result = Profiles.ResetCurrent();
        if (!result.Ok)
        {
            await Dialogs.MessageAsync(this, "操作失败", result.Message);
            return;
        }
        App.Window.Restart();
    }
}
