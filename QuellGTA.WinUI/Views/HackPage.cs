using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>骇入：自动骇入（五种小游戏）和自动指纹。</summary>
public sealed partial class HackPage : FeaturePage
{
    private static QuellCore Core => App.Core;

    public HackPage() : base("骇入", "抢劫任务里的骇入小游戏：截取游戏画面识别，再替你按键完成。只在游戏位于前台时按键。")
    {
        var hack = Core.AutoHack;
        Section("自动骇入", "进入骇入画面后按快捷键，程序会认出是哪一种小游戏并自动完成。");
        var state = Ui.Info("", "");
        Add(state);
        Add(Ui.Expander("自动骇入", "支持：" + string.Join("、", hack.SupportedGames) + "。", "\uE7EF",
            Ui.Switch(() => hack.Enabled, v => hack.Enabled = v),
            Ui.Shortcut(this, "骇入键"),
            Ui.Number("按键时长（毫秒）", "每次点按按住多久，默认 20。帧率低、游戏漏按键时调大。", 1, 250, () => hack.TapHoldMs, v => hack.TapHoldMs = (int)v, 5),
            Ui.Number("按键间隔（毫秒）", "两次点按之间的间隔，默认 20。", 1, 250, () => hack.TapGapMs, v => hack.TapGapMs = (int)v, 5),
            Ui.Toggle("识别叠加层", "把程序认出来的东西（标签、光标、目标位置等）画在游戏画面上，方便确认识别得对不对。叠加层不会出现在截图和录屏里。游戏要用无边框窗口才看得到。",
                () => hack.OverlayEnabled, v => hack.OverlayEnabled = v)));
        var toggle = new Button { MinWidth = 120 };
        toggle.Click += (_, _) => hack.Toggle();
        Add(Ui.Row("手动开始", "和快捷键的作用相同。开始后会先把游戏切到前台，最多等 60 秒骇入画面出现。", toggle, "\uE768"));

        Section("自动指纹", "名钻赌场和佩里科岛的指纹破解。在破解画面按快捷键：截取画面，认出该选哪几项，然后替你按方向键。只支持 16:9 的画面。");
        var fingerprint = Core.Fingerprint;
        Add(Ui.Expander("自动指纹", "", "\uE928",
            Ui.Switch(() => fingerprint.Enabled, v => fingerprint.Enabled = v),
            Ui.Shortcut(this, "指纹键"),
            Ui.Choice("指纹类型", "", ["名钻指纹（赌场豪劫）", "佩岛指纹（佩里科岛）"], () => (int)fingerprint.Kind, i => fingerprint.Kind = (FingerprintKind)i, 220),
            Ui.Toggle("游戏全屏模式", "开启：截取整个主屏幕。关闭：只截取游戏窗口（窗口化时使用）。", () => fingerprint.FullScreen, v => fingerprint.FullScreen = v),
            Ui.Number("按键延迟（毫秒）", "每次按键按住和间隔的时间，默认 60。帧率低时调大。", 10, 500, () => fingerprint.KeyDelay, v => fingerprint.KeyDelay = (int)v, 10),
            Ui.Number("图案相似度（%）", "默认 85。认不出来时可以调低一些。", 50, 100, () => fingerprint.Similarity, v => fingerprint.Similarity = (int)v, 5),
            Ui.Toggle("随任务自动切换", "根据启动器日志里的「正在玩」信息：进入名钻赌场任务时切到名钻指纹并开启，进入佩里科岛任务时切到佩岛指纹并开启，离开后关闭。",
                () => Core.FingerprintFollowMission, v => Core.FingerprintFollowMission = v)));

        void Refresh()
        {
            var running = hack.Running;
            state.Severity = running ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            state.Title = running ? "自动骇入运行中" : hack.Enabled ? "就绪" : "自动骇入未开启";
            state.Message = running ? (string.IsNullOrEmpty(hack.Status) ? "正在等待骇入画面…" : hack.Status) : "运行过程记录在「活动」页。";
            toggle.Content = running ? "停止" : "开始";
        }
        Action handler = () => DispatcherQueue.TryEnqueue(Refresh);
        Loaded += (_, _) => { hack.Changed -= handler; hack.Changed += handler; Refresh(); };
        Unloaded += (_, _) => hack.Changed -= handler;
    }
}
