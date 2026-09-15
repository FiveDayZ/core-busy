namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

/// <summary>CPU 信息栏统计卡：图标 + 标签（弱化）+ 数值（突出）。</summary>
public partial class CpuSummaryCard : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(CpuSummaryCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(CpuSummaryCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconGeometryProperty = DependencyProperty.Register(
        nameof(IconGeometry), typeof(Geometry), typeof(CpuSummaryCard), new PropertyMetadata(null));

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(CpuSummaryCard), new PropertyMetadata(null));

    public static readonly DependencyProperty IconDimBrushProperty = DependencyProperty.Register(
        nameof(IconDimBrush), typeof(Brush), typeof(CpuSummaryCard), new PropertyMetadata(null));

    public static readonly DependencyProperty IsStrokeIconProperty = DependencyProperty.Register(
        nameof(IsStrokeIcon), typeof(bool), typeof(CpuSummaryCard), new PropertyMetadata(false));

    public bool IsStrokeIcon
    {
        get => (bool)GetValue(IsStrokeIconProperty);
        set => SetValue(IsStrokeIconProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Geometry IconGeometry
    {
        get => (Geometry)GetValue(IconGeometryProperty);
        set => SetValue(IconGeometryProperty, value);
    }

    public Brush IconBrush
    {
        get => (Brush)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public Brush IconDimBrush
    {
        get => (Brush)GetValue(IconDimBrushProperty);
        set => SetValue(IconDimBrushProperty, value);
    }

    public CpuSummaryCard()
    {
        InitializeComponent();
    }
}
