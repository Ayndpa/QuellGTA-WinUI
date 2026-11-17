using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

/// <summary>统一的确认框 / 提示框（原版的「信息框」和「弹窗通知」）。</summary>
public static class Dialogs
{
    private static bool _open; // 同一时间只能显示一个 ContentDialog

    private static async Task<ContentDialogResult> ShowAsync(FrameworkElement owner, ContentDialog dialog)
    {
        if (_open || owner.XamlRoot is null) return ContentDialogResult.None;
        _open = true;
        try
        {
            dialog.XamlRoot = owner.XamlRoot;
            return await dialog.ShowAsync();
        }
        finally { _open = false; }
    }

    public static async Task<bool> ConfirmAsync(FrameworkElement owner, string title, string message, string confirm = "继续")
    {
        var result = await ShowAsync(owner, new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        });
        return result == ContentDialogResult.Primary;
    }

    public static Task MessageAsync(FrameworkElement owner, string title, string message) =>
        ShowAsync(owner, new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                MaxHeight = 420,
            },
            CloseButtonText = "好",
        });

    public static Task<ContentDialogResult> CustomAsync(FrameworkElement owner, ContentDialog dialog) =>
        ShowAsync(owner, dialog);

    /// <summary>确认 → 在后台执行文件操作 → 显示结果。大部分修复类功能都是这个流程。</summary>
    public static async Task RunAsync(FrameworkElement owner, string title, string confirmMessage, Func<FileResult> action)
    {
        if (!await ConfirmAsync(owner, title, confirmMessage)) return;
        FileResult result;
        try { result = await Task.Run(action); }
        catch (Exception ex) { result = new FileResult(false, "操作出错：" + ex.Message); }
        await MessageAsync(owner, result.Ok ? "操作完成" : "操作未完成", result.Message);
    }
}
