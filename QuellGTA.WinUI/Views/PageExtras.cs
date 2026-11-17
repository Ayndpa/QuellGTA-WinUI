using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuellGTA.Services;
using Windows.ApplicationModel.DataTransfer;

namespace QuellGTA.Views;

// 各页面后加的功能区。每个页面在 XAML 末尾留了一个 ExtraPanel，这里用 Ui 里的方法往里填。

public sealed partial class MacroPage
{
    private void BuildExtras()
    {
        var core = App.Core;
        var panel = ExtraPanel.Children;

        panel.Add(Ui.Toggle("挂机时伪后台", "开始挂机后把游戏窗口压到所有窗口的最底下。游戏仍是活动窗口，只是被别的窗口盖住；点了别的窗口挂机就会结束。",
            () => core.AfkPseudoBackground, v => core.AfkPseudoBackground = v));
        panel.Add(Ui.Buttons("仅执行伪后台操作", "不挂机，只把游戏窗口压到最底下。",
            ("执行", () => { if (!core.SendGameWindowToBack()) core.Log.Write("游戏未在运行。"); })));

        panel.Add(Ui.Header("自动挂机防踢"));
        panel.Add(Ui.Toggle("长时间无操作时自动按键", "游戏运行期间，超过设定时间没有任何键鼠操作，就替你按一下键，避免因挂机被踢出战局。",
            () => core.AntiAfkEnabled, v => core.AntiAfkEnabled = v));
        panel.Add(Ui.Number("超时（秒）", "多久没有操作算挂机，默认 120。", 10, 3600, () => core.AntiAfkTimeout, v => core.AntiAfkTimeout = (int)v));
        panel.Add(Ui.Key(this, "超时后按下的按键", "默认 Z。选一个在游戏里按了没有副作用的键。", () => core.AntiAfkKey, v => core.AntiAfkKey = v));
        panel.Add(Ui.Toggle("游戏在后台时先切到前台", "关闭后，游戏不在前台时不按键，只在日志里提示。",
            () => core.AntiAfkBringToFront, v => core.AntiAfkBringToFront = v));

        panel.Add(Ui.Header("自动骇入"));
        var hack = core.AutoHack;
        panel.Add(Ui.Note("进入骇入小游戏后按「自动骇入」热键：程序截取游戏画面，认出是哪一种小游戏，然后替你按键完成。只在游戏位于前台时按键。"
            + "目前支持：" + string.Join("、", hack.SupportedGames) + "。指纹类的小游戏请用上面的「自动指纹」。"));
        panel.Add(Ui.Toggle("启用自动骇入", "", () => hack.Enabled, v => hack.Enabled = v));
        panel.Add(Ui.Number("按键时长（毫秒）", "每次点按按住多久，默认 20。帧率低、游戏漏按键时调大。", 1, 250, () => hack.TapHoldMs, v => hack.TapHoldMs = (int)v, 5));
        panel.Add(Ui.Number("按键间隔（毫秒）", "两次点按之间的间隔，默认 20。", 1, 250, () => hack.TapGapMs, v => hack.TapGapMs = (int)v, 5));
        panel.Add(Ui.Buttons("手动开始 / 停止", "和热键的作用相同。开始后会把游戏当成前台窗口来等待，最多等 60 秒。", ("开始 / 停止", hack.Toggle)));

        panel.Add(Ui.Header("虚拟手柄"));
        panel.Add(Ui.Note("下面两个功能通过一只虚拟手柄来推左摇杆，需要先安装 ViGEmBus 驱动。虚拟手柄插着的时候，游戏会把按键提示换成手柄图标；真手柄失灵时用最下面的「移除虚拟手柄」。"));
        // 没装驱动时开不了：开关弹回去，并给出下载页
        SettingRow PadToggle(string title, string description, Func<bool> get, Action<bool> set)
        {
            var toggle = new ToggleSwitch { IsOn = get() };
            toggle.Toggled += async (_, _) =>
            {
                if (toggle.IsOn == get()) return;
                if (toggle.IsOn && !await Task.Run(GamepadService.DriverInstalled))
                {
                    toggle.IsOn = false;
                    if (await Dialogs.ConfirmAsync(this, "没有检测到 ViGEmBus 驱动", "虚拟手柄依赖 ViGEmBus 驱动。安装后不用重启本程序，回来再打开这个开关即可。", "打开下载页"))
                        Shell.Open(GamepadService.DriverUrl);
                    return;
                }
                set(toggle.IsOn);
            };
            return Ui.Row(title, description, toggle);
        }
        panel.Add(PadToggle("模拟手柄挂机（真后台）", "上面的「长时间无操作时自动按键」超时后，不按键盘，改为把左摇杆推一下再松开。游戏在后台也有效，不会打断你在别的窗口里做的事。",
            () => core.GamepadAfk, v => core.GamepadAfk = v));
        panel.Add(Ui.Choice("推动方向", "原版默认向下。", GamepadService.DirectionArrows, () => core.GamepadAfkDirection, i => core.GamepadAfkDirection = i, 120));
        panel.Add(Ui.Number("推动时长（毫秒）", "默认 1000。", 10, 60000, () => core.GamepadAfkDuration, v => core.GamepadAfkDuration = (int)v, 100));
        panel.Add(Ui.Number("推动行程", "0–255，默认 255（推到底）。", 0, 255, () => core.GamepadAfkStroke, v => core.GamepadAfkStroke = (int)v));
        panel.Add(PadToggle("手柄偷速", "按「热键」页里的「手柄偷速」键时，把左摇杆往前推到约 88%，人物以偷速状态奔跑。只在游戏位于前台时生效。",
            () => core.GamepadRun, v => core.GamepadRun = v));
        panel.Add(Ui.Choice("触发方式", "「按下时切换」会在偷速期间替你按住 Shift（触发键本身是 Shift 时除外）。", ["按住按键时启动", "按下时切换开关"],
            () => core.GamepadRunToggle ? 1 : 0, i => core.GamepadRunToggle = i == 1));
        panel.Add(Ui.Number("偷速行程", "0–255，默认 224（约 88%），适用于 PS4 虚拟手柄。", 0, 255, () => core.GamepadRunStroke, v => core.GamepadRunStroke = (int)v));
        panel.Add(Ui.Toggle("启停时声音提醒", "", () => core.GamepadRunSound, v => core.GamepadRunSound = v));
        panel.Add(Ui.Choice("手柄类型", "默认 PS4。改完后下次创建虚拟手柄时生效。", ["PlayStation 4", "Xbox 360"],
            () => core.Gamepad.UseXbox ? 1 : 0, i => core.Gamepad.UseXbox = i == 1));
        panel.Add(Ui.Toggle("按需创建", "只在要推摇杆的那一刻插上虚拟手柄，推完就拔掉，平时游戏仍显示键鼠提示。偷速需要随按随有，建议关闭。",
            () => core.GamepadOnDemand, v => core.GamepadOnDemand = v));
        panel.Add(Ui.Buttons("移除虚拟手柄", "关闭上面两个功能并立即拔掉虚拟手柄。之后需要重启游戏，它才会重新认出真手柄。", ("移除", async () =>
        {
            if (!await Dialogs.ConfirmAsync(this, "移除虚拟手柄", Lang.C(531))) return;
            core.RemoveGamepad();
            Frame.Navigate(GetType()); // 重新加载本页，让开关显示最新状态
        })));

        panel.Add(Ui.Header("聊天输入"));
        panel.Add(Ui.Toggle("粘贴文字", "游戏聊天框不支持 Ctrl+V。开启后用「粘贴文字」热键把剪贴板内容打进去（最多 256 个字符）。增强版只能打出键盘上有的字符，传承版可以输入中文。",
            () => core.PasteEnabled, v => core.PasteEnabled = v));
        panel.Add(Ui.Toggle("英语输入锁定", "定时让游戏切回英文输入法，避免中文输入法吃掉游戏按键。要打中文时按「解除英语锁定」热键，锁定会暂停到你按回车或 Esc。",
            () => core.EnglishLock, v => core.EnglishLock = v));
        var rates = new[] { 1, 2, 5, 10, 20 };
        panel.Add(Ui.Choice("锁定频率", "每秒切换几次。越高越不容易被输入法抢回去。", ["每秒 1 次", "每秒 2 次", "每秒 5 次", "每秒 10 次", "每秒 20 次"],
            () => Array.IndexOf(rates, core.EnglishLockRate), i => core.EnglishLockRate = rates[i]));

        panel.Add(Ui.Header("自动指纹"));
        var fingerprint = core.Fingerprint;
        panel.Add(Ui.Toggle("启用自动指纹", "在指纹破解画面按「自动指纹」热键：截取画面，认出该选哪几项，然后替你按方向键。只支持 16:9 的画面。",
            () => fingerprint.Enabled, v => fingerprint.Enabled = v));
        panel.Add(Ui.Choice("指纹类型", "", ["名钻指纹（赌场豪劫）", "佩岛指纹（佩里科岛）"], () => (int)fingerprint.Kind, i => fingerprint.Kind = (FingerprintKind)i));
        panel.Add(Ui.Toggle("游戏全屏模式", "开启：截取整个主屏幕。关闭：只截取游戏窗口（窗口化时使用）。",
            () => fingerprint.FullScreen, v => fingerprint.FullScreen = v));
        panel.Add(Ui.Number("按键延迟（毫秒）", "每次按键按住和间隔的时间，默认 60。帧率低时调大。", 10, 500, () => fingerprint.KeyDelay, v => fingerprint.KeyDelay = (int)v, 10));
        panel.Add(Ui.Number("图案相似度（%）", "默认 85。认不出来时可以调低一些。", 50, 100, () => fingerprint.Similarity, v => fingerprint.Similarity = (int)v, 5));
    }
}

public sealed partial class OverlayPage
{
    private void BuildExtras()
    {
        var overlay = App.Core.Overlay;
        var panel = ExtraPanel.Children;

        panel.Add(Ui.Header("准星字符"));
        panel.Add(Ui.Text("用字符当准星", "把上面的「形状」选成「字符」后生效，最多 4 个字符。",
            () => overlay.CrosshairText, v => overlay.CrosshairText = v, "+", 120));

        BuildRtss(panel);

        panel.Add(Ui.Header("计时扩展"));
        panel.Add(Ui.Buttons("暂停与恢复", "暂停后读数停住，恢复后接着走（结束计时之后也可以恢复）。",
            ("暂停计时", overlay.PauseTimer), ("恢复计时", overlay.ResumeTimer)));
        panel.Add(Ui.Buttons("记忆时间", "把当前读数记下来，之后可以读回来（重启程序后仍然有效）。",
            ("记忆当前时间", overlay.SaveTimerMemory), ("读取记忆时间", () => overlay.SetTimer(overlay.TimerMemory))));

        var manual = new TextBox { Width = 160, PlaceholderText = "00:12:34.50", IsSpellCheckEnabled = false };
        var apply = new Button { Content = "应用" };
        apply.Click += async (_, _) =>
        {
            if (OverlayService.ParseTime(manual.Text) is { } ms) overlay.SetTimer(ms);
            else await Dialogs.MessageAsync(this, "格式不对", "请按「时:分:秒.百分秒」填写，例如 00:12:34.50；也可以只写「分:秒」。");
        };
        var manualPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        manualPanel.Children.Add(manual);
        manualPanel.Children.Add(apply);
        panel.Add(Ui.Row("手动调整时间", "把计时器的读数直接改成指定的时间。", manualPanel));

        panel.Add(Ui.Text("个性签名", "显示在计时器下面的一行小字，留空不显示。", () => overlay.TimerSignature, v => overlay.TimerSignature = v, "", 240));
        var fonts = new[] { "Consolas", "Segoe UI", "Microsoft YaHei UI", "SimHei", "SimSun", "Arial", "Impact" };
        panel.Add(Ui.Choice("显示字体", "默认 Consolas（等宽，数字跳动时宽度不变）。",
            ["Consolas", "Segoe UI", "微软雅黑", "黑体", "宋体", "Arial", "Impact"],
            () => Math.Max(0, Array.IndexOf(fonts, overlay.TimerFont)), i => overlay.TimerFont = fonts[i]));
    }
}

public sealed partial class OverlayPage
{
    /// <summary>RTSS 锁帧与 OSD。</summary>
    private void BuildRtss(UIElementCollection panel)
    {
        var core = App.Core;
        panel.Add(Ui.Header("RTSS 锁帧"));
        panel.Add(Ui.Note("通过 RivaTuner Statistics Server（RTSS，需自行安装）限制游戏帧数。常见用法：过场动画或特定任务里临时锁到低帧数，用热键在几个档位之间切换。"));

        var status = Ui.Row("RTSS 状态", "", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 });
        void Describe()
        {
            var installed = RtssService.ExecutablePath is not null;
            var limit = core.RtssCurrentLimit < 0 ? "" : "；当前上限：" + QuellCore.FrameLimitText(core.RtssCurrentLimit)
                + (core.RtssLimiterActive == false ? "（锁帧总开关已关）" : "");
            status.Description = (RtssService.IsRunning ? "正在运行" : installed ? "已安装，未运行" : "没有检测到 RTSS") + limit;
        }
        var buttons = (StackPanel)status.Action!;
        void Add(string text, Action click)
        {
            var button = new Button { Content = text };
            button.Click += async (_, _) => { click(); await Task.Delay(600); Describe(); };
            buttons.Children.Add(button);
        }
        Add("刷新", () => Task.Run(core.RefreshRtssState));
        Add("启动", () => { if (!RtssService.Start() && RtssService.ExecutablePath is null) Shell.Open(RtssService.DownloadUrl); });
        Add("关闭", () => RtssService.Close(core.RtssForceClose));
        Add("下载", () => Shell.Open(RtssService.DownloadUrl));
        panel.Add(status);
        Describe();

        panel.Add(Ui.Toggle("开启 RTSS 锁帧控制", "开启后才能用下面的按钮和「RTSS 锁帧」热键。", () => core.RtssLimiter, v => core.RtssLimiter = v));
        panel.Add(Ui.Toggle("仅游戏进程", "只改 RTSS 里游戏进程自己的配置。关闭后改的是全局配置；注意 RTSS 里已有的游戏配置会比全局优先生效，需要你到 RTSS 里手动删掉。",
            () => core.RtssOnlyGame, v => core.RtssOnlyGame = v));

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            var box = new NumberBox
            {
                Minimum = -1, Maximum = 999, Width = 96, Value = core.RtssPresets[index],
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            };
            box.ValueChanged += (_, _) =>
            {
                if (double.IsNaN(box.Value)) return;
                var values = core.RtssPresets;
                values[index] = (int)box.Value;
                core.RtssPresets = values;
            };
            var apply = new Button { Content = "应用" };
            apply.Click += async (_, _) =>
            {
                var value = core.RtssPresets[index];
                if (value >= 0) await Task.Run(() => core.ApplyFrameLimit(value));
                Describe();
            };
            presets.Children.Add(box);
            presets.Children.Add(apply);
        }
        panel.Add(Ui.Row("三个帧数预设", "0 = 不限制，-1 = 停用这一档（切换时跳过）。热键按「一 → 二 → 三」循环。", presets));
        panel.Add(Ui.Buttons("锁帧总开关", "只开关 RTSS 的帧数限制，不改上限的数值。",
            ("下一个预设", () => { core.CycleFrameLimit(); Describe(); }),
            ("停用锁帧", () => { core.SetRtssLimiterActive(false); Describe(); }),
            ("启用锁帧", () => { core.SetRtssLimiterActive(true); Describe(); })));
        panel.Add(Ui.Toggle("按需自动启动 RTSS", "应用帧数时如果 RTSS 没在运行，自动把它启动起来。", () => core.RtssAutoStart, v => core.RtssAutoStart = v));
        panel.Add(Ui.Toggle("退出本程序时关闭 RTSS", "", () => core.RtssAutoClose, v => core.RtssAutoClose = v));
        panel.Add(Ui.Toggle("关闭 RTSS 时强制结束进程", "默认是请 RTSS 自己退出。它不响应时再打开这一项。", () => core.RtssForceClose, v => core.RtssForceClose = v));

        panel.Add(Ui.Header("用 RTSS 显示"));
        panel.Add(Ui.Note(Lang.C(459)));
        panel.Add(Ui.Toggle("用 RTSS 显示计时", "独占全屏时内置的浮窗显示不出来，RTSS 的 OSD 可以。只在游戏运行时显示；签名里的中文不会显示。",
            () => core.RtssOsdTimer, v => core.RtssOsdTimer = v));
        panel.Add(Ui.Number("计时位置 X", "RTSS 的坐标单位，从画面左上角算起。", 0, 4000, () => core.RtssTimerX, v => core.RtssTimerX = (int)v, 10));
        panel.Add(Ui.Number("计时位置 Y", "", 0, 4000, () => core.RtssTimerY, v => core.RtssTimerY = (int)v, 10));
        panel.Add(Ui.Toggle("显示锁帧帧数", "在计时上面的状态行里显示当前锁的帧数。", () => core.RtssShowLimit, v => core.RtssShowLimit = v));
        panel.Add(Ui.Toggle("用 RTSS 显示准星", "用一个字符当准星，位置和颜色沿用上面准星的设置。", () => core.RtssOsdCrosshair, v => core.RtssOsdCrosshair = v));
        panel.Add(Ui.Text("RTSS 准星字符", "原版默认 *，只能用英文符号。", () => core.RtssCrosshairText, v => core.RtssCrosshairText = v, "*", 120));
    }
}

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

public sealed partial class NetworkPage
{
    private void BuildExtras()
    {
        var core = App.Core;
        var panel = ExtraPanel.Children;

        panel.Add(Ui.Header("断网方式"));
        panel.Add(Ui.Toggle("WFP 模式", "阻止联网时不建 Windows 防火墙规则，而是直接向 Windows 筛选平台（WFP）添加过滤器。防火墙被关掉也能用，生效更快；规则在重启系统后自动消失。「恢复网络」会把两种方式的规则都清掉。",
            () => core.WfpMode, v => core.WfpMode = v));

        panel.Add(Ui.Toggle("Dev 硬件模式", "「禁用网卡」时直接在设备层面禁用网卡，不经过 PowerShell，比默认方式快一两秒。开启后上面的「仅解绑 TCP/IP」不起作用。原版通过内嵌的 devcon.exe 实现。",
            () => core.Nic.HardwareMode, v => core.Nic.HardwareMode = v));

        panel.Add(Ui.Header("进程断网"));
        panel.Add(Ui.Text("手动指定被断网的程序", "「仅断开游戏进程」开启时，默认自动探测游戏进程。使用加速器时，游戏的流量其实是加速器发出去的，这时要填加速器程序的完整路径。留空恢复自动探测。",
            () => core.BlockTargetPath, v => core.BlockTargetPath = v, @"例如 C:\Program Files\加速器\accelerator.exe", 360));

        panel.Add(Ui.Header("云存档提示"));
        panel.Add(Ui.Note("通过游戏设置文件里的「上次线上云存档成功时间」判断游戏是否刚存过档。断网保进度时，如果刚好在存档完成之后才断，进度其实已经保存到云端了。"));
        panel.Add(Ui.Toggle("保存成功时记录日志", "", () => core.CloudSaveLog, v => core.CloudSaveLog = v));
        panel.Add(Ui.Toggle("保存成功时播放声音", "需要同时开启上一项。", () => core.CloudSaveSound, v => core.CloudSaveSound = v));
        panel.Add(Ui.Toggle("操作时提示保存成功间隔", "杀进程、断网时，如果 10 秒内刚存过档，在日志里提示距离上次存档过了几秒。",
            () => core.CloudSaveInterval, v => core.CloudSaveInterval = v));
        panel.Add(Ui.Toggle("罪神时评估进度保留情况", "联机状态下杀进程时，如果 10 秒内刚存过档，弹出警告：罪神进度可能已经被云端重置。",
            () => core.CloudSaveHeistWarning, v => core.CloudSaveHeistWarning = v));
        panel.Add(Ui.Buttons("查询上次云存档", "", ("查询", async () =>
        {
            var last = core.LastCloudSave;
            await Dialogs.MessageAsync(this, "上次云存档", last is null
                ? "本次运行期间还没有观察到云存档。需要先开启上面任意一项，并且游戏在线上模式里存过一次档。"
                : $"{last.Value.ToLocalTime():HH:mm:ss}（{(int)(DateTimeOffset.UtcNow - last.Value).TotalSeconds} 秒前）");
        })));
    }
}

public sealed partial class HotkeysPage
{
    private void BuildExtras()
    {
        var core = App.Core;
        var panel = ExtraPanel.Children;

        var ignore = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (text, flag) in new[] { ("忽略 Alt", HotkeyMods.Alt), ("忽略 Ctrl", HotkeyMods.Ctrl), ("忽略 Shift", HotkeyMods.Shift) })
        {
            var box = new CheckBox { Content = text, IsChecked = core.IgnoredModifiers.HasFlag(flag), MinWidth = 0 };
            box.Click += (_, _) => core.IgnoredModifiers = box.IsChecked == true ? core.IgnoredModifiers | flag : core.IgnoredModifiers & ~flag;
            ignore.Children.Add(box);
        }
        panel.Add(Ui.Row("组合键忽略", "勾选后判断热键时不看这个修饰键。例如按住 Shift 奔跑时，不带 Shift 的热键默认不会触发；勾选「忽略 Shift」后照样触发。", ignore));
        panel.Add(Ui.Toggle("轮询模式", "不用键鼠钩子，改为每 10 毫秒查询一次按键状态。钩子被其他软件干扰、热键时灵时不灵时使用。不支持鼠标滚轮。",
            () => core.HotkeyPolling, v => { core.HotkeyPolling = v; RefreshEngine(); }));
        panel.Add(Ui.Toggle("热键引擎有效性自检", "发现钩子不再收到按键时自动重启热键引擎。", () => core.HotkeySelfCheckEnabled, v => core.HotkeySelfCheckEnabled = v));
    }

    /// <summary>给一行热键加上「鼠标键」菜单：左右键和滚轮没法通过「按一下」来设置（那样界面就点不了了），所以从菜单里选。</summary>
    private Button MouseMenu(string name)
    {
        var flyout = new MenuFlyout();
        foreach (var vk in new[] { 0x01, 0x02, 0x04, HotkeyService.WheelUp, HotkeyService.WheelDown, 0x06, 0x05 })
        {
            var item = new MenuFlyoutItem { Text = Hotkey.KeyName(vk) };
            item.Click += (_, _) =>
            {
                CancelCapture();
                var hotkey = new Hotkey(HotkeyMods.None, vk);
                foreach (var other in QuellCore.HotkeyNames)
                    if (other != name && Core.GetHotkey(other) == hotkey) Core.SetHotkey(other, Hotkey.None);
                Core.SetHotkey(name, hotkey);
                RefreshKeys();
            };
            flyout.Items.Add(item);
        }
        return new Button { Content = "鼠标键", Flyout = flyout };
    }
}

public sealed partial class SettingsPage
{
    private void BuildExtras()
    {
        var core = App.Core;
        var panel = ExtraPanel.Children;

        panel.Add(Ui.Header("语言与日志"));
        panel.Add(Ui.Choice("日志和提示的语言", "只影响取自原版语言表的文字（日志和大部分提示框）。界面上的按钮和说明目前只有简体中文。重启后完全生效。",
            ["简体中文", "繁體中文", "English"], () => core.Language - 1, i => core.Language = i + 1));
        panel.Add(Ui.Toggle("每天自动存日志", "每条日志同时写到数据目录的 Logs 文件夹，一天一个文件。", () => core.LogDailySave, v => core.LogDailySave = v));
        panel.Add(Ui.Toggle("禁用记录日志", "不再记录任何日志（主页的日志区也不会有内容）。", () => core.LogDisabled, v => core.LogDisabled = v));

        panel.Add(Ui.Header("外部控制"));
        panel.Add(Ui.Toggle("UDP 指令", "让其他程序用 UDP 数据报触发操作，例如 kill、fwon、fwoff、single、bancard、timerst、joinbot1。只接受本机发来的指令。",
            () => core.UdpEnabled, v => core.UdpEnabled = v));
        panel.Add(Ui.Number("UDP 端口", "默认 55378。改完后把上面的开关关掉再打开才会生效。", 1, 65534, () => core.UdpPort, v => core.UdpPort = (int)v));

        panel.Add(Ui.Header("进程守护"));
        panel.Add(Ui.Toggle("进程结束保护", "另外启动一个很小的守护进程盯着本程序：本程序崩溃或被其他软件结束时，自动重新启动。正常退出不会触发。原版默认开启，移植版默认关闭。",
            () => core.Guard.Enabled, v => core.Guard.Enabled = v));
        panel.Add(Ui.Toggle("进程无响应保护", "需要先开启上一项。界面超过 12 秒没有响应时，结束并重新启动本程序。",
            () => core.Guard.WatchHang, v => core.Guard.WatchHang = v));
        panel.Add(Ui.Toggle("被重启后显示警告", "关闭时只在日志里记一条。", () => core.Guard.ShowWarning, v => core.Guard.ShowWarning = v));
        panel.Add(Ui.Toggle("被重启后自动恢复状态", "重新创建房间或连接上次的房间。断网、禁卡状态任何时候启动都会接着显示。",
            () => core.Guard.AutoRecover, v => core.Guard.AutoRecover = v));

        panel.Add(Ui.Header("原版更新"));
        panel.Add(Ui.Toggle("启动时检查原版更新", "移植版没有自己的更新渠道，这里查的是原版有没有比 Build 0216 更新的版本，用来判断移植版是否落后。不会自动下载任何东西。",
            () => core.Updates.CheckOnStartup, v => core.Updates.CheckOnStartup = v));
        panel.Add(Ui.Buttons("立即检查", "", ("检查更新", async () =>
        {
            var latest = await core.Updates.CheckAsync();
            var notice = await core.Updates.GetNoticeAsync();
            var text = latest is null ? Lang.C(12)
                : $"原版最新：Build {latest.Build}\n移植所依据：Build {TeamService.BuildVersion}\n\n{latest.Notes}";
            if (notice.Length > 0) text += "\n\n公告：\n" + notice;
            await Dialogs.MessageAsync(this, "原版更新", text);
        })));
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
