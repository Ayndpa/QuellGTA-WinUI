using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>挂机：前台挂机、自动挂机防踢、虚拟手柄（后台挂机与手柄偷速）。</summary>
public sealed partial class AfkPage : FeaturePage
{
    private static QuellCore Core => App.Core;

    public AfkPage() : base("挂机", "离开电脑时防止因长时间不操作被踢出战局。")
    {
        // ───────── 前台挂机 ─────────
        Section("前台挂机", "把游戏切到前台并替你一直按住某个键。亲自按一下那个键，或者切走游戏窗口，挂机就结束。");
        var start = new Button { MinWidth = 120, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        start.Click += (_, _) => Run(Core.ToggleAfk);
        var startCard = Ui.Row("", "", start, "\uE823");
        Add(startCard);
        Add(Ui.Shortcut(this, "挂机键"));
        Add(Ui.Expander("挂机方式", "", "\uE713",
            Ui.Combo(["按住右键（自由状态）", "按住 S 键（差事页面）"], () => (int)Core.AfkMode, i => Core.AfkMode = (AfkMode)i, 200),
            Ui.Toggle("挂机时伪后台", "开始挂机后把游戏窗口压到所有窗口的最底下。游戏仍是活动窗口，只是被别的窗口盖住；点了别的窗口挂机就会结束。",
                () => Core.AfkPseudoBackground, v => Core.AfkPseudoBackground = v),
            Ui.Buttons("仅执行伪后台操作", "不挂机，只把游戏窗口压到最底下。",
                ("执行", () => { if (!Core.SendGameWindowToBack()) Core.Log.Write("游戏未在运行。"); }))));

        // ───────── 防踢 ─────────
        Section("自动挂机防踢", "游戏运行期间，超过设定时间没有任何键鼠操作，就替你按一下键（或推一下虚拟手柄的摇杆）。");
        Add(Ui.Expander("长时间无操作时自动按键", "", "\uE916",
            Ui.Switch(() => Core.AntiAfkEnabled, v => Core.AntiAfkEnabled = v),
            Ui.Number("超时（秒）", "多久没有操作算挂机，默认 120。", 10, 3600, () => Core.AntiAfkTimeout, v => Core.AntiAfkTimeout = (int)v),
            Ui.Key(this, "超时后按下的按键", "默认 Z。选一个在游戏里按了没有副作用的键。", () => Core.AntiAfkKey, v => Core.AntiAfkKey = v),
            Ui.Toggle("游戏在后台时先切到前台", "关闭后，游戏不在前台时不按键，只在日志里提示。", () => Core.AntiAfkBringToFront, v => Core.AntiAfkBringToFront = v)));

        // ───────── 虚拟手柄 ─────────
        Section("虚拟手柄", "通过一只虚拟手柄推左摇杆，需要先安装 ViGEmBus 驱动。虚拟手柄插着的时候，游戏会把按键提示换成手柄图标。");
        Add(Ui.Expander("模拟手柄挂机（真后台）", "防踢超时后不按键盘，改为把左摇杆推一下再松开。游戏在后台也有效，不会打断你在别的窗口里做的事。", "\uE7FC",
            PadSwitch(() => Core.GamepadAfk, v => Core.GamepadAfk = v),
            Ui.Choice("推动方向", "原版默认向下。", GamepadService.DirectionArrows, () => Core.GamepadAfkDirection, i => Core.GamepadAfkDirection = i, 120),
            Ui.Number("推动时长（毫秒）", "默认 1000。", 10, 60000, () => Core.GamepadAfkDuration, v => Core.GamepadAfkDuration = (int)v, 100),
            Ui.Number("推动行程", "0–255，默认 255（推到底）。", 0, 255, () => Core.GamepadAfkStroke, v => Core.GamepadAfkStroke = (int)v)));
        Add(Ui.Expander("手柄偷速", "按快捷键时把左摇杆往前推到约 88%，人物以偷速状态奔跑。只在游戏位于前台时生效。", "\uE7C9",
            PadSwitch(() => Core.GamepadRun, v => Core.GamepadRun = v),
            Ui.Shortcut(this, "手柄跑键"),
            Ui.Choice("触发方式", "「按下时切换」会在偷速期间替你按住 Shift（触发键本身是 Shift 时除外）。", ["按住按键时启动", "按下时切换开关"],
                () => Core.GamepadRunToggle ? 1 : 0, i => Core.GamepadRunToggle = i == 1, 180),
            Ui.Number("偷速行程", "0–255，默认 224（约 88%），适用于 PS4 虚拟手柄。", 0, 255, () => Core.GamepadRunStroke, v => Core.GamepadRunStroke = (int)v),
            Ui.Toggle("启停时声音提醒", "", () => Core.GamepadRunSound, v => Core.GamepadRunSound = v)));
        Add(Ui.Expander("虚拟手柄选项", "", "\uE713", null,
            Ui.Choice("手柄类型", "默认 PS4。改完后下次创建虚拟手柄时生效。", ["PlayStation 4", "Xbox 360"],
                () => Core.Gamepad.UseXbox ? 1 : 0, i => Core.Gamepad.UseXbox = i == 1, 180),
            Ui.Toggle("按需创建", "只在要推摇杆的那一刻插上虚拟手柄，推完就拔掉，平时游戏仍显示键鼠提示。偷速需要随按随有，建议关闭。",
                () => Core.GamepadOnDemand, v => Core.GamepadOnDemand = v),
            Ui.Buttons("移除虚拟手柄", "关闭上面两个功能并立即拔掉虚拟手柄。之后需要重启游戏，它才会重新认出真手柄。", ("移除", async () =>
            {
                if (!await Dialogs.ConfirmAsync(this, "移除虚拟手柄", Lang.C(531))) return;
                Core.RemoveGamepad();
                App.Window.ReloadPage(); // 重新加载本页，让开关显示最新状态
            })),
            Ui.Buttons("ViGEmBus 驱动", "虚拟手柄依赖这个驱动，由用户自行安装。", ("下载页", () => Shell.Open(GamepadService.DriverUrl)))));

        OnCoreState(() =>
        {
            startCard.Header = Core.AfkActive ? "正在前台挂机" : "未在挂机";
            startCard.Description = Core.AfkActive ? "亲自按一下挂机的那个键，或切走游戏窗口即结束。" : "点开始后会把游戏切到前台。";
            start.Content = Core.AfkActive ? "停止挂机" : "开始挂机";
        });
        Unloaded += (_, _) => Core.Hotkeys.CancelCapture();
    }

    /// <summary>需要 ViGEmBus 驱动的开关：没装驱动时弹回去，并给出下载页。</summary>
    private ToggleSwitch PadSwitch(Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = get(), OnContent = "开", OffContent = "关" };
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
        return toggle;
    }
}
