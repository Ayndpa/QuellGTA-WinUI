using System.Reflection;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class AboutPage : Page
{
    private static QuellCore Core => App.Core;

    public AboutPage()
    {
        InitializeComponent();
        Loaded += (_, _) => OnShown();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"WinUI 3 移植版 · 基于原版 build 0216（V4.22.216）· 程序版本 {version?.ToString(3)} · GPL-3.0";
        AboutText.Text = ReadAsset("About.txt");
        DeclarationText.Text = ReadAsset("Declaration.txt");
    }

    /// <summary>页面每次显示时刷新（页面由主窗口直接创建，不经过 Frame 导航）。</summary>
    private void OnShown() => Refresh();

    private static string ReadAsset(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : "";
    }

    private void Refresh()
    {
        KillCountText.Text = Core.KillCount.ToString();
        CutCountText.Text = Core.CutCount.ToString();
        SoloCountText.Text = Core.SoloCount.ToString();
        static string Hours(TimeSpan time) => time.TotalHours >= 1 ? $"{time.TotalHours:0.0} 小时" : $"{time.TotalMinutes:0} 分钟";
        TimeText.Text = $"软件使用时长 {Hours(Core.UsageTime)}　游戏运行时长 {Hours(Core.GameTime)}";
    }

    private void License_Click(object sender, RoutedEventArgs e)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        if (File.Exists(file)) Shell.Open(file);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Core.ResetStatistics();
        Core.ResetTimeStatistics();
        Refresh();
    }
}
