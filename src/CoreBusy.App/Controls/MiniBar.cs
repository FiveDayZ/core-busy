namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Media;

/// <summary>连续进度条（排行榜 / 进程占用），纯 OnRender 绘制。</summary>
public sealed class MiniBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MiniBar),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));

    private readonly ValueAnimator _animator;

    public MiniBar() => _animator = new ValueAnimator(this);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (MiniBar)d;
        bar._animator.MoveTo((double)e.NewValue);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(MiniBar),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(MiniBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(MiniBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var height = Math.Max(2, ActualHeight);
        var full = new Rect(0, 0, Math.Max(0, ActualWidth), height);
        drawingContext.DrawRoundedRectangle(TrackBrush ?? Brushes.Transparent, null, full, height / 2, height / 2);

        var max = Math.Max(1e-6, Maximum);
        var ratio = Math.Clamp(Value / max, 0, 1);
        if (ratio <= 0)
            return;

        var width = Math.Max(height, ActualWidth * ratio);
        var fillRect = new Rect(0, 0, Math.Min(width, ActualWidth), height);
        drawingContext.DrawRoundedRectangle(FillBrush ?? Brushes.Transparent, null, fillRect, height / 2, height / 2);
    }
}
