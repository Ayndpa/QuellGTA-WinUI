using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Plugins;

namespace QuellGTA.Plugin.EnsureStart;

/// <summary>
/// 确保启动（原版是一个独立的小程序，「启动 Grand Theft Auto V Enhanced」）：
/// 打开平台的启动链接，然后每秒找一次游戏窗口（类名 sgaWindow 或 grcWindow，标题 Grand Theft Auto V），
/// 倒计时结束还没找到就再打开一次启动链接、重新倒计时，直到窗口出现或者你点停止。
/// 原版只支持 Steam 增强版、固定等 300 秒；它的 grcWindow 判断写成了「句柄 == -1」，实际上只认 sgaWindow，这里两种都认。
/// </summary>
public sealed class EnsureStartPlugin : IQuellPlugin
{
    private static readonly (string name, string url)[] Platforms =
    [
        ("Steam 增强版", "steam://rungameid/3240220"),
        ("Epic 增强版", "com.epicgames.launcher://apps/b0cd075465c44f87be3b505ac04a2e46%3A122e5e90b7b8424d930be8bc1a7e05fb%3A8769e24080ea413b8ebca3f1b8c50951?action=launch"),
        ("Steam 传承版", "steam://rungameid/271590"),
        ("Epic 传承版", "com.epicgames.launcher://apps/0584d2013f0149a791e7b9bad0eec102%3A6e563a2c0f5f46e3b4e88b5f4ed50cca%3A9d2d0eb64d5c44529cece33fe2a46482?action=launch"),
    ];

    private IPluginHost _host = null!;
    private CancellationTokenSource? _cts;
    private volatile string _status = "";

    public event Action? StatusChanged;

    private int Platform
    {
        get => Math.Clamp(_host.Settings.GetInt("平台", 0), 0, Platforms.Length - 1);
        set => _host.Settings.SetInt("平台", value);
    }
    /// <summary>每次等待窗口的秒数，原版 300。</summary>
    private int TimeoutSeconds
    {
        get => Math.Clamp(_host.Settings.GetInt("等待秒数", 300), 30, 1800);
        set => _host.Settings.SetInt("等待秒数", Math.Clamp(value, 30, 1800));
    }
    private int MaxAttempts
    {
        get => Math.Clamp(_host.Settings.GetInt("最多次数", 5), 1, 50);
        set => _host.Settings.SetInt("最多次数", Math.Clamp(value, 1, 50));
    }

    public bool Running => _cts is not null;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _host.Hotkeys.Register("start", "确保启动游戏", "开始（或停止）确保启动。", "", down =>
        {
            if (!down) _host.Dispatcher.TryEnqueue(Toggle);
        });
    }

    private void SetStatus(string text)
    {
        _status = text;
        StatusChanged?.Invoke();
    }

    public void Toggle()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts = null;
            SetStatus("已停止。");
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        var (name, url) = Platforms[Platform];
        var timeout = TimeoutSeconds;
        var attempts = MaxAttempts;
        Task.Run(async () =>
        {
            try
            {
                for (var attempt = 1; attempt <= attempts; attempt++)
                {
                    if (FindGameWindow() != 0) { SetStatus("游戏窗口已经在了。"); return; }
                    Launch(url);
                    _host.Log($"第 {attempt} 次启动{name}。");
                    for (var left = timeout; left > 0; left--)
                    {
                        SetStatus($"第 {attempt} 次启动，等待游戏窗口… {left} 秒");
                        await Task.Delay(1000, cts.Token);
                        if (FindGameWindow() == 0) continue;
                        SetStatus("游戏启动成功！");
                        _host.Log("游戏窗口已出现，启动成功。", PluginLogLevel.Success);
                        return;
                    }
                }
                SetStatus($"试了 {attempts} 次，游戏窗口一直没有出现。");
                _host.Log(_status, PluginLogLevel.Warning);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_cts, cts)) _cts = null;
                StatusChanged?.Invoke();
            }
        });
        StatusChanged?.Invoke();
    }

    /// <summary>用 explorer 打开链接，让游戏平台不继承本程序的管理员权限（和主程序的「启动游戏」一样）。</summary>
    private static void Launch(string url)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string? title);

    /// <summary>增强版窗口类名 sgaWindow，传承版 grcWindow。</summary>
    private static nint FindGameWindow()
    {
        var hwnd = FindWindowW("sgaWindow", "Grand Theft Auto V");
        return hwnd != 0 ? hwnd : FindWindowW("grcWindow", "Grand Theft Auto V");
    }

    public UIElement? CreatePage()
    {
        var ui = _host.Ui;
        var page = ui.CreatePage("确保启动", "打开游戏平台的启动链接后一直等到游戏窗口出现。超时还没出现就再启动一次，直到成功或达到次数上限。");
        var button = new Button { MinWidth = 120, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        button.Click += (_, _) => Toggle();
        var state = ui.Info("", "");
        void Refresh()
        {
            button.Content = Running ? "停止" : "开始";
            state.Title = Running ? "正在确保启动" : "未在运行";
            state.Severity = Running ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            state.Message = _status;
        }
        Action handler = () => _host.Dispatcher.TryEnqueue(Refresh);
        page.Shown += () => { StatusChanged -= handler; StatusChanged += handler; Refresh(); };
        page.Hidden += () => StatusChanged -= handler;
        page.Add(state);
        page.Add(ui.Row("启动游戏并等待窗口出现", "按下面选的平台和版本启动。", button, ""));
        page.Add(ui.Shortcut("start"));
        page.Section("选项");
        page.Add(ui.Choice("平台和版本", "", Platforms.Select(p => p.name).ToArray(), () => Platform, i => { Platform = i; Refresh(); }, 180));
        page.Add(ui.Number("每次等待（秒）", "游戏窗口多久没出现就重新启动，原版 300。", 30, 1800, () => TimeoutSeconds, v => TimeoutSeconds = (int)v, 30));
        page.Add(ui.Number("最多启动几次", "原版不设上限，一直重试。", 1, 50, () => MaxAttempts, v => MaxAttempts = (int)v));
        return page.Element;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
    }
}
