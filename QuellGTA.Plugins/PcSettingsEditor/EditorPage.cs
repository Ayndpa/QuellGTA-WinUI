using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuellGTA.Plugins;
using Windows.Storage.Pickers;

namespace QuellGTA.Plugin.PcSettingsEditor;

/// <summary>
/// 插件页面。顶部是存档列表和当前文件，下面用标签切换原版的各个功能页。
/// 文件内容重新读取后，当前标签整个重建（控件都从内存里的文件取值）。
/// </summary>
internal sealed class EditorPage
{
    private readonly PcSettingsEditorPlugin _plugin;
    private readonly IPluginHost _host;
    private readonly IPluginUi _ui;
    private readonly IPluginPage _page;
    private readonly ListView _profiles = new() { Height = 168, SelectionMode = ListViewSelectionMode.Single };
    private readonly InfoBar _fileInfo;
    private readonly SelectorBar _tabs = new() { Margin = new Thickness(0, 12, 0, 4) };
    private readonly StackPanel _content = new() { Spacing = 4 };
    private bool _filling;

    // 随后台状态刷新的控件（只在对应标签显示时存在）
    private InfoBar? _tunnelInfo;
    private InfoBar? _cloudInfo;
    private TextBox? _cloudLog;

    private static readonly string[] TabNames = ["常用设置", "任务状态", "名钻隧道", "云存档", "手动编辑"];
    private static readonly string[] TabGlyphs = ["", "", "", "", ""];

    public UIElement Element => _page.Element;

    public EditorPage(PcSettingsEditorPlugin plugin)
    {
        _plugin = plugin;
        _host = plugin.Host;
        _ui = _host.Ui;
        _page = _ui.CreatePage("PC 设置编辑器",
            "编辑线上设置文件 pc_settings.bin。游戏退出时会重写这个文件，请先退出游戏再保存，保存后重新启动游戏生效。原版「GTAOL PC设置编辑器 V3.4」。");

        // ───────── 存档 ─────────
        _page.Section("存档", "最近一次使用时间最晚的通常就是当前存档。清洗设置文件在「修复」页，换机导出时也可以在「迁移」页顺带清洗。");
        _profiles.SelectionChanged += (_, _) => _ = OnProfileSelected();
        _page.Add(Table(["游戏版本", "存档代号", "最近一次使用"], [90, -1, 170], _profiles));
        _fileInfo = _ui.Info("", "");
        _page.Add(_fileInfo);
        _page.Add(_ui.Buttons("文件", "锁定 = 设为只读：游戏退出时改不了它，游戏里改过的设置也就不会保存。要修改时先解锁。",
            ("刷新列表", () => { _plugin.RescanProfiles(); FillProfiles(); RefreshAll(); }),
            ("打开其他文件…", () => _ = OpenOther()),
            ("重新读取", () => _ = Reload()),
            ("锁定", () => _ = Lock(true)),
            ("解锁", () => _ = Lock(false))));

        // ───────── 功能标签 ─────────
        for (var i = 0; i < TabNames.Length; i++)
            _tabs.Items.Add(new SelectorBarItem { Text = TabNames[i], Icon = new FontIcon { Glyph = TabGlyphs[i] } });
        _tabs.SelectedItem = _tabs.Items[Math.Clamp(_plugin.SelectedTab, 0, TabNames.Length - 1)];
        _tabs.SelectionChanged += (_, _) =>
        {
            _plugin.SelectedTab = Math.Max(0, _tabs.Items.IndexOf(_tabs.SelectedItem));
            RenderTab();
        };
        _page.Add(_tabs);
        _page.Add(_content);

        Action handler = () => _host.Dispatcher.TryEnqueue(RefreshLive);
        _page.Shown += () =>
        {
            _plugin.StateChanged -= handler;
            _plugin.StateChanged += handler;
            RefreshFile();
            RefreshLive();
        };
        _page.Hidden += () => _plugin.StateChanged -= handler;

        FillProfiles();
        RefreshAll();
    }

    // ───────── 存档列表 ─────────

    private void FillProfiles()
    {
        _filling = true;
        _profiles.Items.Clear();
        foreach (var p in _plugin.Profiles)
            _profiles.Items.Add(new ListViewItem { HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Cells([p.Edition, p.Id, p.LastUsedText], [90, -1, 170]) });
        _profiles.SelectedIndex = _plugin.SelectedProfile;
        _filling = false;
    }

    private async Task OnProfileSelected()
    {
        if (_filling || _profiles.SelectedIndex < 0 || _profiles.SelectedIndex == _plugin.SelectedProfile) return;
        if (_plugin.Dirty && !await _ui.ConfirmAsync("切换存档", "当前文件有未保存的修改，切换后会丢失。", "切换"))
        {
            _filling = true;
            _profiles.SelectedIndex = _plugin.SelectedProfile;
            _filling = false;
            return;
        }
        _plugin.SelectedProfile = _profiles.SelectedIndex;
        _plugin.Open(_plugin.Profiles[_plugin.SelectedProfile].SettingsFile);
        RefreshAll();
    }

    /// <summary>原版 _打开按钮_被单击 / _打开按钮m_被单击：手动选一个 pc_settings.bin。</summary>
    private async Task OpenOther()
    {
        if (_plugin.Dirty && !await _ui.ConfirmAsync("打开其他文件", "当前文件有未保存的修改，打开其他文件后会丢失。", "继续")) return;
        string? path = null;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".bin");
            picker.FileTypeFilter.Add("*");
            if (!InitPicker(picker)) throw new InvalidOperationException();
            path = (await picker.PickSingleFileAsync())?.Path;
            if (path is null) return;
        }
        catch (Exception)
        {
            // 取不到窗口句柄时退回手动输入路径
            path = await _ui.InputAsync("打开其他文件", "pc_settings.bin 的完整路径：", _plugin.FilePath);
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Trim().Trim('"');
        }
        if (!File.Exists(path))
        {
            await _ui.MessageAsync("打开其他文件", "未能找到文件！");
            return;
        }
        _plugin.Open(path);
        _plugin.SelectedProfile = _plugin.Profiles.FindIndex(p => p.SettingsFile.Equals(path, StringComparison.OrdinalIgnoreCase));
        FillProfiles();
        RefreshAll();
    }

    private async Task Reload()
    {
        if (_plugin.Dirty && !await _ui.ConfirmAsync("重新读取", "当前文件有未保存的修改，重新读取后会丢失。", "重新读取")) return;
        _plugin.Reload();
        RefreshAll();
    }

    private async Task Lock(bool locked)
    {
        if (_plugin.FilePath.Length == 0) return;
        await _ui.MessageAsync(locked ? "锁定" : "解锁", _plugin.SetReadOnly(locked));
        RefreshFile();
    }

    private void RefreshAll()
    {
        RefreshFile();
        RenderTab();
    }

    private void RefreshFile()
    {
        var path = _plugin.FilePath;
        if (path.Length == 0)
        {
            _fileInfo.Title = "没有选择存档";
            _fileInfo.Message = "没有找到存档目录。可以点「打开其他文件…」手动选择 pc_settings.bin。";
            _fileInfo.Severity = InfoBarSeverity.Warning;
            return;
        }
        if (_plugin.Doc is not { } doc)
        {
            _fileInfo.Title = "没有读到文件";
            _fileInfo.Message = $"{path}\n{_plugin.LoadError}";
            _fileInfo.Severity = InfoBarSeverity.Warning;
            return;
        }
        var state = new List<string>();
        try { if (_plugin.IsReadOnly) state.Add("已锁定（只读）"); }
        catch (Exception) { }
        if (_plugin.Dirty) state.Add("有未保存的修改");
        _fileInfo.Title = $"已读取 {doc.Count} 项" + (state.Count > 0 ? "，" + string.Join("，", state) : "");
        _fileInfo.Message = path;
        _fileInfo.Severity = _plugin.Dirty ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
    }

    private void Changed()
    {
        _plugin.Dirty = true;
        RefreshFile();
    }

    // ───────── 标签 ─────────

    private void RenderTab()
    {
        _content.Children.Clear();
        _tunnelInfo = null;
        _cloudInfo = null;
        _cloudLog = null;
        switch (_plugin.SelectedTab)
        {
            case 1: BuildFlags(); break;
            case 2: BuildTunnel(); break;
            case 3: BuildCloud(); break;
            case 4: BuildManual(); break;
            default: BuildSliders(); break;
        }
        RefreshLive();
    }

    private void Header(string title, string note = "")
    {
        var header = new TextBlock { Text = title, Margin = new Thickness(1, 20, 0, 6) };
        if (Application.Current.Resources.TryGetValue("SectionHeaderStyle", out var style) && style is Style s) header.Style = s;
        else header.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        if (_content.Children.Count == 0) header.Margin = new Thickness(1, 8, 0, 6);
        _content.Children.Add(header);
        if (note.Length == 0) return;
        _content.Children.Add(new TextBlock
        {
            Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 0, 0, 8),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
    }

    private void Add(UIElement element) => _content.Children.Add(element);

    /// <summary>需要先有文件的标签：没有文件时只显示提示，返回 null。</summary>
    private PcSettingsFile? RequireDoc()
    {
        if (_plugin.Doc is { } doc) return doc;
        Add(_ui.Info("请先打开文件", "在上面的列表里选一个存档，或者手动打开 pc_settings.bin。", 2));
        return null;
    }

    /// <summary>「传统编辑」的保存按钮和「游戏退出后保存」（原版 _保存按钮_被单击 / _自动保存_被单击）。</summary>
    private void AddClassicSave()
    {
        Header("保存");
        Add(_ui.Buttons("保存修改", "把这两个标签里的七项设置和两个角色的任务状态全部写入文件（文件里没有的项目会添加到末尾）。",
            ("保存", () => _ = SaveClassic())));
        Add(_ui.Toggle("游戏退出后保存", "游戏退出时会重写 pc_settings.bin。打开后，检测到游戏退出就自动保存一次这里的内容，再启动游戏即可生效。只在本次运行有效。",
            () => _plugin.SaveOnExit, v =>
            {
                _plugin.SetSaveOnExit(v);
                if (v) _host.Log("已打开「游戏退出后保存」：游戏退出后会自动保存 pc_settings.bin。");
            }, ""));
    }

    private async Task SaveClassic()
    {
        var error = _plugin.Save(classic: true);
        if (error is null)
        {
            _host.Log("已保存 " + _plugin.FilePath, PluginLogLevel.Success);
            await _ui.MessageAsync("保存", "文件保存成功！需要重新启动游戏来让文件生效。");
        }
        else if (_plugin.SaveOnExit && _plugin.GameRunning())
            await _ui.MessageAsync("保存", "游戏正在运行。已打开「游戏退出后保存」，游戏退出后会自动保存。");
        else await _ui.MessageAsync("保存", error);
        RefreshAll();
    }

    // ───────── 常用设置（原版「传统编辑」左半边的七个滑块） ─────────

    private void BuildSliders()
    {
        Header("常用设置", "右边的百分比是游戏设置菜单里显示的数值。文件里没有的项目按默认值显示，保存时添加。");
        if (RequireDoc() is not { } doc) return;
        foreach (var slider in PcLayout.Sliders) Add(SliderRow(doc, slider));
        AddClassicSave();
    }

    private FrameworkElement SliderRow(PcSettingsFile doc, PcSlider spec)
    {
        var value = PcLayout.ReadSlider(doc, spec);
        var percent = new TextBlock { MinWidth = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 255, StepFrequency = 1, Width = 200, Value = value, VerticalAlignment = VerticalAlignment.Center };
        var box = new NumberBox { Minimum = 0, Maximum = 255, Value = value, Width = 96, VerticalAlignment = VerticalAlignment.Center };
        percent.Text = PcLayout.PercentText(spec, value);
        var syncing = false;
        void Apply(int v)
        {
            if (syncing) return;
            syncing = true;
            slider.Value = v;
            box.Value = v;
            syncing = false;
            percent.Text = PcLayout.PercentText(spec, v);
            PcLayout.WriteSlider(doc, spec, v);
            Changed();
        }
        slider.ValueChanged += (_, e) => Apply((int)Math.Round(e.NewValue));
        box.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) box.Value = slider.Value;
            else Apply((int)Math.Clamp(Math.Round(e.NewValue), 0, 255));
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        panel.Children.Add(percent);
        panel.Children.Add(slider);
        panel.Children.Add(box);
        var description = spec.Description + (doc.Contains(spec.Id) ? "" : $" 文件里没有这一项，保存时按 {spec.Default} 添加。");
        return _ui.Row(spec.Title, description, panel, spec.Glyph);
    }

    // ───────── 任务状态（原版「传统编辑」右下的两个角色分组和首页的快捷任务） ─────────

    private static readonly string[] EarlyNames = ["无", "全福", "越狱", "人道"];
    private static readonly string[] LateNames = ["无", "首轮", "太平洋"];

    private void BuildFlags()
    {
        Header("角色任务状态", "差事面板上已经买下的抢劫。原版共有两个角色存档位置，分别对应游戏里的角色 1、角色 2。");
        if (RequireDoc() is { } doc)
        {
            Add(CharacterCard(doc, 1));
            Add(CharacterCard(doc, 2));
            AddClassicSave();
        }

        Header("快捷任务", "直接写入文件，两个角色都改。全福、越狱、人道同时把「重置越狱人道面板」设为否。游戏运行时不能用。");
        Add(_ui.Buttons("拥有抢劫", "",
            ("全福", () => _ = Quick("全福", f => PcLayout.QuickEarly(f, EarlyHeist.Fleeca))),
            ("越狱", () => _ = Quick("越狱", f => PcLayout.QuickEarly(f, EarlyHeist.PrisonBreak))),
            ("人道", () => _ = Quick("人道", f => PcLayout.QuickEarly(f, EarlyHeist.HumaneLabs))),
            ("首轮", () => _ = Quick("首轮", f => PcLayout.QuickLate(f, LateHeist.SeriesA))),
            ("太平洋", () => _ = Quick("太平洋", f => PcLayout.QuickLate(f, LateHeist.PacificStandard)))));
    }

    private FrameworkElement CharacterCard(PcSettingsFile doc, int c)
    {
        CharacterFlags Get() => PcLayout.ReadFlags(doc, c);
        void Update(Func<CharacterFlags, CharacterFlags> edit)
        {
            PcLayout.WriteFlags(doc, c, edit(Get()));
            Changed();
        }
        var flags = Get();
        // 文件里是原版不认识的值时多一个「其他」选项，选着它保存时不改这几个字节
        string[] early = flags.Early == EarlyHeist.Other ? [.. EarlyNames, "其他（保持原值）"] : EarlyNames;
        string[] late = flags.Late == LateHeist.Other ? [.. LateNames, "其他（保持原值）"] : LateNames;
        // 改成别的再改回「其他」时，把读到的原始字节写回去
        var earlyRaw = doc.GetBytes(PcLayout.RecordA(c), 1, 2);
        var lateRaw = doc.GetBytes(PcLayout.RecordB(c), 0, 3);
        var missing = doc.Contains(PcLayout.RecordA(c)) && doc.Contains(PcLayout.RecordB(c)) ? "" : "文件里还没有这个角色的记录，保存时添加。";
        return _ui.Expander($"角色 {c}", missing, "", null,
            _ui.Toggle("任务进行中", "", () => Get().InMission, v => Update(f => f with { InMission = v })),
            _ui.Choice("拥有的抢劫", "全福 / 越狱 / 人道。", early, () => (int)Get().Early,
                i =>
                {
                    if ((EarlyHeist)i == EarlyHeist.Other && earlyRaw is not null) doc.SetBytes(PcLayout.RecordA(c), 1, earlyRaw);
                    Update(f => f with { Early = (EarlyHeist)i });
                }, 180),
            _ui.Toggle("重置越狱人道面板", "下次启动游戏时重置公寓里的抢劫面板。", () => Get().ResetBoard, v => Update(f => f with { ResetBoard = v })),
            _ui.Choice("拥有的抢劫（首轮 / 太平洋）", "", late, () => (int)Get().Late,
                i =>
                {
                    if ((LateHeist)i == LateHeist.Other && lateRaw is not null) doc.SetBytes(PcLayout.RecordB(c), 0, lateRaw);
                    Update(f => f with { Late = (LateHeist)i });
                }, 180));
    }

    private async Task Quick(string name, Action<PcSettingsFile> edit)
    {
        if (_plugin.Dirty && !await _ui.ConfirmAsync("快捷任务", "快捷任务直接改文件，改完重新读取，当前未保存的修改会丢失。", "继续")) return;
        var error = _plugin.Quick(edit);
        if (error is null)
        {
            _host.Log($"快捷任务「拥有{name}」：角色 1、角色 2 已完成编辑。", PluginLogLevel.Success);
            await _ui.MessageAsync("快捷任务", $"操作记录：\n存档的角色 1 ...已完成编辑！\n存档的角色 2 ...已完成编辑！\n操作已全部完成！");
        }
        else await _ui.MessageAsync("快捷任务", error);
        RefreshAll();
    }

    // ───────── 名钻隧道防掉线（原版「隧道防掉」） ─────────

    private void BuildTunnel()
    {
        Header("名钻隧道防掉线",
            "增强版在下载下一段差事时，后台帧率很高会把加载超时缩短到不足 2 秒，名钻任务过隧道时容易因此掉线。" +
            "启动后盯着所选角色的「任务进行中」状态：变成「差事未载入」时锁 30 帧，让加载有足够时间；「载入已完成」后解除锁帧。" +
            "完成任务后请点停止，否则会一直锁帧。");
        _tunnelInfo = _ui.Info("", "");
        Add(_tunnelInfo);
        Add(_ui.Buttons("运行", "读的是上面选中的存档。", ("启动", () => { _plugin.StartTunnel(); RefreshLive(); }),
            ("停止", () => { _plugin.StopTunnel(); RefreshLive(); })));
        Add(_ui.Choice("角色", "当前在玩的角色。", ["角色 1", "角色 2"], () => _plugin.TunnelCharacter - 1, i => _plugin.TunnelCharacter = i + 1, 140));
        Add(_ui.Toggle("控制 RTSS 锁帧", "需要安装 RTSS（装微星小飞机时会一起装），并在「浮窗」页打开 RTSS 锁帧。停止时恢复为不限制。",
            () => _plugin.TunnelRtss, v => _plugin.TunnelRtss = v, ""));
        Add(_ui.Toggle("声音提示", "状态变化时播放提示音（锁帧成功时用主程序的锁帧提示音）。", () => _plugin.TunnelSound, v => _plugin.TunnelSound = v, ""));
    }

    // ───────── 监控云存档（原版「监控存档」） ─────────

    private void BuildCloud()
    {
        Header("监控云存档",
            "文件里 931、932 两项是上次线上云存档成功的时间，它一变就说明游戏刚存过档。读的是上面选中的存档。" +
            "主程序「网络」页的「云存档提示」也有同样的功能（自动跟随最近使用的存档）。");
        _cloudInfo = _ui.Info("", "");
        Add(_cloudInfo);
        Add(_ui.Buttons("运行", "", ("开始监控", () => { _plugin.StartCloud(); RefreshLive(); }),
            ("停止监控", () => { _plugin.StopCloud(); RefreshLive(); }),
            ("清空记录", _plugin.ClearCloudLog)));
        Add(_ui.Toggle("声音提示", "监测到保存成功时播放提示音。", () => _plugin.CloudSound, v => _plugin.CloudSound = v, ""));
        _cloudLog = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 240,
            FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 0),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_cloudLog, ScrollBarVisibility.Auto);
        Add(_cloudLog);
    }

    /// <summary>后台状态变化后刷新：防掉线红绿灯（原版 信号灯控制d）、云存档记录、保存状态。</summary>
    private void RefreshLive()
    {
        RefreshFile();
        if (_tunnelInfo is { } t)
        {
            if (!_plugin.TunnelRunning)
                (t.Title, t.Message, t.Severity) = ("未启动", "", InfoBarSeverity.Informational);
            else
                (t.Title, t.Message, t.Severity) = _plugin.TunnelState switch
                {
                    1 => ("载入已完成", "差事已载入，已解除锁帧。", InfoBarSeverity.Success),
                    -1 => ("差事未载入", "正在等待差事载入，已锁 30 帧。", InfoBarSeverity.Error),
                    _ => ("运行中", $"正在读取角色 {_plugin.TunnelCharacter} 的任务状态。", InfoBarSeverity.Informational),
                };
        }
        if (_cloudInfo is { } c)
            (c.Title, c.Severity) = _plugin.CloudRunning ? ("正在监控", InfoBarSeverity.Success) : ("未在监控", InfoBarSeverity.Informational);
        if (_cloudLog is { } log && log.Text != _plugin.CloudLogText)
        {
            log.Text = _plugin.CloudLogText;
            log.SelectionStart = log.Text.Length;
        }
    }

    // ───────── 手动编辑（原版「手动编辑」） ─────────

    private ListView? _records;
    private TextBox? _idBox, _valueBox;
    private RadioButtons? _radix;
    private int _radixIndex;

    private void BuildManual()
    {
        Add(_ui.Info("注意", "非专业人员请勿随意修改，否则可能导致游戏设置错乱、无法读取设置、闪退、无法启动或无法进入线上。" +
            "出现这些问题时，删除 pc_settings.bin 再启动游戏，游戏会重建这个文件（其他设置也会恢复默认）。", 2));
        if (RequireDoc() is not { } doc) return;

        Header("项目列表", "描述来自原版的对照表，仅供参考。改动先留在列表里，点「保存 bin」才写入文件。");
        _records = new ListView { Height = 360, SelectionMode = ListViewSelectionMode.Single };
        _records.SelectionChanged += (_, _) =>
        {
            // 原版 _手动列表框_左键单击表项：把这一项填到下面
            if (_records.SelectedItem is ListViewItem { Tag: int index } && index < doc.Count)
            {
                _idBox!.Text = doc.Records[index].Id.ToString();
                _valueBox!.Text = PcLayout.FormatValue(doc.Records[index].Value, _radixIndex);
            }
        };
        Add(Table(["项目 ID", "项目数值", "描述（仅供参考）"], [90, 120, -1], _records));
        FillRecords(doc);

        Header("编辑指定数值");
        _idBox = new TextBox { Width = 120, PlaceholderText = "ID" };
        var search = new AutoSuggestBox { Width = 260, PlaceholderText = "按名称查找 ID", QueryIcon = new SymbolIcon(Symbol.Find) };
        search.TextChanged += (s, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var key = s.Text.Trim();
            s.ItemsSource = key.Length == 0 ? null : SettingNames.All
                .Where(x => x.name.Contains(key, StringComparison.OrdinalIgnoreCase) || x.id.ToString() == key)
                .Take(50).Select(x => $"{x.id}  {x.name}").ToList();
        };
        search.SuggestionChosen += (s, e) =>
        {
            // 原版 _ID组合框_列表项被选择：选预设项目后填入 ID
            if (e.SelectedItem is string text && text.Split(' ')[0] is var id) _idBox.Text = id;
        };
        var idPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        idPanel.Children.Add(_idBox);
        idPanel.Children.Add(search);
        Add(_ui.Row("项目 ID", "", idPanel, ""));

        _valueBox = new TextBox { Width = 280, FontFamily = new FontFamily("Consolas"), PlaceholderText = "数值" };
        _radix = new RadioButtons { MaxColumns = 3, SelectedIndex = _radixIndex };
        foreach (var name in new[] { "十进制", "二进制", "十六进制" }) _radix.Items.Add(name);
        _radix.SelectionChanged += (_, _) =>
        {
            // 原版 _十进制单选框_被单击 等：按旧进制解析，再用新进制显示
            var old = _radixIndex;
            _radixIndex = Math.Max(0, _radix.SelectedIndex);
            if (PcLayout.TryParseValue(_valueBox.Text, old, out var v)) _valueBox.Text = PcLayout.FormatValue(v, _radixIndex);
        };
        var valuePanel = new StackPanel { Spacing = 4 };
        valuePanel.Children.Add(_valueBox);
        valuePanel.Children.Add(_radix);
        Add(_ui.Row("项目数值", "", valuePanel, ""));

        Add(_ui.Buttons("操作", "覆写时如果没有这一项，会添加到末尾。",
            ("寻找", () => _ = FindRecord(doc)),
            ("读取", () => _ = ReadRecord(doc)),
            ("覆写", () => _ = WriteRecord(doc)),
            ("删除", () => _ = DeleteRecord(doc))));
        Add(_ui.Buttons("文件", "保存 bin 按列表原样写入文件。",
            ("保存 bin", () => _ = SaveManual()),
            ("导出 CSV", () => _ = ExportCsv(doc))));
    }

    private void FillRecords(PcSettingsFile doc)
    {
        if (_records is null) return;
        _records.Items.Clear();
        for (var i = 0; i < doc.Count; i++)
        {
            var r = doc.Records[i];
            _records.Items.Add(new ListViewItem { Tag = i, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Cells([r.Id.ToString(), r.Value.ToString(), SettingNames.Describe(r.Id)], [90, 120, -1]) });
        }
    }

    private bool TryId(out int id)
    {
        id = 0;
        return _idBox is not null && int.TryParse(_idBox.Text.Trim(), out id);
    }

    private async Task FindRecord(PcSettingsFile doc)
    {
        if (!TryId(out var id) || doc.IndexOf(id) is var i && i < 0)
        {
            await _ui.MessageAsync("寻找", "未找到此 ID，你可以直接点击覆写，将自动添加到末尾。");
            return;
        }
        _records!.SelectedIndex = i;
        _records.ScrollIntoView(_records.Items[i]);
    }

    private async Task ReadRecord(PcSettingsFile doc)
    {
        if (!TryId(out var id) || doc.Get(id) is not { } value)
        {
            await _ui.MessageAsync("读取", "找不到要读取的表项！");
            return;
        }
        _valueBox!.Text = PcLayout.FormatValue(value, _radixIndex);
    }

    private async Task WriteRecord(PcSettingsFile doc)
    {
        if (!TryId(out var id) || !PcLayout.TryParseValue(_valueBox!.Text, _radixIndex, out var value))
        {
            await _ui.MessageAsync("覆写", "您填写的数值无效或溢出！无法保存！");
            return;
        }
        var existed = doc.Set(id, value);
        Changed();
        FillRecords(doc);
        var i = doc.IndexOf(id);
        _records!.SelectedIndex = i;
        _records.ScrollIntoView(_records.Items[i]);
        if (!existed) await _ui.MessageAsync("覆写", "找不到要覆写的表项，已添加到列表末尾！");
    }

    private async Task DeleteRecord(PcSettingsFile doc)
    {
        if (!TryId(out var id) || !doc.Remove(id))
        {
            await _ui.MessageAsync("删除", "找不到要删除的表项！");
            return;
        }
        Changed();
        FillRecords(doc);
    }

    private async Task SaveManual()
    {
        var error = _plugin.Save(classic: false);
        if (error is null) _host.Log("已保存 " + _plugin.FilePath, PluginLogLevel.Success);
        await _ui.MessageAsync("保存 bin", error ?? "保存成功！");
        RefreshFile();
    }

    /// <summary>原版 _导出按钮m_被单击：表头 + 每行「ID,数值,描述」。这里用带 BOM 的 UTF-8，Excel 能直接打开。</summary>
    private async Task ExportCsv(PcSettingsFile doc)
    {
        string? path;
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "pc_settings" };
            picker.FileTypeChoices.Add("CSV 文件", [".csv"]);
            if (!InitPicker(picker)) throw new InvalidOperationException();
            path = (await picker.PickSaveFileAsync())?.Path;
            if (path is null) return;
        }
        catch (Exception)
        {
            path = await _ui.InputAsync("导出 CSV", "保存到（完整路径）：",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "pc_settings.csv"));
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Trim().Trim('"');
        }
        var text = new StringBuilder("项目ID,项目数值,描述（仅供参考）");
        foreach (var r in doc.Records) text.Append("\r\n").Append(r.Id).Append(',').Append(r.Value).Append(',').Append(SettingNames.Describe(r.Id));
        try
        {
            await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(true));
            await _ui.MessageAsync("导出 CSV", "保存成功！");
        }
        catch (Exception ex) { await _ui.MessageAsync("导出 CSV", "保存失败！" + ex.Message); }
    }

    // ───────── 小工具 ─────────

    /// <summary>文件选择框需要主窗口句柄：从页面所在的 XamlRoot 取。</summary>
    private bool InitPicker(object picker)
    {
        var root = (_page.Element as FrameworkElement)?.XamlRoot;
        if (root?.ContentIslandEnvironment is not { } env) return false;
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(env.AppWindowId);
        if (hwnd == 0) return false;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return true;
    }

    /// <summary>一行固定列宽的文字；宽度 -1 表示占满剩余空间。</summary>
    private static Grid Cells(string[] texts, int[] widths)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < texts.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = widths[i] < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i]) });
            var cell = new TextBlock { Text = texts[i], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    /// <summary>带表头的列表，外面套一张卡片。</summary>
    private static Border Table(string[] headers, int[] widths, ListView list)
    {
        var header = Cells(headers, widths);
        header.Margin = new Thickness(16, 8, 16, 4);
        foreach (var child in header.Children.OfType<TextBlock>())
        {
            child.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            child.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(list);
        return new Border
        {
            Child = stack,
            Padding = new Thickness(0, 0, 0, 4),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
    }
}
