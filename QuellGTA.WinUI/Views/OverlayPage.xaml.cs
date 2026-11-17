using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Services;

namespace QuellGTA.Views;

public sealed partial class OverlayPage : Page
{
    private static OverlayService Overlay => App.Core.Overlay;
    private readonly bool _ready;
    private readonly DispatcherQueueTimer _clock;

    private static readonly (string name, string rgb)[] Colors =
    [
        ("青色", "00FFFF"), ("绿色", "00FF66"), ("红色", "FF3B30"), ("黄色", "FFE600"),
        ("品红", "FF2BD6"), ("白色", "FFFFFF"), ("浅粉（原版计时默认）", "FFC4C4"), ("橙色", "FF9500"),
    ];

    public OverlayPage()
    {
        InitializeComponent();
        BuildExtras();

        FillColors(CrosshairColorCombo, Overlay.CrosshairColor);
        FillColors(TimerColorCombo, Overlay.TimerColor);

        CrosshairSwitch.IsOn = Overlay.CrosshairEnabled;
        ShapeCombo.SelectedIndex = (int)Overlay.CrosshairShape;
        SizeSlider.Value = Overlay.CrosshairSize;
        ThicknessSlider.Value = Overlay.CrosshairThickness;
        CrosshairOpacitySlider.Value = Overlay.CrosshairOpacity;
        CrosshairXBox.Value = Overlay.CrosshairOffsetX;
        CrosshairYBox.Value = Overlay.CrosshairOffsetY;
        OutlineCheck.IsChecked = Overlay.CrosshairOutline;
        FollowCheck.IsChecked = Overlay.CrosshairFollowGame;
        ForegroundCheck.IsChecked = Overlay.CrosshairOnlyForeground;
        CrosshairCaptureCheck.IsChecked = Overlay.CrosshairHideFromCapture;

        TimerSwitch.IsOn = Overlay.TimerEnabled;
        FontSlider.Value = Overlay.TimerFontSize;
        TimerOpacitySlider.Value = Overlay.TimerOpacity;
        TimerXBox.Value = Overlay.TimerOffsetX;
        TimerYBox.Value = Overlay.TimerOffsetY;
        HideHoursCheck.IsChecked = Overlay.TimerHideHours;
        HideIdleCheck.IsChecked = Overlay.TimerHideWhenIdle;
        TimerCaptureCheck.IsChecked = Overlay.TimerHideFromCapture;
        _ready = true;

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(100);
        _clock.Tick += (_, _) => RefreshTimer();
        Loaded += (_, _) => { RefreshTimer(); _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
    }

    private static void FillColors(ComboBox combo, string current)
    {
        foreach (var (name, rgb) in Colors) combo.Items.Add(new ComboBoxItem { Content = name, Tag = rgb });
        var index = Array.FindIndex(Colors, c => c.rgb.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            combo.Items.Add(new ComboBoxItem { Content = "自定义 #" + current, Tag = current });
            index = combo.Items.Count - 1;
        }
        combo.SelectedIndex = index;
    }

    private static string SelectedColor(ComboBox combo, string fallback) =>
        combo.SelectedItem is ComboBoxItem { Tag: string rgb } ? rgb : fallback;

    private void RefreshTimer()
    {
        TimerRow.Title = Overlay.FormatTimer(Overlay.TimerMilliseconds);
        TimerButton.Content = Overlay.TimerState switch
        {
            TimerState.Idle => Lang.C(241),
            TimerState.Running => Lang.C(242),
            _ => Lang.C(243),
        };
        TimerButton.IsEnabled = Overlay.TimerEnabled;
    }

    private void Crosshair_Changed(object sender, object e)
    {
        if (!_ready) return;
        Overlay.CrosshairShape = (CrosshairShape)Math.Max(0, ShapeCombo.SelectedIndex);
        Overlay.CrosshairColor = SelectedColor(CrosshairColorCombo, Overlay.CrosshairColor);
        Overlay.CrosshairSize = (int)SizeSlider.Value;
        Overlay.CrosshairThickness = (int)ThicknessSlider.Value;
        Overlay.CrosshairOpacity = (int)CrosshairOpacitySlider.Value;
        Overlay.CrosshairOutline = OutlineCheck.IsChecked == true;
        Overlay.CrosshairFollowGame = FollowCheck.IsChecked == true;
        Overlay.CrosshairOnlyForeground = ForegroundCheck.IsChecked == true;
        Overlay.CrosshairHideFromCapture = CrosshairCaptureCheck.IsChecked == true;
        Overlay.CrosshairEnabled = CrosshairSwitch.IsOn;
    }

    private void CrosshairOffset_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready) return;
        if (!double.IsNaN(CrosshairXBox.Value)) Overlay.CrosshairOffsetX = (int)CrosshairXBox.Value;
        if (!double.IsNaN(CrosshairYBox.Value)) Overlay.CrosshairOffsetY = (int)CrosshairYBox.Value;
    }

    private void Timer_Changed(object sender, object e)
    {
        if (!_ready) return;
        Overlay.TimerColor = SelectedColor(TimerColorCombo, Overlay.TimerColor);
        Overlay.TimerFontSize = (int)FontSlider.Value;
        Overlay.TimerOpacity = (int)TimerOpacitySlider.Value;
        Overlay.TimerHideHours = HideHoursCheck.IsChecked == true;
        Overlay.TimerHideWhenIdle = HideIdleCheck.IsChecked == true;
        Overlay.TimerHideFromCapture = TimerCaptureCheck.IsChecked == true;
        Overlay.TimerEnabled = TimerSwitch.IsOn;
        RefreshTimer();
    }

    private void TimerOffset_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready) return;
        if (!double.IsNaN(TimerXBox.Value)) Overlay.TimerOffsetX = (int)TimerXBox.Value;
        if (!double.IsNaN(TimerYBox.Value)) Overlay.TimerOffsetY = (int)TimerYBox.Value;
    }

    private void TimerButton_Click(object sender, RoutedEventArgs e)
    {
        App.Core.AdvanceTimer();
        RefreshTimer();
    }
}
