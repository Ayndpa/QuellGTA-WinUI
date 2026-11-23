using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using QuellGTA.Services;
using QuellGTA.Views;
using WinRT.Interop;

namespace QuellGTA;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    private readonly DispatcherQueueTimer _toastTimer;
    private readonly TrayService _tray;
    private readonly nint _hwnd;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        // 默认 1100×760（按 DPI 缩放），不超过屏幕工作区，并居中显示
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)(1100 * scale), work.Width - 40);
        var height = Math.Min((int)(760 * scale), work.Height - 40);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(4);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => Toast.IsOpen = false;

        _tray = new TrayService(_hwnd) { BuildMenu = BuildTrayMenu };
        _tray.Activated += ShowWindow;
        _tray.MenuInvoked += OnTrayMenu;
        ApplyTraySetting();

        App.Core.Log.Important += ShowToast;
        App.Core.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateTitle);
        App.Core.Team.Changed += () => DispatcherQueue.TryEnqueue(UpdateTitle);
        App.Core.AutoHack.Changed += () => DispatcherQueue.TryEnqueue(UpdateTitle);
        App.Core.ExitRequested += () => DispatcherQueue.TryEnqueue(Exit);
        App.Core.RestartRequested += () => DispatcherQueue.TryEnqueue(Restart);
        App.Core.FrameLimitInputRequested += () => DispatcherQueue.TryEnqueue(AskFrameLimit);
        App.Core.Bots.CefDebuggingNeeded += () => DispatcherQueue.TryEnqueue(AskCefDebugging);
        if (App.Core.Updates.CheckOnStartup) _ = App.Core.Updates.CheckAsync();
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            _tray.Dispose();
            App.Core.Dispose();
        };

        UpdateTitle();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    // ───────── 托盘 ─────────

    public void ApplyTraySetting()
    {
        if (App.Core.TrayIcon) _tray.Show(); else _tray.Hide();
    }

    /// <summary>从托盘恢复主窗口。</summary>
    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        SetForegroundWindow(_hwnd);
    }

    /// <summary>启动时不显示窗口：有托盘图标就完全隐藏，否则最小化到任务栏。</summary>
    public void StartHidden()
    {
        if (App.Core.TrayIcon) return; // 窗口从未显示过，等用户点托盘图标
        Activate();
        (AppWindow.Presenter as OverlappedPresenter)?.Minimize();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting || !App.Core.TrayIcon || !App.Core.CloseToTray) return;
        args.Cancel = true;
        sender.Hide();
    }

    /// <summary>重启程序：读取配置存档之后需要（原版「重启QuellGTA」）。</summary>
    public void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            // 等自己退出（释放单实例锁）之后再启动新的。
            // 不能用 cmd 的 timeout：没有控制台时它立刻报错退出，新实例会在旧实例还没退出时启动，被单实例锁挡回去
            Shell.PowerShell($"Wait-Process -Id {Environment.ProcessId} -Timeout 30 -ErrorAction SilentlyContinue; Start-Process -FilePath '{exe.Replace("'", "''")}'", wait: false);
        }
        Exit();
    }

    private bool _askingFrameLimit;

    /// <summary>RTSS_手动输入：锁帧键设成「手动输入」时，弹出主窗口让你输入目标帧数。</summary>
    private async void AskFrameLimit()
    {
        if (_askingFrameLimit || Content is not FrameworkElement owner) return;
        _askingFrameLimit = true;
        try
        {
            ShowWindow();
            var current = App.Core.RtssCurrentLimit;
            var box = new NumberBox
            {
                Minimum = 0, Maximum = 999, SmallChange = 10, LargeChange = 30,
                Value = current >= 0 ? current : double.NaN,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                PlaceholderText = "留空不修改",
            };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = Lang.C(449), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(box);
            var dialog = new ContentDialog
            {
                Title = "RTSS 锁帧",
                Content = panel,
                PrimaryButtonText = "应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await Views.Dialogs.CustomAsync(owner, dialog) != ContentDialogResult.Primary || double.IsNaN(box.Value)) return;
            var fps = (int)Math.Round(box.Value);
            if (fps is < 0 or > 999) { await Views.Dialogs.MessageAsync(owner, "RTSS 锁帧", Lang.C(409)); return; }
            await Task.Run(() => App.Core.ApplyFrameLimit(fps));
        }
        finally { _askingFrameLimit = false; }
    }

    /// <summary>按 Steam 好友加入时连不上 Steam：问一下要不要开启它的 CEF 远程调试（要重启 Steam）。</summary>
    private async void AskCefDebugging()
    {
        if (_askingCef || Content is not FrameworkElement owner) return;
        _askingCef = true;
        try
        {
            ShowWindow();
            if (!await Views.Dialogs.ConfirmAsync(owner, "开启 Steam 的 CEF 远程调试", Lang.C(556), "开启并重启 Steam")) return;
            var ok = await Task.Run(SteamFriendService.EnableCefDebugging);
            App.Core.Log.Write(ok ? "已开启 Steam 的 CEF 远程调试，Steam 正在重启。等它启动完成后再试一次。" : "开启失败：没有找到正在运行的 Steam。", ok ? 1 : 2);
        }
        catch (Exception) { }
        finally { _askingCef = false; }
    }

    private bool _askingCef;

    public void Exit()
    {
        _exiting = true;
        Close();
    }

    private IEnumerable<(int id, string? text, bool isChecked)> BuildTrayMenu()
    {
        var core = App.Core;
        yield return (1, "打开 QuellGTA", false);
        yield return (0, null, false);
        yield return (2, "杀掉进程", false);
        yield return (3, "阻止联网", core.NetBlocked);
        yield return (4, "禁用网卡", core.NicDisabled);
        yield return (5, "恢复网络", false);
        yield return (6, core.SoloActive ? "结束卡单" : "战局卡单", core.SoloActive);
        yield return (0, null, false);
        yield return (7, "重启热键引擎", false);
        yield return (0, null, false);
        yield return (9, "退出 QuellGTA", false);
    }

    private void OnTrayMenu(int id)
    {
        var core = App.Core;
        switch (id)
        {
            case 1: ShowWindow(); break;
            case 2: Task.Run(core.Kill); break;
            case 3: Task.Run(core.BlockNetwork); break;
            case 4: Task.Run(core.DisableNic); break;
            case 5: Task.Run(core.RestoreNetwork); break;
            case 6: Task.Run(core.ToggleSolo); break;
            case 7:
                core.Hotkeys.Restart();
                core.Log.Write(core.Hotkeys.IsRunning ? "热键引擎已重启。" : "热键引擎启动失败。", core.Hotkeys.IsRunning ? 1 : 3);
                break;
            case 9: Exit(); break;
        }
    }

    // ───────── 标题栏状态与提示 ─────────

    private void UpdateTitle()
    {
        var core = App.Core;
        var parts = new List<string> { core.GameRunning ? "游戏运行中" : "游戏未运行" };
        if (core.NetBlocked) parts.Add("已阻止联网");
        if (core.NicDisabled) parts.Add("已禁用网卡");
        if (core.SoloActive) parts.Add("卡单中");
        if (core.Team.Mode == TeamMode.Host) parts.Add("房主");
        if (core.Team.Mode == TeamMode.Client) parts.Add("已联机");
        TitleStatus.Text = string.Join(" · ", parts);

        // 导航上的状态角标：有东西正在生效时提醒一下
        NetworkItem.InfoBadge = core.NetBlocked || core.NicDisabled ? new InfoBadge { Style = (Style)Application.Current.Resources["CriticalIconInfoBadgeStyle"] } : null;
        SessionItem.InfoBadge = core.SoloActive ? new InfoBadge { Style = (Style)Application.Current.Resources["CautionIconInfoBadgeStyle"] } : null;
        TeamItem.InfoBadge = core.Team.Mode is TeamMode.Host or TeamMode.Client ? new InfoBadge { Style = (Style)Application.Current.Resources["SuccessDotInfoBadgeStyle"] } : null;
        AfkItem.InfoBadge = core.AfkActive ? new InfoBadge { Style = (Style)Application.Current.Resources["AttentionDotInfoBadgeStyle"] } : null;
        HackItem.InfoBadge = core.AutoHack.Running ? new InfoBadge { Style = (Style)Application.Current.Resources["AttentionDotInfoBadgeStyle"] } : null;
    }

    /// <summary>重要日志（等级 ≥ 2）：窗口可见时在底部弹出提示条，隐藏在托盘时改用气泡提示。</summary>
    private void ShowToast(LogEntry entry)
    {
        if (!AppWindow.IsVisible)
        {
            if (App.Core.TrayNotifications) _tray.Balloon("QuellGTA", entry.Text);
            return;
        }
        Toast.Severity = entry.Level >= 3 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        Toast.Message = entry.Text;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ───────── 导航 ─────────

    /// <summary>跳到指定页面（主页上的快捷入口用）。</summary>
    public void NavigateTo(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        if (tag.StartsWith("plugin:", StringComparison.Ordinal))
        {
            ShowPlugin(tag["plugin:".Length..]);
            return;
        }
        var page = tag switch
        {
            "network" => typeof(NetworkPage),
            "session" => typeof(SessionPage),
            "team" => typeof(TeamPage),
            "bot" => typeof(BotPage),
            "macros" => typeof(MacroPage),
            "afk" => typeof(AfkPage),
            "hack" => typeof(HackPage),
            "overlay" => typeof(OverlayPage),
            "game" => typeof(GamePage),
            "repair" => typeof(RepairPage),
            "migrate" => typeof(MigratePage),
            "totp" => typeof(TotpPage),
            "plugins" => typeof(PluginsPage),
            "activity" => typeof(ActivityPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(DashboardPage),
        };
        Show(page);
    }

    /// <summary>
    /// 显示页面。不用 Frame.Navigate：用代码写的页面（没有 XAML）没有 XAML 类型信息，Navigate 会直接崩溃。
    /// 每次都新建页面实例，页面打开时读到的就是最新设置；切换动画由 Frame 的内容过渡负责。
    /// </summary>
    private void Show(Type page, bool force = false)
    {
        if (!force && ContentFrame.Content?.GetType() == page) return;
        ContentFrame.Content = (UIElement)Activator.CreateInstance(page)!;
    }

    /// <summary>导航栏里加上已加载、并且有页面的插件（启动时调用一次）。</summary>
    public void AddPluginItems()
    {
        var withPages = App.Core.Plugins.Plugins.Where(p => p.Loaded).ToList();
        if (withPages.Count == 0) return;
        Nav.MenuItems.Add(new NavigationViewItemHeader { Content = "插件" });
        foreach (var entry in withPages)
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = entry.Info.Name,
                Tag = "plugin:" + entry.Info.Id,
                Icon = new FontIcon { Glyph = entry.Info.Glyph },
            });
    }

    /// <summary>显示插件页面。插件出错时显示错误，不影响主程序。</summary>
    private void ShowPlugin(string id)
    {
        var entry = App.Core.Plugins.Plugins.FirstOrDefault(p => p.Info.Id == id);
        UIElement? page = null;
        string? error = null;
        try { page = entry?.Instance?.CreatePage(); }
        catch (Exception ex) { error = ex.Message; }
        if (page is null)
        {
            var info = new InfoBar
            {
                IsOpen = true, IsClosable = false, Margin = new Thickness(28),
                Severity = error is null ? InfoBarSeverity.Informational : InfoBarSeverity.Error,
                Title = entry?.Info.Name ?? id,
                Message = error is null ? "这个插件没有页面。" : "插件页面出错：" + error,
            };
            page = info;
        }
        ContentFrame.Content = page;
    }

    /// <summary>重新加载当前页面（页面上的状态被别处改了之后用）。</summary>
    public void ReloadPage()
    {
        if (Nav.SelectedItem is NavigationViewItem { Tag: string tag } && tag.StartsWith("plugin:", StringComparison.Ordinal))
            ShowPlugin(tag["plugin:".Length..]);
        else if (ContentFrame.Content is not null) Show(ContentFrame.Content.GetType(), force: true);
    }
}
