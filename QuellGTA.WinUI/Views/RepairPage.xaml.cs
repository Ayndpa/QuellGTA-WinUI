using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class RepairPage : Page
{
    private static GameFilesService Files => App.Core.Files;

    public RepairPage()
    {
        InitializeComponent();
        BuildExtras();
        OnlyLatestCheck.IsChecked = Files.OnlyLatestProfile;
        LegacyCheck.IsChecked = Files.LegacyClean;
        CleanOnExitCheck.IsChecked = Files.CleanOnExit;
        DeleteOnExitCheck.IsChecked = Files.DeleteOnExit;
        ShaderOnExitCheck.IsChecked = Files.ClearShaderOnExit;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => RefreshProfiles();

    private void RefreshProfiles()
    {
        ProfilePanel.Children.Clear();
        var profiles = Files.DescribeProfiles();
        if (profiles.Count == 0)
        {
            ProfilePanel.Children.Add(new TextBlock
            {
                Text = $"没有找到存档目录：{Files.ProfilesDir}",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
            });
            return;
        }
        foreach (var (profile, exists, hasBackup, readOnly, latest) in profiles)
        {
            var tags = new List<string> { exists ? "有设置文件" : "没有设置文件" };
            if (hasBackup) tags.Add("有备份");
            if (readOnly) tags.Add("只读");
            if (latest) tags.Add("最近使用");
            ProfilePanel.Children.Add(new TextBlock
            {
                Text = $"{profile}　{string.Join(" · ", tags)}",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Opacity = latest ? 1 : 0.7,
            });
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshProfiles();

    private void OpenProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dir = Directory.Exists(Files.ProfilesDir) ? Files.ProfilesDir : Files.DocumentsDir;
        if (Directory.Exists(dir)) Shell.Open(dir);
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        // 自动清洗和自动删除二选一（原版是互斥的菜单项）
        if (ReferenceEquals(sender, CleanOnExitCheck) && CleanOnExitCheck.IsChecked == true) DeleteOnExitCheck.IsChecked = false;
        if (ReferenceEquals(sender, DeleteOnExitCheck) && DeleteOnExitCheck.IsChecked == true) CleanOnExitCheck.IsChecked = false;

        Files.OnlyLatestProfile = OnlyLatestCheck.IsChecked == true;
        Files.LegacyClean = LegacyCheck.IsChecked == true;
        Files.CleanOnExit = CleanOnExitCheck.IsChecked == true;
        Files.DeleteOnExit = DeleteOnExitCheck.IsChecked == true;
        Files.ClearShaderOnExit = ShaderOnExitCheck.IsChecked == true;
    }

    private async Task RunAsync(string title, string message, Func<FileResult> action)
    {
        await Dialogs.RunAsync(this, title, message, action);
        RefreshProfiles();
    }

    private string LockWarning() =>
        App.Core.SessionLock.GetStatus().State == LockState.Locked ? "\n\n" + Lang.C(190) : "";

    private async void Clean_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("清洗设置文件", $"{Lang.C(315)}\n{Lang.C(187)}{Lang.C(188)}{Lang.C(316)}{LockWarning()}", Files.CleanSettings);

    private async void Delete_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("备份后删除设置文件", $"{Lang.C(186)}\n{Lang.C(187)}{Lang.C(188)}{Lang.C(189)}{LockWarning()}", Files.DeleteSettings);

    private async void Backup_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("备份设置文件", Lang.C(224), Files.BackupSettings);

    private async void Restore_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("恢复备份", "用 .bak 备份覆盖当前的 pc_settings.bin，当前文件会移到回收站。没有备份的存档不受影响。是否继续？", Files.RestoreSettings);

    private async void ReadOnly_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("设置只读", Lang.C(313), () => Files.SetReadOnly(true));

    private async void Writable_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("取消只读", Lang.C(314), () => Files.SetReadOnly(false));

    private async void DeleteGraphics_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("删除画质设置文件", Lang.C(503), Files.DeleteGraphicsSettings);

    private async void DeleteKeys_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("删除键位设置文件", Lang.C(506), Files.DeleteKeyBindings);

    private async void ClearShader_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("清理 DX12 着色器缓存", $"将删除 {Files.DocumentsDir}\\Settings 文件夹（移到回收站）。是否继续？", Files.ClearShaderCache);

    private async void RepairLauncher_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("修复 Rockstar 启动器", Lang.C(291), Files.RepairLauncher);

    private async void RepairBattlEye_Click(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(this, "修复 BattlEye 反作弊", Lang.C(445))) return;
        var reinstall = await Dialogs.ConfirmAsync(this, "重新安装", Lang.C(446), "现在安装");
        var result = await Task.Run(() => Files.RepairBattlEye(reinstall));
        await Dialogs.MessageAsync(this, result.Ok ? "操作完成" : "操作未完成", result.Message);
    }
}
