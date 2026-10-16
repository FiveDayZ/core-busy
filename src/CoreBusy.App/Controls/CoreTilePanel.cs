namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

/// <summary>保持各分区相同列宽；窄窗少放几列，避免多核平台的封装与读数挤在一起。</summary>
public sealed class CoreTilePanel : UniformGrid
{
    private static readonly Pen CircuitPen = (Pen)new Pen(new SolidColorBrush(Color.FromRgb(52, 85, 64)), .7).GetAsFrozen();
    private static readonly Brush ViaBrush = (Brush)new SolidColorBrush(Color.FromRgb(110, 121, 77)).GetAsFrozen();

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(CoreTilePanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    public static readonly DependencyProperty FitEightCoresProperty = DependencyProperty.Register(
        nameof(FitEightCores), typeof(bool), typeof(CoreTilePanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool FitEightCores { get => (bool)GetValue(FitEightCoresProperty); set => SetValue(FitEightCoresProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        var max = Math.Max(1, MaxColumns);
        var capacity = double.IsFinite(constraint.Width) ? Math.Max(1, (int)(constraint.Width / 148)) : max;
        var columns = max >= 8 && capacity >= 8 ? 8
            : max == 6 && capacity >= 6 ? 6 : Math.Min(Math.Min(capacity, max), 4);
        if (FitEightCores) columns = 4;
        if (Columns != columns) SetCurrentValue(ColumnsProperty, columns);
        return base.MeasureOverride(constraint);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Columns <= 0 || InternalChildren.Count == 0) return;
        var rows = (InternalChildren.Count + Columns - 1) / Columns;
        var cellWidth = ActualWidth / Columns;
        var cellHeight = ActualHeight / rows;
        // 走线跟随真实插座行列，沿卡片之间的通道布线；卡片本体遮住连接端。
        for (var column = 1; column < Columns; column++)
        {
            var x = column * cellWidth;
            dc.DrawLine(CircuitPen, new Point(x - 2, 6), new Point(x - 2, ActualHeight - 6));
            dc.DrawLine(CircuitPen, new Point(x + 2, 10), new Point(x + 2, ActualHeight - 10));
            for (var row = 0; row < rows; row++)
            {
                var y = row * cellHeight + 22;
                dc.DrawLine(CircuitPen, new Point(x - 7, y - 5), new Point(x - 2, y));
                dc.DrawEllipse(ViaBrush, null, new Point(x + 2, y + 5), 1.4, 1.4);
            }
        }
        for (var row = 1; row < rows; row++)
        {
            var y = row * cellHeight;
            dc.DrawLine(CircuitPen, new Point(7, y - 2), new Point(ActualWidth - 7, y - 2));
            dc.DrawLine(CircuitPen, new Point(12, y + 2), new Point(ActualWidth - 12, y + 2));
        }
    }
}
