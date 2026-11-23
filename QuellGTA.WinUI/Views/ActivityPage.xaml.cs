using System.Collections.Specialized;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>活动：完整日志。</summary>
public sealed partial class ActivityPage : Page
{
    private static QuellCore Core => App.Core;

    public ActivityPage()
    {
        InitializeComponent();
        LogList.ItemsSource = Core.Log.Entries;
        NotifyCollectionChangedEventHandler handler = (_, e) =>
        {
            Refresh();
            if (e.Action == NotifyCollectionChangedAction.Add && Core.Log.Entries.Count > 0)
                LogList.ScrollIntoView(Core.Log.Entries[^1]);
        };
        Loaded += (_, _) =>
        {
            Core.Log.Entries.CollectionChanged += handler;
            Refresh();
            if (Core.Log.Entries.Count > 0) LogList.ScrollIntoView(Core.Log.Entries[^1]);
        };
        Unloaded += (_, _) => Core.Log.Entries.CollectionChanged -= handler;
    }

    private void Refresh() => EmptyText.Visibility = Core.Log.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ClearLog_Click(object sender, RoutedEventArgs e) => Core.Log.Entries.Clear();

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Core.Settings.DataDirectory, "Logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"QuellGTA_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(file, Core.Log.Export(), new UTF8Encoding(true));
            Shell.Open(file);
        }
        catch (Exception ex)
        {
            Core.Log.Write("保存日志失败：" + ex.Message, 3);
        }
    }
}
