namespace CoreBusy.App.Controls;

using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

/// <summary>排行榜面板（劳模榜 / 摸鱼榜，右上角 Tab 可切换）。</summary>
public partial class RankingPanel : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(RankingPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(Geometry), typeof(RankingPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(
        nameof(GlyphBrush), typeof(Brush), typeof(RankingPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty GlyphDimBrushProperty = DependencyProperty.Register(
        nameof(GlyphDimBrush), typeof(Brush), typeof(RankingPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(RankingPanel), new PropertyMetadata(null));

    /// <summary>右上角可点击的另一个 Tab 文案（摸鱼榜 / 劳模榜）。</summary>
    public static readonly DependencyProperty TabTextProperty = DependencyProperty.Register(
        nameof(TabText), typeof(string), typeof(RankingPanel), new PropertyMetadata(string.Empty));

    /// <summary>点击右上角 Tab 时冒泡的路由事件，由宿主窗口处理。</summary>
    public static readonly RoutedEvent TabClickEvent = EventManager.RegisterRoutedEvent(
        nameof(TabClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RankingPanel));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public Geometry Glyph
    {
        get => (Geometry)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Brush GlyphBrush
    {
        get => (Brush)GetValue(GlyphBrushProperty);
        set => SetValue(GlyphBrushProperty, value);
    }

    public Brush GlyphDimBrush
    {
        get => (Brush)GetValue(GlyphDimBrushProperty);
        set => SetValue(GlyphDimBrushProperty, value);
    }

    public IEnumerable ItemsSource
    {
        get => (IEnumerable)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string TabText
    {
        get => (string)GetValue(TabTextProperty);
        set => SetValue(TabTextProperty, value);
    }

    public event RoutedEventHandler TabClick
    {
        add => AddHandler(TabClickEvent, value);
        remove => RemoveHandler(TabClickEvent, value);
    }

    public RankingPanel()
    {
        InitializeComponent();
    }

    private void OnTabClicked(object sender, RoutedEventArgs e)
        => RaiseEvent(new RoutedEventArgs(TabClickEvent, this));
}
