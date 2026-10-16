namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Controls;

/// <summary>
/// 单核心 Tile：展示核心编号、使用率、分段负载条、频率与状态徽章。
/// 通过 <see cref="IsCompact"/> 在 P-Core 标准布局与 E-Core 紧凑布局间切换；
/// 硬件插图与文字共用布局，仅在紧凑模式缩小插图与等级字号。
/// </summary>
public partial class CpuCoreTile : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(CpuCoreTile),
        new PropertyMetadata(false));

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public static readonly DependencyProperty IsCondensedProperty = DependencyProperty.Register(
        nameof(IsCondensed), typeof(bool), typeof(CpuCoreTile), new PropertyMetadata(false));
    public bool IsCondensed { get => (bool)GetValue(IsCondensedProperty); set => SetValue(IsCondensedProperty, value); }

    public CpuCoreTile()
    {
        InitializeComponent();
    }
}
