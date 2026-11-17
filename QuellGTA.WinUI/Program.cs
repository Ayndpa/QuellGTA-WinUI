using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using QuellGTA.Services;

namespace QuellGTA;

/// <summary>
/// 程序入口。带 guard 参数启动时只当守护进程用（见 GuardService），不加载界面；否则正常启动 WinUI。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("guard", StringComparison.OrdinalIgnoreCase))
            return GuardService.RunGuard(args);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}
