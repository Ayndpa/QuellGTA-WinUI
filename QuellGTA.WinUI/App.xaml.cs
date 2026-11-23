using Microsoft.UI.Xaml;
using QuellGTA.Services;

namespace QuellGTA;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    public static QuellCore Core { get; private set; } = null!;
    public static MainWindow Window { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            try { Core?.Log.Write("未处理的异常：" + e.Message, 3); } catch (Exception) { }
            try
            {
                // 留一份完整的异常信息，方便排查界面线程上的崩溃
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MageAngela", "QuellGTA");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Message}\r\n{e.Exception}\r\n\r\n");
            }
            catch (Exception) { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 原版默认不允许重复运行：两个实例会同时响应热键。
        _singleInstance = new Mutex(true, @"Global\QuellGTA.WinUI", out var createdNew);
        if (!createdNew)
        {
            Exit();
            return;
        }

        Core = new QuellCore();
        Window = new MainWindow();
        Core.Log.Attach(Window.DispatcherQueue);
        // 插件：先只读 plugin.json，启用了的才加载
        Core.Plugins.Discover();
        Core.Plugins.LoadEnabled(info => new Views.PluginHost(info, Window.DispatcherQueue));
        Window.AddPluginItems();
        // 「启动之后最小化」，或者由开机自启的计划任务带 minimized 参数启动
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        Core.HandleCommandLine(arguments);
        var hidden = !Core.ShowRequested
                     && (Core.StartMinimized || arguments.Any(a => a.Equals("minimized", StringComparison.OrdinalIgnoreCase)));
        if (hidden) Window.StartHidden(); else Window.Activate();
        Core.Guard.AttachUi(Window.DispatcherQueue);
        Core.Hotkeys.Start();
        Core.Log.Write($"QuellGTA 已启动，当前游戏版本：{Core.Game.ProcessName}");
        if (!Core.Hotkeys.IsRunning) Core.Log.Write("热键引擎启动失败，全局热键不可用。", 3);
    }
}
