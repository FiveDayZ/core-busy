namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

/// <summary>分区标题：强调色竖条 + 标题 + 副标题 + 右侧说明。</summary>
public partial class SectionHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty RightNoteProperty = DependencyProperty.Register(
        nameof(RightNote), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty));

    /// <summary>右侧说明的可见性：累积视图下让位给模式开关，由调用方（VM）决定。</summary>
    public static readonly DependencyProperty RightNoteVisibilityProperty = DependencyProperty.Register(
        nameof(RightNoteVisibility), typeof(Visibility), typeof(SectionHeader),
        new PropertyMetadata(Visibility.Visible));

    /// <summary>标题行最右侧的交互内容槽位（如"实时 / 累积"切换），默认无内容。</summary>
    public static readonly DependencyProperty RightContentProperty = DependencyProperty.Register(
        nameof(RightContent), typeof(object), typeof(SectionHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(SectionHeader), new PropertyMetadata(null));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string RightNote
    {
        get => (string)GetValue(RightNoteProperty);
        set => SetValue(RightNoteProperty, value);
    }

    public Visibility RightNoteVisibility
    {
        get => (Visibility)GetValue(RightNoteVisibilityProperty);
        set => SetValue(RightNoteVisibilityProperty, value);
    }

    public object? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public SectionHeader()
    {
        InitializeComponent();
    }
}
