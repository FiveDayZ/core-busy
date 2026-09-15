namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// 分段负载条：由 N 个小方块组成，按 Value(0-100) 点亮前若干段。
/// 纯 OnRender 绘制，避免几百个 Border 的视觉树开销。
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SegmentBar),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));

    private readonly ValueAnimator _animator;

    public SegmentBar() => _animator = new ValueAnimator(this);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (SegmentBar)d;
        bar._animator.MoveTo((double)e.NewValue);
    }

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(int), typeof(SegmentBar),
        new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyBrushProperty = DependencyProperty.Register(
        nameof(EmptyBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush EmptyBrush
    {
        get => (Brush)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    static SegmentBar()
        => SnapsToDevicePixelsProperty.OverrideMetadata(typeof(SegmentBar), new FrameworkPropertyMetadata(true));

    protected override void OnRender(DrawingContext drawingContext)
    {
        var fill = FillBrush ?? Brushes.Transparent;
        var empty = EmptyBrush ?? Brushes.Transparent;

        var n = Math.Max(1, Segments);
        const double gap = 2.5;
        var segWidth = Math.Max(1, (ActualWidth - (n - 1) * gap) / n);
        var height = Math.Max(2, ActualHeight);
        var lit = (int)Math.Round(Math.Clamp(_animator.Current, 0, 100) / 100.0 * n);

        for (var i = 0; i < n; i++)
        {
            var x = i * (segWidth + gap);
            var rect = new Rect(x, 0, segWidth, height);
            drawingContext.DrawRoundedRectangle(i < lit ? fill : empty, null, rect, 1.2, 1.2);
        }
    }
}
