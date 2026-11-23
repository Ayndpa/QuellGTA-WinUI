using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>战局：卡单、瞬间悬停、战局锁。</summary>
public sealed partial class SessionPage : FeaturePage
{
    private static QuellCore Core => App.Core;
    private readonly InfoBar _soloState = Ui.Info("", "");
    private readonly ComboBox _mode;
    private readonly SettingsCard _switchKey;
    private readonly InfoBar _lockInfo = Ui.Info("", "");
    private readonly ComboBox _password = new() { IsEditable = true, Width = 200, PlaceholderText = "输入密码" };
    private readonly Button _lock = new() { Content = "上锁", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _unlock = new() { Content = "解锁" };
    private readonly SettingsCard _gameDir;

    // 下拉框顺序：模式 1 ~ 4，最后一项是关闭（模式 0）
    private static int ModeIndex() => Core.SoloMode == SoloMode.Off ? 4 : (int)Core.SoloMode - 1;

    public SessionPage() : base("战局", "得到单人公开战局、锁住自己的战局、断网后悬停在保存失败的提示上。")
    {
        // ───────── 卡单 ─────────
        Section("战局卡单", "让游戏短暂失去响应，其他玩家因超时离开，从而得到单人公开战局。");
        Add(_soloState);
        var toggle = new Button { Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 120 };
        toggle.Click += (_, _) => Run(Core.ToggleSolo);
        Add(Ui.Row("立即卡单", "卡单过程中再点一次立即结束。", toggle, "\uE77B"));
        Add(Ui.Shortcut(this, "卡单键"));
        _mode = Ui.Combo(["暂停进程（推荐）", "挂起窗口（传承版）", "右键菜单（增强版，需前台）", "阻止 P2P 端口 6672", "关闭"],
            ModeIndex, i =>
            {
                // 卡单过程中不允许换模式，否则会用错误的方式去恢复游戏
                if (Core.SoloActive) return;
                Core.SoloMode = i == 4 ? SoloMode.Off : (SoloMode)(i + 1);
            }, 240);
        Add(Ui.Expander("卡单方式", "暂停进程兼容性最好；阻止 P2P 端口不暂停游戏，而是用防火墙把其他玩家隔开。", "\uE713", _mode,
            Ui.Number("卡单时长（秒）", "到时间后自动结束。", 1, 60, () => Math.Round(Core.SoloDurationMs / 1000.0),
                v => Core.SoloDurationMs = (int)Math.Round(v * 1000) + 1), // 原版默认值是 10001 毫秒，保留多出来的 1 毫秒
            Ui.Number("安全间隔（毫秒）", "两次卡单操作之间的最短间隔，防止连按。", 100, 10000, () => Core.SoloIntervalMs,
                v => Core.SoloIntervalMs = (int)v, 100)));

        // ───────── 悬停 ─────────
        Section("瞬间悬停", "断网后让游戏停在「保存失败」的状态，用于卡住任务奖励等。");
        Add(Ui.Shortcut(this, "悬停键"));
        Add(Ui.Choice("悬停方式",
            "Alt+F4 方式：临时阻断游戏联网 → 向游戏发送关闭 → 按 Esc 取消退出，保存请求发不出去。切换角色方式：按住快捷键时替你按住切换角色键，松开时松开并按 Esc。",
            ["关闭", "Alt+F4 阻云悬停（只断 TCP）", "Alt+F4 断网悬停（全部断开）", "切换角色悬停"],
            () => Core.HoverMode switch
            {
                HoverMode.AltF4BlockCloud => 1,
                HoverMode.AltF4Disconnect => 2,
                HoverMode.SwitchCharacter => 3,
                _ => 0,
            },
            i =>
            {
                Core.HoverMode = i switch
                {
                    1 => HoverMode.AltF4BlockCloud,
                    2 => HoverMode.AltF4Disconnect,
                    3 => HoverMode.SwitchCharacter,
                    _ => HoverMode.Off,
                };
                RefreshSwitchKey();
            }, 260, "\uE916"));
        _switchKey = Ui.Key(this, "切换角色键", "填游戏里「切换到某个故事模式角色」的按键。", () => Core.SwitchCharacterKey, v => Core.SwitchCharacterKey = v, "\uE77B");
        Add(_switchKey);
        RefreshSwitchKey();

        // ───────── 战局锁 ─────────
        Section("战局锁", "只有使用相同密码的玩家才会匹配到同一个战局。创建或删除后都要重启游戏才生效。");
        Add(_lockInfo);
        _lock.Click += Lock_Click;
        _unlock.Click += Unlock_Click;
        var lockPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        lockPanel.Children.Add(_password);
        lockPanel.Children.Add(_lock);
        lockPanel.Children.Add(_unlock);
        Add(Ui.Row("战局密码", "记住用过的密码，下次从下拉框里选。", lockPanel, "\uE72E"));
        _gameDir = Ui.Row("游戏位置", "", Ui.ButtonRow(("刷新状态", RefreshLock), ("清空记住的密码", () =>
        {
            Core.SessionLock.ClearSavedPasswords();
            RefreshLock();
        })), "\uE8B7");
        Add(_gameDir);

        OnCoreState(RefreshSolo);
        Loaded += (_, _) => RefreshLock();
        Unloaded += (_, _) => Core.Hotkeys.CancelCapture();

        void RefreshSolo()
        {
            _soloState.Severity = Core.SoloActive ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            _soloState.Title = Core.SoloActive ? "卡单中" : "未在卡单";
            _soloState.Message = Core.SoloMode == SoloMode.Off ? "卡单功能已关闭。" : $"每次卡单 {Core.SoloDurationMs / 1000.0:0.#} 秒。";
            toggle.Content = Core.SoloActive ? "结束卡单" : "开始卡单";
            toggle.IsEnabled = Core.SoloMode != SoloMode.Off;
            _mode.IsEnabled = !Core.SoloActive;
            _mode.SelectedIndex = ModeIndex();
        }
    }

    private void RefreshSwitchKey() =>
        _switchKey.Visibility = Core.HoverMode == HoverMode.SwitchCharacter ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshLock()
    {
        var status = Core.SessionLock.GetStatus();
        _lockInfo.Severity = status.State switch
        {
            LockState.Locked => InfoBarSeverity.Success,
            LockState.Unlocked => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Warning,
        };
        _lockInfo.Title = status.State switch
        {
            LockState.Locked => "已上锁",
            LockState.Unlocked => "未上锁",
            LockState.Corrupted => "锁文件损坏",
            _ => "无法使用",
        };
        _lockInfo.Message = status.Message;
        _lock.IsEnabled = status.State is LockState.Locked or LockState.Unlocked or LockState.Corrupted;
        _unlock.IsEnabled = status.State is LockState.Locked or LockState.Corrupted;
        _gameDir.Description = Core.Files.InstallDir.Length > 0 ? Core.Files.InstallDir : "还不知道游戏装在哪。请在本程序运行时启动一次游戏。";
        var typed = _password.Text;
        _password.Items.Clear();
        foreach (var password in Core.SessionLock.SavedPasswords) _password.Items.Add(password);
        _password.Text = status.State == LockState.Locked ? status.Password
            : !string.IsNullOrEmpty(typed) ? typed
            : Core.SessionLock.SavedPasswords.FirstOrDefault() ?? "QuellGTA";
    }

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        var password = _password.Text ?? "";
        if (Core.GameRunning && !await Dialogs.ConfirmAsync(this, "游戏正在运行", "战局锁要重启游戏后才生效，游戏运行时文件也可能被占用。仍然继续？"))
            return;
        var result = Core.SessionLock.Lock(password);
        RefreshLock();
        await Dialogs.MessageAsync(this, result.Ok ? "战局锁已创建" : "创建失败", result.Message);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var result = Core.SessionLock.Unlock();
        RefreshLock();
        await Dialogs.MessageAsync(this, result.Ok ? "战局锁已删除" : "删除失败", result.Message);
    }
}
