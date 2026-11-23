using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>设置：启动与托盘、快捷键引擎、提示、语言与日志、安全、进程守护、外部控制、更新、数据。</summary>
public sealed partial class SettingsPage : FeaturePage
{
    private static QuellCore Core => App.Core;

    public SettingsPage() : base("设置", "")
    {
        // ───────── 启动与托盘 ─────────
        Section("启动与托盘");
        var autoStart = new ToggleSwitch { OnContent = "开", OffContent = "关", IsEnabled = false };
        var tray = Ui.Switch(() => Core.TrayIcon, v => { Core.TrayIcon = v; App.Window.ApplyTraySetting(); });
        Add(Ui.Row("开机自动运行", "登录 Windows 后以管理员权限自动启动，并最小化到托盘。", autoStart, "\uE7E8"));
        Add(Ui.Expander("托盘图标", "右键托盘图标可以直接杀进程、断网、恢复网络。", "\uE8A7", tray,
            Ui.Toggle("关闭窗口时隐藏到托盘", "点关闭按钮不退出程序，快捷键继续有效。从托盘菜单退出。", () => Core.CloseToTray, v => Core.CloseToTray = v),
            Ui.Toggle("启动之后最小化", "启动时不显示主窗口。", () => Core.StartMinimized, v => Core.StartMinimized = v)));
        // 查询计划任务要启动 schtasks，放到后台
        var ready = false;
        Task.Run(() =>
        {
            var enabled = AutoStart.IsEnabled();
            DispatcherQueue.TryEnqueue(() => { autoStart.IsOn = enabled; autoStart.IsEnabled = true; ready = true; });
        });
        autoStart.Toggled += async (_, _) =>
        {
            if (!ready) return;
            var enable = autoStart.IsOn;
            var dataDir = Core.Settings.DataDirectory;
            var ok = await Task.Run(() => enable ? AutoStart.Enable(dataDir) : AutoStart.Disable());
            if (!ok && enable)
            {
                ready = false;
                autoStart.IsOn = false;
                ready = true;
                await Dialogs.MessageAsync(this, "设置失败", "创建计划任务失败。");
                return;
            }
            if (enable)
            {
                // 原版：开启自启时顺带开启托盘图标（否则最小化启动后找不到窗口）
                tray.IsOn = true;
                Core.Log.Write("已设置开机自动运行。移动或删除程序文件后需要重新设置。", 1);
            }
            else Core.Log.Write("已取消开机自动运行。", 1);
        };

        // ───────── 快捷键 ─────────
        Section("快捷键", "各功能的快捷键在它自己的页面里设置，这里可以一次看到全部，并调整快捷键的生效条件。");
        var all = Ui.Expander("全部快捷键", "同一个按键只能给一个功能用，设置时如果冲突会提示。", "\uE765", null);
        foreach (var info in Shortcuts.AllWithPlugins) all.Items.Add(Ui.Shortcut(this, info.Name));
        Add(all);
        Add(Ui.Toggle("仅游戏在前台时生效", "游戏窗口不在最前面时忽略所有快捷键。", () => Core.HotkeysOnlyForeground, v => Core.HotkeysOnlyForeground = v, "\uE7F4"));
        Add(Ui.Toggle("仅游戏运行时生效", "游戏进程不存在时忽略所有快捷键。", () => Core.HotkeysOnlyRunning, v => Core.HotkeysOnlyRunning = v, "\uE7FC"));
        var engine = Ui.Row("", "快捷键失灵时可以重启一次。", null, "\uE7EF");
        void RefreshEngine() => engine.Header = Core.Hotkeys.IsRunning ? "快捷键引擎：运行中" : "快捷键引擎：未运行";
        engine.Content = Ui.ButtonRow(("重启", () =>
        {
            Core.Hotkeys.Restart();
            RefreshEngine();
            Core.Log.Write(Core.Hotkeys.IsRunning ? "热键引擎已重启。" : "热键引擎启动失败。", Core.Hotkeys.IsRunning ? 1 : 3);
        }));
        RefreshEngine();
        var ignore = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var (text, flag) in new[] { ("Alt", HotkeyMods.Alt), ("Ctrl", HotkeyMods.Ctrl), ("Shift", HotkeyMods.Shift) })
        {
            var box = new CheckBox { Content = text, IsChecked = Core.IgnoredModifiers.HasFlag(flag), MinWidth = 0 };
            box.Click += (_, _) => Core.IgnoredModifiers = box.IsChecked == true ? Core.IgnoredModifiers | flag : Core.IgnoredModifiers & ~flag;
            ignore.Children.Add(box);
        }
        Add(Ui.Expander("快捷键引擎", "", "\uE713", null,
            engine,
            Ui.Row("忽略修饰键", "勾选后判断快捷键时不看这个修饰键。例如按住 Shift 奔跑时，不带 Shift 的快捷键默认不会触发；勾选 Shift 后照样触发。", ignore),
            Ui.Choice("监听方式", "键鼠钩子：默认，改键时能把按下的键吃掉。轮询：每 10 毫秒查一次按键状态，钩子被其他软件干扰时使用，不支持滚轮。原始输入：通过 Raw Input 旁听键鼠，不怕钩子超时被系统摘掉，但改键时按下的键会照常传给当前窗口。",
                ["键鼠钩子", "轮询", "原始输入"], () => (int)Core.HotkeyEngine, i => { Core.HotkeyEngine = (HotkeyEngine)i; RefreshEngine(); }, 160),
            Ui.Toggle("自检", "发现钩子不再收到按键时自动重启引擎。", () => Core.HotkeySelfCheckEnabled, v => Core.HotkeySelfCheckEnabled = v)));

        // ───────── 提示 ─────────
        Section("提示");
        var volume = new Slider { Minimum = 0, Maximum = 100, Width = 200, Value = Core.Sound.Volume };
        volume.ValueChanged += (_, e) => Core.Sound.Volume = (int)e.NewValue;
        var test = new Button { Content = "试听" };
        test.Click += (_, _) => Core.Sound.Play(SoundService.Restore);
        var volumePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        volumePanel.Children.Add(volume);
        volumePanel.Children.Add(test);
        Add(Ui.Row("提示音音量", "设为 0 关闭提示音。", volumePanel, "\uE767"));
        Add(Ui.Toggle("推送系统通知", "窗口隐藏时，重要的操作结果通过托盘气泡提示。", () => Core.TrayNotifications, v => Core.TrayNotifications = v, "\uEA8F"));

        // ───────── 语言与日志 ─────────
        Section("语言与日志");
        Add(Ui.Choice("日志和提示的语言", "只影响取自原版语言表的文字（日志和大部分提示框）。界面上的按钮和说明目前只有简体中文。重启后完全生效。",
            ["简体中文", "繁體中文", "English"], () => Core.Language - 1, i => Core.Language = i + 1, 180, "\uF2B7"));
        Add(Ui.Toggle("每天自动存日志", "每条日志同时写到数据目录的 Logs 文件夹，一天一个文件。", () => Core.LogDailySave, v => Core.LogDailySave = v, "\uE81C"));
        Add(Ui.Toggle("禁用记录日志", "不再记录任何日志（「活动」页也不会有内容）。", () => Core.LogDisabled, v => Core.LogDisabled = v, "\uE8D8"));

        // ───────── 安全 ─────────
        Section("安全");
        Add(Ui.Toggle("启用杀掉进程", "关闭后，杀掉进程的按钮和快捷键不会真的结束游戏，用于防止误按。", () => Core.KillEnabled, v => Core.KillEnabled = v, "\uE7E8"));
        Add(Ui.Toggle("禁用进程路径校验", "结束启动器时不再检查它是否真的是 Rockstar 启动器。", () => Core.Game.SkipPathCheck, v => Core.Game.SkipPathCheck = v, "\uE72E"));
        Add(Ui.Expander("进程守护", "另外启动一个很小的守护进程盯着本程序：本程序崩溃或被其他软件结束时，自动重新启动。正常退出不会触发。原版默认开启，移植版默认关闭。", "\uE83D",
            Ui.Switch(() => Core.Guard.Enabled, v => Core.Guard.Enabled = v),
            Ui.Toggle("进程无响应保护", "界面超过 12 秒没有响应时，结束并重新启动本程序。", () => Core.Guard.WatchHang, v => Core.Guard.WatchHang = v),
            Ui.Toggle("被重启后显示警告", "关闭时只在日志里记一条。", () => Core.Guard.ShowWarning, v => Core.Guard.ShowWarning = v),
            Ui.Toggle("被重启后自动恢复状态", "重新创建房间或连接上次的房间。断网、禁卡状态任何时候启动都会接着显示。", () => Core.Guard.AutoRecover, v => Core.Guard.AutoRecover = v)));

        // ───────── 外部控制 ─────────
        Section("外部控制");
        Add(Ui.Expander("UDP 指令", "让其他程序用 UDP 数据报触发操作，例如 kill、fwon、fwoff、single、bancard、timerst、joinbot1。只接受本机发来的指令。", "\uE968",
            Ui.Switch(() => Core.UdpEnabled, v => Core.UdpEnabled = v),
            Ui.Number("端口", "默认 55378。改完后把开关关掉再打开才会生效。", 1, 65534, () => Core.UdpPort, v => Core.UdpPort = (int)v)));

        // ───────── 更新与数据 ─────────
        Section("更新与数据");
        var updateCard = Ui.Row("移植版更新", $"当前版本 {SelfUpdateService.CurrentVersion}。从 GitHub Releases 下载新版本，退出后自动替换程序文件并重新启动。", null, "\uE896");
        var updateButton = new Button { Content = "检查更新", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        updateButton.Click += async (_, _) => await CheckPortUpdateAsync(updateButton, updateCard);
        var releases = new HyperlinkButton { Content = "发布页" };
        releases.Click += (_, _) => Shell.Open(SelfUpdateService.ReleasesPage);
        var updatePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        updatePanel.Children.Add(releases);
        updatePanel.Children.Add(updateButton);
        updateCard.Content = updatePanel;
        Add(updateCard);
        Add(Ui.Expander("原版更新", "查原版有没有比 Build 0216 更新的版本，用来判断移植版是否落后于原版。不会下载任何东西。", "\uE895",
            Ui.ButtonRow(("立即检查", async () =>
            {
                var latest = await Core.Updates.CheckAsync();
                var notice = await Core.Updates.GetNoticeAsync();
                var text = latest is null ? Lang.C(12)
                    : $"原版最新：Build {latest.Build}\n移植所依据：Build {TeamService.BuildVersion}\n\n{latest.Notes}";
                if (notice.Length > 0) text += "\n\n公告：\n" + notice;
                await Dialogs.MessageAsync(this, "原版更新", text);
            })),
            Ui.Toggle("启动时检查", "", () => Core.Updates.CheckOnStartup, v => Core.Updates.CheckOnStartup = v)));
        Add(Ui.Link("数据目录", Core.Settings.DataDirectory + "　配置文件、宏库、令牌和保存的日志都在这里。", "\uE8B7", () =>
        {
            Directory.CreateDirectory(Core.Settings.DataDirectory);
            Shell.Open(Core.Settings.DataDirectory);
        }, "\uE8A7"));

        Unloaded += (_, _) => Core.Hotkeys.CancelCapture();
    }

    /// <summary>检查移植版的新版本；有的话确认后下载、退出并原地替换。</summary>
    private async Task CheckPortUpdateAsync(Button button, SettingsCard card)
    {
        button.IsEnabled = false;
        var original = card.Description;
        try
        {
            card.Description = "正在检查…";
            var (release, error) = await Core.SelfUpdate.CheckAsync();
            card.Description = original;
            if (release is null)
            {
                await Dialogs.MessageAsync(this, "移植版更新", error);
                return;
            }
            if (!SelfUpdateService.IsNewer(release))
            {
                await Dialogs.MessageAsync(this, "移植版更新", $"已是最新版本（{SelfUpdateService.CurrentVersion}，最新发布 {release.Tag}）。");
                return;
            }
            var notes = release.Notes.Length > 600 ? release.Notes[..600] + "…" : release.Notes;
            if (!await Dialogs.ConfirmAsync(this, $"发现新版本 {release.Tag}",
                    (notes.Length > 0 ? notes + "\n\n" : "") + $"将下载 {release.AssetName}"
                    + (release.Size > 0 ? $"（{release.Size / 1048576.0:0.0} MB）" : "") + "，下载完成后本程序会退出，替换文件后自动重新启动。",
                    "下载并更新")) return;
            var progress = new Progress<double>(p => card.Description = $"正在下载 {p:P0}…");
            var failure = await Core.SelfUpdate.DownloadAndStageAsync(release, progress);
            if (failure is not null)
            {
                card.Description = original;
                await Dialogs.MessageAsync(this, "移植版更新", failure);
                return;
            }
            App.Window.Exit();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
