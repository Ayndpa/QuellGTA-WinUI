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
        App.Core.ExitRequested += () => DispatcherQueue.TryEnqueue(Exit);
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
            // 等自己退出（释放单实例锁）之后再启动新的
            Shell.Run("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"", wait: false);
        }
        Exit();
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

    private void Exit()
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
        var page = tag switch
        {
            "network" => typeof(NetworkPage),
            "session" => typeof(SessionPage),
            "team" => typeof(TeamPage),
            "bot" => typeof(BotPage),
            "macros" => typeof(MacroPage),
            "overlay" => typeof(OverlayPage),
            "game" => typeof(GamePage),
            "repair" => typeof(RepairPage),
            "migrate" => typeof(MigratePage),
            "totp" => typeof(TotpPage),
            "hotkeys" => typeof(HotkeysPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(HomePage),
        };
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }
}
