namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// 环形使用率仪表：底部深色轨道 + 青色圆弧，圆角端点，纯 OnRender 绘制。
/// 中心文字由外部 XAML 叠加（Binding 即可）。
/// </summary>
public sealed class DonutGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(DonutGauge),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));

    private readonly ValueAnimator _animator;

    public DonutGauge() => _animator = new ValueAnimator(this);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (DonutGauge)d;
        gauge._animator.MoveTo((double)e.NewValue);
    }

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(DonutGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(DonutGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ValueBrush
    {
        get => (Brush)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 8;
        if (radius <= 4)
            return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        const double thickness = 11;

        // 轨道圆环。
        var trackPen = new Pen(TrackBrush ?? Brushes.Transparent, thickness);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        // 数值弧线：从顶部(-90°)顺时针。
        var ratio = Math.Clamp(_animator.Current, 0, 100) / 100.0;
        if (ratio <= 0.001)
            return;

        var sweep = 360.0 * ratio;
        var startAngle = -90.0;
        var endAngle = startAngle + sweep;

        var start = PointFromAngle(center, radius, startAngle);
        var end = PointFromAngle(center, radius, endAngle);
        var largeArc = sweep > 180.0;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, largeArc, SweepDirection.Clockwise, true, false);
        }

        geo.Freeze();
        var valuePen = new Pen(ValueBrush ?? Brushes.Transparent, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawGeometry(null, valuePen, geo);
    }

    private static Point PointFromAngle(Point center, double radius, double angleDeg)
    {
        var rad = angleDeg * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
