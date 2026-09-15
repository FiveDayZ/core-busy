namespace CoreBusy.App.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

/// <summary>迷你趋势折线图（峰值温度 Sparkline）：无坐标轴无网格，带柔和面积填充。</summary>
public sealed class SparkLine : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(SparkLine),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush), typeof(Brush), typeof(SparkLine),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SparkLine),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        if (values is null || values.Count < 2 || ActualWidth < 4 || ActualHeight < 4)
            return;

        // 传感器关闭 / 读取失败时采样值是 NaN（WindowsCpuMonitorService 中 _packageTemp = null）。
        // NaN 参与 min/max 比较恒为 false，会被静默跳过却又在 X(i) 里产生 NaN 顶点，
        // 导致几何坐标非法（渲染异常或整条曲线消失）。这里先按"就近保持"规范化：
        // 非有限值沿用上一个有效值，序列开头无有效值时取 0。
        var series = new double[values.Count];
        var lastValid = 0.0;
        var hasValid = false;
        for (var i = 0; i < values.Count; i++)
        {
            if (double.IsFinite(values[i]))
            {
                lastValid = values[i];
                hasValid = true;
            }

            series[i] = hasValid ? lastValid : 0.0;
        }

        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in series)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (max - min < 1e-6)
        {
            min -= 0.5;
            max += 0.5;
        }

        const double pad = 2;
        var w = ActualWidth - pad * 2;
        var h = ActualHeight - pad * 2;
        var stepX = w / (series.Length - 1);

        Point X(int i) => new(pad + i * stepX, pad + h - (series[i] - min) / (max - min) * h);

        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            ctx.BeginFigure(X(0), false, false);
            for (var i = 1; i < series.Length; i++)
                ctx.LineTo(X(i), true, true);
        }

        lineGeo.Freeze();

        if (FillBrush is not null)
        {
            var fillGeo = new StreamGeometry();
            using (var ctx = fillGeo.Open())
            {
                ctx.BeginFigure(new Point(X(0).X, ActualHeight), true, true);
                for (var i = 0; i < series.Length; i++)
                    ctx.LineTo(X(i), true, true);
                ctx.LineTo(new Point(X(series.Length - 1).X, ActualHeight), true, true);
            }

            fillGeo.Freeze();
            dc.DrawGeometry(FillBrush, null, fillGeo);
        }

        dc.DrawGeometry(null, new Pen(StrokeBrush ?? Brushes.Transparent, 1.6), lineGeo);
    }
}
