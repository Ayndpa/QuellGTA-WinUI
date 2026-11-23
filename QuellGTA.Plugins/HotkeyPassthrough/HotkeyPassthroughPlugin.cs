using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Plugins;

namespace QuellGTA.Plugin.HotkeyPassthrough;

/// <summary>
/// 热键穿透（原版独立工具「HotKeySavior for GTAV」，早期主程序里也有同名功能）。
/// GTA 在前台时会挡掉其他程序用 RegisterHotKey 注册的全局热键（OBS 录像、音乐播放器切歌等）。
/// 列表里的组合键在游戏前台被按下时，先把焦点切到自己的一个小窗口，再把组合键重发一遍让那个程序收到，然后切回游戏。
/// </summary>
public sealed class HotkeyPassthroughPlugin : IQuellPlugin
{
    private const string FileName = "HKsavior.ini";

    private IPluginHost _host = null!;
    private PassthroughEngine _engine = null!;
    private Timer? _watchdog;
    private List<Combo> _combos = [];

    private bool Enabled
    {
        get => _host.Settings.GetBool("启用", true);
        set => _host.Settings.SetBool("启用", value);
    }

    /// <summary>原版固定 300 毫秒。有的程序反应慢，可以调大。</summary>
    private int SwitchDelay
    {
        get => Math.Clamp(_host.Settings.GetInt("切换等待", 300), 50, 2000);
        set => _host.Settings.SetInt("切换等待", value);
    }

    private string ListPath => Path.Combine(_host.DataDirectory, FileName);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _engine = new PassthroughEngine(() => _host.Game.IsForeground, () => _host.Game.WindowHandle,
            (text, warning) => _host.Log(text, warning ? PluginLogLevel.Warning : PluginLogLevel.Info));
        _engine.SwitchDelay = SwitchDelay;
        Load();
        _host.Game.Started += OnGameStarted;
        Apply();
    }

    private void OnGameStarted() => _engine.Rehook();

    /// <summary>有东西要穿透且开着时才装钩子；没有就不占用系统钩子。</summary>
    private void Apply()
    {
        _engine.SetCombos(_combos);
        if (Enabled && _combos.Count > 0)
        {
            _engine.Start();
            _watchdog ??= new Timer(_ => _engine.CheckHook(_host.Input.IdleMilliseconds), null, 5000, 5000);
        }
        else
        {
            _watchdog?.Dispose();
            _watchdog = null;
            _engine.Stop();
        }
    }

    // ───────── 列表文件 ─────────

    /// <summary>
    /// 对应原版「读取报表」。第一次运行时把主程序数据目录里的 HKsavior.ini（早期主程序「热键穿透」用的那份）导进来；
    /// 独立版放在它自己的程序目录里，位置不固定，没法自动找。
    /// </summary>
    private void Load()
    {
        try
        {
            var path = ListPath;
            if (!File.Exists(path))
            {
                var legacy = Path.Combine(_host.HostDataDirectory, FileName);
                if (!File.Exists(legacy)) return;
                _combos = ComboFile.Parse(File.ReadAllText(legacy));
                Save();
                _host.Log($"已导入原版的热键穿透列表（{_combos.Count} 项）", PluginLogLevel.Success);
                return;
            }
            _combos = ComboFile.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _host.Log("读取热键穿透列表失败：" + ex.Message, PluginLogLevel.Error);
        }
    }

    /// <summary>对应原版「保存报表」，格式不变。</summary>
    private void Save()
    {
        try { File.WriteAllText(ListPath, ComboFile.Serialize(_combos)); }
        catch (Exception ex) { _host.Log("保存热键穿透列表失败：" + ex.Message, PluginLogLevel.Error); }
    }

    // ───────── 页面 ─────────

    public UIElement? CreatePage()
    {
        var ui = _host.Ui;
        var page = ui.CreatePage("热键穿透",
            "GTA 在前台时，OBS、音乐播放器等程序的全局热键会失灵。把这些组合键加到下面的列表里，"
            + "在游戏里按下时会先切到一个看不见的小窗口、把组合键重发给那些程序，再切回游戏。");

        var status = ui.Info("", "");
        void RefreshStatus()
        {
            var running = _engine.IsRunning;
            status.Title = !Enabled ? "未启用" : _combos.Count == 0 ? "列表是空的" : "正在监听";
            status.Message = !Enabled ? "" : _combos.Count == 0 ? "先在下面添加要穿透的组合键。" : $"{_combos.Count} 个组合键，只在游戏位于前台时穿透。";
            status.Severity = running ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        }

        page.Add(status);
        page.Add(ui.Toggle("启用", "关闭后不再监听键盘。", () => Enabled, v =>
        {
            Enabled = v;
            Apply();
            RefreshStatus();
        }, ""));
        page.Add(ui.Number("切换等待（毫秒）", "切到小窗口后等多久再重发。原版是 300；目标程序没反应时调大一些。", 50, 2000,
            () => SwitchDelay, v =>
            {
                SwitchDelay = (int)v;
                _engine.SwitchDelay = SwitchDelay;
            }, 50));
        page.Add(ui.Info("提示",
            "全屏独占模式下游戏失去焦点会最小化再还原，建议用无边框窗口。重发的组合键不会触发本程序自己的快捷键。", 0));

        // ───────── 列表 ─────────
        page.Section("要穿透的组合键");
        var listPanel = new StackPanel { Spacing = 4 };
        var empty = ui.Info("还没有组合键", "用下面的「添加」录一个，例如 OBS 的开始录制键。");

        void RefreshList()
        {
            listPanel.Children.Clear();
            foreach (var combo in _combos)
            {
                var delete = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
                ToolTipService.SetToolTip(delete, "删除");
                var target = combo;
                delete.Click += (_, _) =>
                {
                    _combos.Remove(target);
                    Save();
                    Apply();
                    RefreshList();
                };
                listPanel.Children.Add(ui.Row(target.ToString(), "", delete, ""));
            }
            empty.IsOpen = _combos.Count == 0;
            RefreshStatus();
        }

        page.Add(empty);
        page.Add(listPanel);

        // ───────── 添加 ─────────
        // 主键用主程序的按键录制卡（它不记修饰键），修饰键用三个勾选框；原版是一个记录「功能键状态 + 键代码」的编辑框。
        page.Section("添加", "先点「主键」按下要穿透的键，再勾选要一起按住的修饰键。");
        var pendingKey = 0x78; // F9
        var ctrl = new CheckBox { Content = "Ctrl", MinWidth = 0 };
        var alt = new CheckBox { Content = "Alt", MinWidth = 0 };
        var shift = new CheckBox { Content = "Shift", MinWidth = 0 };
        var preview = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        var add = new Button { Content = "添加", Style = AccentStyle() };

        Combo Pending() => new((alt.IsChecked == true ? ComboMods.Alt : 0) | (ctrl.IsChecked == true ? ComboMods.Ctrl : 0)
                               | (shift.IsChecked == true ? ComboMods.Shift : 0), pendingKey);
        void RefreshPending()
        {
            var combo = Pending();
            if (!combo.IsValid) { preview.Text = "这个键不能用"; add.IsEnabled = false; }
            else if (_combos.Contains(combo)) { preview.Text = $"{combo} 已在列表里"; add.IsEnabled = false; }
            else { preview.Text = $"将添加 {combo}"; add.IsEnabled = true; }
        }
        foreach (var box in new[] { ctrl, alt, shift })
        {
            box.Checked += (_, _) => RefreshPending();
            box.Unchecked += (_, _) => RefreshPending();
        }
        add.Click += (_, _) =>
        {
            var combo = Pending();
            if (!combo.IsValid || _combos.Contains(combo)) return;
            _combos.Add(combo);
            Save();
            Apply();
            RefreshList();
            RefreshPending();
        };

        page.Add(ui.Key("主键", "Esc 取消录制。鼠标键和单独的修饰键不能用。", () => pendingKey, v =>
        {
            pendingKey = v;
            RefreshPending();
        }, ""));
        var modsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        modsPanel.Children.Add(ctrl);
        modsPanel.Children.Add(alt);
        modsPanel.Children.Add(shift);
        page.Add(ui.Row("修饰键", "要和目标程序里设的热键完全一致，多按或少按一个都不会穿透。", modsPanel, ""));
        var addPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        addPanel.Children.Add(preview);
        addPanel.Children.Add(add);
        page.Add(ui.Row("添加到列表", "", addPanel, ""));

        page.Shown += () => { RefreshList(); RefreshPending(); };
        RefreshList();
        RefreshPending();
        return page.Element;
    }

    private static Style? AccentStyle() =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) ? style as Style : null;

    public void Dispose()
    {
        if (_host is not null) _host.Game.Started -= OnGameStarted;
        _watchdog?.Dispose();
        _watchdog = null;
        _engine?.Dispose();
    }
}
