using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>插件管理：列出找到的插件，启用 / 停用（重启后生效），打开插件目录。</summary>
public sealed partial class PluginsPage : FeaturePage
{
    private static PluginManager Plugins => App.Core.Plugins;
    private readonly InfoBar _restart;

    public PluginsPage() : base("插件", "原版的外置小工具在这里以插件的形式提供。插件是独立的程序集，放在插件目录里就能被识别；启用或停用后重启本程序生效。")
    {
        _restart = Ui.Info("需要重启", "插件的启用状态改了，重启本程序后生效。");
        _restart.IsOpen = false;
        var restart = new Button { Content = "立即重启" };
        restart.Click += (_, _) => App.Window.Restart();
        _restart.ActionButton = restart;
        Add(_restart);

        var bundled = Plugins.Plugins.Where(p => p.Info.Bundled).ToList();
        var user = Plugins.Plugins.Where(p => !p.Info.Bundled).ToList();

        Section("随程序发布的插件", bundled.Count == 0 ? "没有找到（程序目录下的 Plugins 文件夹是空的）。" : "");
        foreach (var entry in bundled) Add(Card(entry));

        Section("第三方插件", "放在数据目录的 Plugins 文件夹里，每个插件一个子文件夹（包含 plugin.json 和插件程序集）。" +
                             "插件和本程序一样以管理员权限运行，只启用你信任的插件。");
        foreach (var entry in user) Add(Card(entry));
        Add(Ui.Link("打开第三方插件文件夹", Plugins.UserDirectory, "", () =>
        {
            Directory.CreateDirectory(Plugins.UserDirectory);
            Shell.Open(Plugins.UserDirectory);
        }, ""));
        Add(Ui.Link("插件开发说明", "接口定义在 QuellGTA.Plugins.Abstractions 程序集里，随插件附带的示例见仓库的 QuellGTA.Plugins 目录。",
            "", () => Shell.Open("https://github.com/Ayndpa/QuellGTA-WinUI/tree/main/QuellGTA.Plugins"), ""));
    }

    private FrameworkElement Card(PluginManager.Entry entry)
    {
        var info = entry.Info;
        var enabled = Plugins.IsEnabled(info);
        var toggle = new ToggleSwitch { IsOn = enabled, OnContent = "启用", OffContent = "停用" };
        toggle.Toggled += async (_, _) =>
        {
            if (toggle.IsOn == Plugins.IsEnabled(info)) return;
            if (toggle.IsOn && !info.Bundled && !await Dialogs.ConfirmAsync(this, "启用第三方插件",
                    $"「{info.Name}」不是随本程序发布的插件，作者：{(info.Author.Length > 0 ? info.Author : "未注明")}。\n\n" +
                    "插件会以管理员权限运行任意代码。只在你确认来源可信时启用。", "启用"))
            {
                toggle.IsOn = false;
                return;
            }
            Plugins.SetEnabled(info, toggle.IsOn);
            _restart.IsOpen = true;
        };

        var status = entry.Loaded ? "已加载" : entry.Error.Length > 0 ? "加载失败：" + entry.Error : enabled ? "重启后加载" : "未启用";
        var meta = $"{info.Version}" + (info.Author.Length > 0 ? $" · {info.Author}" : "") + $" · {status}";
        var expander = Ui.Expander(info.Name, info.Description, info.Glyph, toggle,
            Ui.Row("状态", meta, null),
            Ui.Link("插件文件夹", info.Directory, null, () => Shell.Open(info.Directory), ""));
        if (entry.Loaded && entry.Instance is not null)
            expander.Items.Insert(0, Ui.Link("打开插件页面", "", null, () => App.Window.NavigateTo("plugin:" + info.Id)));
        return expander;
    }
}
