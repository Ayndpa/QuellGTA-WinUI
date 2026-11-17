using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuellGTA.Services;
using Windows.ApplicationModel.DataTransfer;

namespace QuellGTA.Views;

public sealed partial class TotpPage : Page
{
    private static TotpService Totp => App.Core.Totp;
    private readonly DispatcherQueueTimer _clock;
    private readonly List<(TotpToken token, Button code)> _rows = [];
    private long _lastStep = -1;

    public TotpPage()
    {
        InitializeComponent();
        BuildExtras();
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(500);
        _clock.Tick += (_, _) => Tick();
        Loaded += (_, _) => { Rebuild(); _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
    }

    private void Rebuild()
    {
        ErrorBar.IsOpen = Totp.LoadError is not null;
        ErrorBar.Message = Totp.LoadError ?? "";

        TokenPanel.Children.Clear();
        _rows.Clear();
        foreach (var token in Totp.Tokens)
        {
            var code = new Button
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 22, MinWidth = 150,
            };
            ToolTipService.SetToolTip(code, "点击复制");
            code.Click += (_, _) => Copy(token);
            var remove = new Button { Content = "删除" };
            remove.Click += async (_, _) =>
            {
                if (!await Dialogs.ConfirmAsync(this, "删除令牌", $"删除「{token.Name}」后无法恢复。请确认账号已经关闭两步验证，或者你保存了备用恢复码。", "删除"))
                    return;
                Totp.Remove(token);
                Rebuild();
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var migrate = new Button { Content = "迁移" };
            migrate.Click += async (_, _) => await MigrateAsync(token);
            actions.Children.Add(code);
            actions.Children.Add(migrate);
            actions.Children.Add(remove);
            TokenPanel.Children.Add(new SettingRow { Title = token.Name, Action = actions });
            _rows.Add((token, code));
        }
        EmptyText.Visibility = Totp.Tokens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Countdown.Visibility = Totp.Tokens.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _lastStep = -1;
        Tick();
    }

    private void Tick()
    {
        var now = Totp.Now;
        Countdown.Value = TotpService.SecondsRemaining(now);
        var step = now.ToUnixTimeSeconds() / 30;
        if (step == _lastStep) return;
        _lastStep = step;
        foreach (var (token, code) in _rows)
        {
            var text = TotpService.Code(token.Secret, now);
            code.Content = text[..3] + " " + text[3..];
        }
    }

    private static void Copy(TotpToken token)
    {
        var package = new DataPackage();
        package.SetText(TotpService.Code(token.Secret, Totp.Now));
        Clipboard.SetContent(package);
        App.Core.Log.Write($"已复制令牌「{token.Name}」的验证码。", 2);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "名称", PlaceholderText = "例如：我的 R 星账号", MaxLength = 40 };
        var secret = new TextBox
        {
            Header = "密钥，或 otpauth:// 链接", PlaceholderText = "JBSWY3DPEHPK3PXP", IsSpellCheckEnabled = false,
        };
        var error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12, Width = 400 };
        panel.Children.Add(name);
        panel.Children.Add(secret);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "添加令牌", Content = panel, PrimaryButtonText = "添加", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var problem = Totp.Add(name.Text, secret.Text);
            if (problem is null) return;
            error.Text = problem;
            args.Cancel = true; // 留在对话框里让用户改
        };
        await Dialogs.CustomAsync(this, dialog);
        Rebuild();
    }
}
