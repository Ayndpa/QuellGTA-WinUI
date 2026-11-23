using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>浮窗：外置准星、简易计时，以及 RTSS 锁帧和 OSD。</summary>
public sealed partial class OverlayPage : FeaturePage
{
    private static QuellCore Core => App.Core;
    private static OverlayService Overlay => App.Core.Overlay;
    private readonly DispatcherQueueTimer _clock;
    private readonly SettingsCard _timerCard;
    private readonly Button _timerButton = new() { MinWidth = 120, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

    private static readonly (string name, string rgb)[] Colors =
    [
        ("青色", "00FFFF"), ("绿色", "00FF66"), ("红色", "FF3B30"), ("黄色", "FFE600"),
        ("品红", "FF2BD6"), ("白色", "FFFFFF"), ("浅粉（原版计时默认）", "FFC4C4"), ("橙色", "FF9500"),
        ("浅蓝（原版状态行默认）", "D6E9F8"), ("浅绿（原版签名默认）", "ABEBC6"),
    ];

    public OverlayPage() : base("浮窗", "显示在游戏画面上方、鼠标可以穿透的准星和计时器。游戏要设为「无边框窗口」；独占全屏下请改用 RTSS 显示。")
    {
        // ───────── 准星 ─────────
        Section("外置准星");
        var charCard = Ui.Text("准星字符", "最多 4 个字符。", () => Overlay.CrosshairText, v => Overlay.CrosshairText = v, "+", 120);
        void RefreshShape() => charCard.Visibility = Overlay.CrosshairShape == CrosshairShape.Text ? Visibility.Visible : Visibility.Collapsed;
        Add(Ui.Expander("外置准星", "在屏幕中央画一个固定的准星。", "\uE710",
            Ui.Switch(() => Overlay.CrosshairEnabled, v => Overlay.CrosshairEnabled = v),
            Ui.Choice("形状", "", ["十字", "圆点", "圆环", "十字 + 中心点", "空心十字", "字符"], () => (int)Overlay.CrosshairShape,
                i => { Overlay.CrosshairShape = (CrosshairShape)i; RefreshShape(); }, 180),
            charCard,
            Ui.Row("颜色", "", ColorCombo(() => Overlay.CrosshairColor, v => Overlay.CrosshairColor = v)),
            Ui.Row("大小", "", Slider(2, 80, 1, () => Overlay.CrosshairSize, v => Overlay.CrosshairSize = (int)v)),
            Ui.Row("线宽", "", Slider(1, 10, 1, () => Overlay.CrosshairThickness, v => Overlay.CrosshairThickness = (int)v)),
            Ui.Row("不透明度", "", Slider(10, 100, 5, () => Overlay.CrosshairOpacity, v => Overlay.CrosshairOpacity = (int)v)),
            Ui.Row("偏移", "相对屏幕（或游戏窗口）中心的像素偏移。", Offsets(() => Overlay.CrosshairOffsetX, v => Overlay.CrosshairOffsetX = v,
                () => Overlay.CrosshairOffsetY, v => Overlay.CrosshairOffsetY = v, "水平", "垂直")),
            Ui.Row("多屏偏移", ScreenOffsetNote, ScreenOffsets(() => Overlay.CrosshairScreenOffset, v => Overlay.CrosshairScreenOffset = v)),
            Ui.Toggle("黑色描边", "", () => Overlay.CrosshairOutline, v => Overlay.CrosshairOutline = v),
            Ui.Toggle("跟随游戏窗口", "以游戏窗口的中心为准，而不是屏幕中心。", () => Overlay.CrosshairFollowGame, v => Overlay.CrosshairFollowGame = v),
            Ui.Toggle("仅游戏在前台时显示", "", () => Overlay.CrosshairOnlyForeground, v => Overlay.CrosshairOnlyForeground = v),
            Ui.Toggle("在截屏和录屏中隐藏", "", () => Overlay.CrosshairHideFromCapture, v => Overlay.CrosshairHideFromCapture = v)));
        RefreshShape();

        // ───────── 计时 ─────────
        Section("简易计时");
        _timerButton.Click += (_, _) => { Core.AdvanceTimer(); RefreshTimer(); };
        _timerCard = Ui.Row("00:00:00.00", "", _timerButton, "\uE916");
        Add(_timerCard);
        Add(Ui.Shortcut(this, "计时键"));
        var manual = new TextBox { Width = 140, PlaceholderText = "00:12:34.50", IsSpellCheckEnabled = false };
        var apply = new Button { Content = "应用" };
        apply.Click += async (_, _) =>
        {
            if (OverlayService.ParseTime(manual.Text) is { } ms) Overlay.SetTimer(ms);
            else await Dialogs.MessageAsync(this, "格式不对", "请按「时:分:秒.百分秒」填写，例如 00:12:34.50；也可以只写「分:秒」。");
        };
        var manualPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        manualPanel.Children.Add(manual);
        manualPanel.Children.Add(apply);
        Add(Ui.Expander("计时操作", "暂停、记忆、手动改读数。", "\uE823", null,
            Ui.Buttons("暂停与恢复", "暂停后读数停住，恢复后接着走（结束计时之后也可以恢复）。", ("暂停", Overlay.PauseTimer), ("恢复", Overlay.ResumeTimer)),
            Ui.Buttons("记忆时间", "把当前读数记下来，之后可以读回来（重启程序后仍然有效）。",
                ("记忆当前时间", Overlay.SaveTimerMemory), ("读取记忆时间", () => Overlay.SetTimer(Overlay.TimerMemory))),
            Ui.Row("手动调整时间", "把读数直接改成指定的时间。", manualPanel)));
        var fonts = new[] { "Consolas", "Segoe UI", "Microsoft YaHei UI", "SimHei", "SimSun", "Arial", "Impact" };
        Add(Ui.Expander("计时器浮窗", "在游戏画面上显示计时读数。", "\uE7B3",
            Ui.Switch(() => Overlay.TimerEnabled, v => { Overlay.TimerEnabled = v; RefreshTimer(); }),
            Ui.Row("字号", "", Slider(8, 72, 1, () => Overlay.TimerFontSize, v => Overlay.TimerFontSize = (int)v)),
            Ui.Row("颜色", "", ColorCombo(() => Overlay.TimerColor, v => Overlay.TimerColor = v)),
            Ui.Row("不透明度", "", Slider(10, 100, 5, () => Overlay.TimerOpacity, v => Overlay.TimerOpacity = (int)v)),
            Ui.Row("位置", "水平偏移相对画面中心，另一项是距顶部的距离。", Offsets(() => Overlay.TimerOffsetX, v => Overlay.TimerOffsetX = v,
                () => Overlay.TimerOffsetY, v => Overlay.TimerOffsetY = v, "水平", "距顶部")),
            Ui.Row("多屏偏移", ScreenOffsetNote, ScreenOffsets(() => Overlay.TimerScreenOffset, v => Overlay.TimerScreenOffset = v)),
            Ui.Choice("字体", "默认 Consolas（等宽，数字跳动时宽度不变）。", ["Consolas", "Segoe UI", "微软雅黑", "黑体", "宋体", "Arial", "Impact"],
                () => Math.Max(0, Array.IndexOf(fonts, Overlay.TimerFont)), i => Overlay.TimerFont = fonts[i], 180),
            Ui.Text("个性签名", "显示在读数旁边的一行小字，留空不显示。", () => Overlay.TimerSignature, v => Overlay.TimerSignature = v, "", 240),
            Ui.Toggle("横排显示", "签名放在读数右边而不是下面；用 RTSS 显示时，状态、时间和签名也排成一行。", () => Overlay.TimerHorizontal, v => Overlay.TimerHorizontal = v),
            Ui.Choice("刷新率", "计时器走动时每秒重画几次。原版默认 100（每 10 毫秒），电脑较慢时可以降到 10。", ["100 次 / 秒", "30 次 / 秒", "10 次 / 秒"],
                () => Overlay.TimerFps switch { >= 100 => 0, >= 30 => 1, _ => 2 }, i => Overlay.TimerFps = i switch { 0 => 100, 1 => 30, _ => 10 }, 160),
            Ui.Toggle("不显示小时", "", () => Overlay.TimerHideHours, v => Overlay.TimerHideHours = v),
            Ui.Toggle("没在计时时隐藏", "", () => Overlay.TimerHideWhenIdle, v => Overlay.TimerHideWhenIdle = v),
            Ui.Toggle("在截屏和录屏中隐藏", "", () => Overlay.TimerHideFromCapture, v => Overlay.TimerHideFromCapture = v)));

        BuildRtss();

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(100);
        _clock.Tick += (_, _) => RefreshTimer();
        Loaded += (_, _) => { RefreshTimer(); _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
    }

    private void RefreshTimer()
    {
        _timerCard.Header = Overlay.FormatTimer(Overlay.TimerMilliseconds);
        _timerCard.Description = Overlay.TimerEnabled ? Overlay.TimerState switch
        {
            TimerState.Running => "计时中",
            TimerState.Paused => "已暂停",
            TimerState.Stopped => "已结束",
            _ => "就绪",
        } : "计时器浮窗未开启，开启后才能计时。";
        _timerButton.Content = Overlay.TimerState switch
        {
            TimerState.Idle => Lang.C(241),
            TimerState.Running => Lang.C(242),
            _ => Lang.C(243),
        };
        _timerButton.IsEnabled = Overlay.TimerEnabled;
    }

    private static Slider Slider(double min, double max, double step, Func<double> get, Action<double> set)
    {
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = get(), Width = 220 };
        slider.ValueChanged += (_, _) => set(slider.Value);
        return slider;
    }

    private static ComboBox ColorCombo(Func<string> get, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 180 };
        var current = get();
        foreach (var (name, rgb) in Colors) combo.Items.Add(new ComboBoxItem { Content = name, Tag = rgb });
        var custom = new ComboBoxItem { Content = "自定义…", Tag = "*" };
        combo.Items.Add(custom);
        var index = Array.FindIndex(Colors, c => c.rgb.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            custom.Content = "自定义 #" + current.ToUpperInvariant();
            index = combo.Items.Count - 1;
        }
        combo.SelectedIndex = index;
        var last = index;
        var picking = false;
        combo.SelectionChanged += async (_, _) =>
        {
            if (picking || combo.SelectedItem is not ComboBoxItem { Tag: string rgb }) return;
            if (rgb != "*")
            {
                last = combo.SelectedIndex;
                set(rgb);
                return;
            }
            // 自定义：弹出取色器（原版「对话框_打开颜色选择框」）
            picking = true;
            try
            {
                var start = get();
                var value = 0xFFFFFF;
                try { value = Convert.ToInt32(start, 16); } catch (Exception) { }
                var picker = new ColorPicker
                {
                    Color = Windows.UI.Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value),
                    IsAlphaEnabled = false,
                    IsMoreButtonVisible = true,
                };
                var dialog = new ContentDialog
                {
                    Title = "选择颜色",
                    Content = picker,
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await Dialogs.CustomAsync(combo, dialog) == ContentDialogResult.Primary)
                {
                    var hex = $"{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
                    custom.Content = "自定义 #" + hex;
                    last = combo.Items.Count - 1;
                    set(hex);
                }
                combo.SelectedIndex = last;
            }
            finally { picking = false; }
        };
        return combo;
    }

    private const string ScreenOffsetNote = "按主屏幕的宽高整块挪动，用来放到旁边的显示器上。例如副屏在主屏左边，横向填 -1。";

    /// <summary>多屏偏移：横向、纵向各挪几个屏幕。</summary>
    private static StackPanel ScreenOffsets(Func<(int x, int y)> get, Action<(int x, int y)> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var x = Ui.NumberBox(-8, 8, () => get().x, v => set(((int)v, get().y)), 1, 110);
        x.Header = "横向（屏）";
        var y = Ui.NumberBox(-8, 8, () => get().y, v => set((get().x, (int)v)), 1, 110);
        y.Header = "纵向（屏）";
        panel.Children.Add(x);
        panel.Children.Add(y);
        return panel;
    }

    private static StackPanel Offsets(Func<int> getX, Action<int> setX, Func<int> getY, Action<int> setY, string xLabel, string yLabel)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, get, set) in new[] { (xLabel, getX, setX), (yLabel, getY, setY) })
        {
            var box = Ui.NumberBox(-4000, 4000, () => get(), v => set((int)v), 1, 120);
            box.Header = label;
            panel.Children.Add(box);
        }
        return panel;
    }

    // ───────── RTSS ─────────

    private void BuildRtss()
    {
        Section("RTSS 锁帧", "通过 RivaTuner Statistics Server（RTSS，需自行安装）限制游戏帧数。常见用法：过场动画或特定任务里临时锁到低帧数，用快捷键在几个档位之间切换。");
        var status = Ui.Row("RTSS", "", null, "\uE9E9");
        void Describe()
        {
            var installed = RtssService.ExecutablePath is not null;
            var limit = Core.RtssCurrentLimit < 0 ? "" : "；当前上限：" + QuellCore.FrameLimitText(Core.RtssCurrentLimit)
                + (Core.RtssLimiterActive == false ? "（锁帧总开关已关）" : "");
            status.Description = (RtssService.IsRunning ? "正在运行" : installed ? "已安装，未运行" : "没有检测到 RTSS") + limit;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void AddButton(string text, Action click)
        {
            var button = new Button { Content = text };
            button.Click += async (_, _) => { click(); await Task.Delay(600); Describe(); };
            buttons.Children.Add(button);
        }
        AddButton("刷新", () => Task.Run(Core.RefreshRtssState));
        AddButton("启动", () => { if (!RtssService.Start() && RtssService.ExecutablePath is null) Shell.Open(RtssService.DownloadUrl); });
        AddButton("关闭", () => RtssService.Close(Core.RtssForceClose));
        AddButton("下载", () => Shell.Open(RtssService.DownloadUrl));
        status.Content = buttons;
        Add(status);
        Describe();

        Add(Ui.Toggle("RTSS 锁帧控制", "开启后才能用下面的预设和快捷键。", () => Core.RtssLimiter, v => Core.RtssLimiter = v, "\uE7E8"));
        Add(Ui.Shortcut(this, "锁帧键"));
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            var box = new NumberBox
            {
                Minimum = -1, Maximum = 999, Width = 90, Value = Core.RtssPresets[index], Header = $"预设{"一二三"[index]}",
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            };
            box.ValueChanged += (_, _) =>
            {
                if (double.IsNaN(box.Value)) return;
                var values = Core.RtssPresets;
                values[index] = (int)box.Value;
                Core.RtssPresets = values;
            };
            var apply = new Button { Content = "应用", VerticalAlignment = VerticalAlignment.Bottom };
            apply.Click += async (_, _) =>
            {
                var value = Core.RtssPresets[index];
                if (value >= 0) await Task.Run(() => Core.ApplyFrameLimit(value));
                Describe();
            };
            presets.Children.Add(box);
            presets.Children.Add(apply);
        }
        Add(Ui.Expander("帧数预设", "0 = 不限制，-1 = 停用这一档（切换时跳过）。", "\uE8FD", null,
            Ui.Row("三个档位", "", presets),
            Ui.Buttons("锁帧总开关", "只开关 RTSS 的帧数限制，不改上限的数值。",
                ("下一个预设", () => { Core.CycleFrameLimit(); Describe(); }),
                ("停用锁帧", () => { Core.SetRtssLimiterActive(false); Describe(); }),
                ("启用锁帧", () => { Core.SetRtssLimiterActive(true); Describe(); }))));
        Add(Ui.Choice("锁帧键的作用", "循环预设：每按一次换到下一个档位。手动输入：每按一次弹出主窗口，输入要锁的帧数。", ["循环预设", "手动输入帧数"],
            () => Core.RtssManualInput ? 1 : 0, i => Core.RtssManualInput = i == 1, 160, "\uE765"));
        Add(Ui.Expander("RTSS 选项", "", "\uE713", null,
            Ui.Toggle("仅游戏进程", "只改 RTSS 里游戏进程自己的配置。关闭后改的是全局配置；RTSS 里已有的游戏配置会比全局优先生效，需要你到 RTSS 里手动删掉。",
                () => Core.RtssOnlyGame, v => Core.RtssOnlyGame = v),
            Ui.Toggle("按需自动启动 RTSS", "应用帧数时如果 RTSS 没在运行，自动把它启动起来。", () => Core.RtssAutoStart, v => Core.RtssAutoStart = v),
            Ui.Toggle("退出本程序时关闭 RTSS", "", () => Core.RtssAutoClose, v => Core.RtssAutoClose = v),
            Ui.Toggle("关闭 RTSS 时强制结束进程", "默认是请 RTSS 自己退出。它不响应时再打开这一项。", () => Core.RtssForceClose, v => Core.RtssForceClose = v)));

        Section("用 RTSS 显示", Lang.C(459));
        Add(Ui.Expander("用 RTSS 显示计时", "独占全屏时内置的浮窗显示不出来，RTSS 的 OSD 可以。只在游戏运行时显示；签名里的中文不会显示。", "\uE916",
            Ui.Switch(() => Core.RtssOsdTimer, v => Core.RtssOsdTimer = v),
            Ui.Number("位置 X", "RTSS 的坐标单位，从画面左上角算起。", 0, 4000, () => Core.RtssTimerX, v => Core.RtssTimerX = (int)v, 10),
            Ui.Number("位置 Y", "", 0, 4000, () => Core.RtssTimerY, v => Core.RtssTimerY = (int)v, 10),
            Ui.Toggle("显示锁帧帧数", "在计时上面的状态行里显示当前锁的帧数。", () => Core.RtssShowLimit, v => Core.RtssShowLimit = v),
            Ui.Row("状态行颜色", "BLOCK、NIC、SOLO 和帧数这一行的颜色。", ColorCombo(() => Overlay.OsdColor, v => Overlay.OsdColor = v)),
            Ui.Row("签名颜色", "个性签名的颜色，内置的计时器浮窗也用这个颜色。", ColorCombo(() => Overlay.SignatureColor, v => Overlay.SignatureColor = v))));
        Add(Ui.Expander("用 RTSS 显示准星", "用一个字符当准星，位置和颜色沿用外置准星的设置。", "\uE710",
            Ui.Switch(() => Core.RtssOsdCrosshair, v => Core.RtssOsdCrosshair = v),
            Ui.Text("准星字符", "原版默认 *，只能用英文符号。", () => Core.RtssCrosshairText, v => Core.RtssCrosshairText = v, "*", 120)));
    }
}
