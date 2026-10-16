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

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (SegmentBar)d;
        // 分段数不变时画面完全相同，不为 EXP 的每个小数变化重绘，更不订阅逐帧动画。
        if (bar.LitSegments((double)e.OldValue) != bar.LitSegments((double)e.NewValue))
            bar.InvalidateVisual();
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

    private int LitSegments(double value)
        => double.IsFinite(value) ? (int)Math.Round(Math.Clamp(value, 0, 100) / 100.0 * Math.Max(1, Segments)) : 0;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var fill = FillBrush ?? Brushes.Transparent;
        var empty = EmptyBrush ?? Brushes.Transparent;

        var n = Math.Max(1, Segments);
        const double gap = 2.5;
        var segWidth = Math.Max(1, (ActualWidth - (n - 1) * gap) / n);
        var height = Math.Max(2, ActualHeight);
        var lit = LitSegments(Value);

        for (var i = 0; i < n; i++)
        {
            var x = i * (segWidth + gap);
            var rect = new Rect(x, 0, segWidth, height);
            drawingContext.DrawRoundedRectangle(i < lit ? fill : empty, null, rect, 1.2, 1.2);
        }
    }
}
