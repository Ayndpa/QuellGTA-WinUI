using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Plugins;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>主程序提供给某一个插件的 IPluginHost。每个插件一份，日志、设置节、快捷键名都带上插件 id。</summary>
public sealed class PluginHost : IPluginHost, IPluginSettings, IPluginGame, IPluginState, IPluginHotkeys, IPluginInput, IPluginUi
{
    private static QuellCore Core => App.Core;
    private readonly string _section;

    public PluginHost(PluginInfo info, DispatcherQueue dispatcher)
    {
        Info = info;
        Dispatcher = dispatcher;
        _section = "插件." + info.Id;
    }

    public PluginInfo Info { get; }
    public Version HostVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    public DispatcherQueue Dispatcher { get; }
    public string HostDataDirectory => Core.Settings.DataDirectory;

    public string DataDirectory
    {
        get
        {
            var dir = Path.Combine(Core.Settings.DataDirectory, "Plugins", Info.Id);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public IPluginSettings Settings => this;
    public IPluginGame Game => this;
    public IPluginState State => this;
    public IPluginHotkeys Hotkeys => this;
    public IPluginInput Input => this;
    public IPluginUi Ui => this;

    public void Log(string text, PluginLogLevel level = PluginLogLevel.Info) => Core.Log.Write($"[{Info.Name}] {text}", (int)level);
    public bool RunCommand(string command) => Core.RunCommand(command.Trim().ToLowerInvariant());
    public void PlaySound(int sound) => Core.Sound.Play(sound);
    public bool SetFrameLimit(int fps) => Core.RtssLimiter && Core.ApplyFrameLimit(fps);

    // ───────── 设置 ─────────

    string IPluginSettings.Get(string key, string fallback) => Core.Settings.Get(_section, key, fallback);
    void IPluginSettings.Set(string key, string value) => Core.Settings.Set(_section, key, value);
    int IPluginSettings.GetInt(string key, int fallback) => Core.Settings.GetInt(_section, key, fallback);
    void IPluginSettings.SetInt(string key, int value) => Core.Settings.SetInt(_section, key, value);
    bool IPluginSettings.GetBool(string key, bool fallback) => Core.Settings.GetBool(_section, key, fallback);
    void IPluginSettings.SetBool(string key, bool value) => Core.Settings.SetBool(_section, key, value);

    // ───────── 游戏 ─────────

    string IPluginGame.ProcessName => Core.Game.ProcessName;
    int IPluginGame.Edition => (int)Core.Game.Edition;
    string IPluginGame.DocumentsDirectory => Core.Files.DocumentsDir;
    string IPluginGame.ProfilesDirectory => Core.Files.ProfilesDir;
    string IPluginGame.InstallDirectory => Core.Files.InstallDir;
    bool IPluginGame.IsRunning => Core.GameRunning;
    bool IPluginGame.IsForeground => Core.Game.IsForeground();
    nint IPluginGame.WindowHandle => Core.Game.GetWindowHandle();
    void IPluginGame.BringToFront() => Core.Game.BringToFront();

    event Action? IPluginGame.Started
    {
        add => Core.GameStarted += value;
        remove => Core.GameStarted -= value;
    }

    event Action? IPluginGame.Exited
    {
        add => Core.GameExited += value;
        remove => Core.GameExited -= value;
    }

    // ───────── 状态 ─────────

    bool IPluginState.NetworkBlocked => Core.NetBlocked;
    bool IPluginState.NicDisabled => Core.NicDisabled;
    bool IPluginState.SoloActive => Core.SoloActive;
    long IPluginState.TimerMilliseconds => Core.Overlay.TimerMilliseconds;
    bool IPluginState.TimerRunning => Core.Overlay.TimerState == TimerState.Running;
    bool IPluginState.UdpEnabled => Core.UdpEnabled;
    int IPluginState.UdpPort => Core.UdpPort;

    event Action? IPluginState.Changed
    {
        add => Core.StateChanged += value;
        remove => Core.StateChanged -= value;
    }

    // ───────── 快捷键 ─────────

    private string HotkeyName(string key) => $"{QuellCore.PluginHotkeyPrefix}{Info.Id}:{key}";

    void IPluginHotkeys.Register(string key, string title, string description, string glyph, Action<bool> handler)
    {
        var name = HotkeyName(key);
        Shortcuts.Register(new Shortcuts.Info(name, title, description, string.IsNullOrEmpty(glyph) ? Info.Glyph : glyph));
        Core.RegisterPluginHotkey(name, down =>
        {
            try { handler(down); }
            catch (Exception ex) { Log($"快捷键「{title}」出错：{ex.Message}", PluginLogLevel.Error); }
        });
    }

    // ───────── 输入 ─────────

    void IPluginInput.Tap(int virtualKey, int holdMs) => InputSim.KeyPress(virtualKey, holdMs);
    void IPluginInput.KeyDown(int virtualKey) => InputSim.KeyDown(virtualKey);
    void IPluginInput.KeyUp(int virtualKey) => InputSim.KeyUp(virtualKey);
    bool IPluginInput.IsDown(int virtualKey) => InputSim.IsDown(virtualKey);
    long IPluginInput.IdleMilliseconds => InputSim.IdleMilliseconds();

    // ───────── 界面 ─────────

    private static FrameworkElement Owner => (FrameworkElement)App.Window.Content;

    IPluginPage IPluginUi.CreatePage(string title, string description) => new PluginPageView(title, description);
    FrameworkElement IPluginUi.Row(string title, string description, UIElement? content, string? glyph) => Views.Ui.Row(title, description, content, glyph);
    FrameworkElement IPluginUi.Toggle(string title, string description, Func<bool> get, Action<bool> set, string? glyph) =>
        Views.Ui.Toggle(title, description, get, set, glyph);
    FrameworkElement IPluginUi.Number(string title, string description, double min, double max, Func<double> get, Action<double> set, double step) =>
        Views.Ui.Number(title, description, min, max, get, set, step);
    FrameworkElement IPluginUi.Text(string title, string description, Func<string> get, Action<string> set, string placeholder, double width) =>
        Views.Ui.Text(title, description, get, set, placeholder, width);
    FrameworkElement IPluginUi.Choice(string title, string description, string[] options, Func<int> get, Action<int> set, double width) =>
        Views.Ui.Choice(title, description, options, get, set, width);
    FrameworkElement IPluginUi.Buttons(string title, string description, params (string text, Action click)[] buttons) =>
        Views.Ui.Buttons(title, description, buttons);
    FrameworkElement IPluginUi.Expander(string title, string description, string? glyph, UIElement? header, params UIElement[] items) =>
        Views.Ui.Expander(title, description, glyph, header, items);
    InfoBar IPluginUi.Info(string title, string message, int severity) => Views.Ui.Info(title, message, severity switch
    {
        1 => InfoBarSeverity.Success,
        2 => InfoBarSeverity.Warning,
        3 => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    });
    FrameworkElement IPluginUi.Key(string title, string description, Func<int> get, Action<int> set, string? glyph) =>
        Views.Ui.Key(Owner, title, description, get, set, glyph);
    FrameworkElement IPluginUi.Shortcut(string key) => Views.Ui.Shortcut(Owner, HotkeyName(key));

    Task IPluginUi.MessageAsync(string title, string message) => Dialogs.MessageAsync(Owner, title, message);
    Task<bool> IPluginUi.ConfirmAsync(string title, string message, string confirm) => Dialogs.ConfirmAsync(Owner, title, message, confirm);

    async Task<string?> IPluginUi.InputAsync(string title, string message, string initial, string placeholder)
    {
        var box = new TextBox { Text = initial, PlaceholderText = placeholder, IsSpellCheckEnabled = false };
        var panel = new StackPanel { Spacing = 12 };
        if (message.Length > 0) panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = title, Content = panel, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        return await Dialogs.CustomAsync(Owner, dialog) == ContentDialogResult.Primary ? box.Text : null;
    }
}

/// <summary>插件的标准页面：和主程序其他页面同样的布局。</summary>
public sealed partial class PluginPageView : FeaturePage, IPluginPage
{
    public PluginPageView(string title, string description) : base(title, description)
    {
        Loaded += (_, _) => Shown?.Invoke();
        Unloaded += (_, _) => Hidden?.Invoke();
    }

    public UIElement Element => this;
    public event Action? Shown;
    public event Action? Hidden;
    void IPluginPage.Section(string title, string note) => Section(title, note);
    void IPluginPage.Add(UIElement element) => Add(element);
}
