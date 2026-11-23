using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace QuellGTA.Views;

/// <summary>
/// 设置项：内部就是一张 SettingsCard。保留这个外壳是为了让各页面 XAML 里原有的写法继续可用。
/// </summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class SettingRow : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow),
            new PropertyMetadata("", (d, e) => ((SettingRow)d).Card.Header = e.NewValue));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow),
            new PropertyMetadata("", (d, e) =>
            {
                var text = (string)e.NewValue;
                ((SettingRow)d).Card.Description = string.IsNullOrEmpty(text) ? null! : text;
            }));

    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.Register(nameof(Action), typeof(object), typeof(SettingRow),
            new PropertyMetadata(null, (d, e) => ((SettingRow)d).Card.Content = e.NewValue));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingRow),
            new PropertyMetadata("", (d, e) =>
            {
                var glyph = (string)e.NewValue;
                ((SettingRow)d).Card.HeaderIcon = string.IsNullOrEmpty(glyph) ? null! : new FontIcon { Glyph = glyph };
            }));

    public SettingRow() => InitializeComponent();

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    /// <summary>左侧图标（Segoe Fluent Icons 字形）。</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
}
