namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Controls;

/// <summary>
/// 单核心 Tile：展示核心编号、使用率、分段负载条、频率与状态徽章。
/// 通过 <see cref="IsCompact"/> 在 P-Core 标准布局与 E-Core 紧凑布局间切换；
/// 高负载时的红色描边 / 红色 Glow 由 ViewModel 绑定驱动。
/// </summary>
public partial class CpuCoreTile : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(CpuCoreTile),
        new PropertyMetadata(false, OnIsCompactChanged));

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public CpuCoreTile()
    {
        InitializeComponent();
    }

    private static void OnIsCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tile = (CpuCoreTile)d;
        var compact = (bool)e.NewValue;
        tile.NormalPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        tile.CompactPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }
}
