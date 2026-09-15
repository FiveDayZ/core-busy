namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Controls;
using CoreBusy.App.ViewModels.Dashboard;

/// <summary>
/// 功耗日历面板：按自然日呈现 CPU 封装功耗累计量。
/// <para>
/// 数据来自 <see cref="DashboardViewModel"/> 的 <c>CalendarDays</c>，
/// 面板本身不持有状态——翻月只是把位移量交给 VM，避免在控件里另存一份月份。
/// </para>
/// </summary>
public partial class PowerCalendarPanel : UserControl
{
    public PowerCalendarPanel()
    {
        InitializeComponent();
    }

    /// <summary>上一个月。历史可一直往前翻（账本保留 400 天）。</summary>
    private void OnPrevMonthClick(object sender, RoutedEventArgs e)
        => (DataContext as DashboardViewModel)?.ShiftCalendarMonth(-1);

    /// <summary>下一个月。越界由 VM 夹到当前月；未来没有数据，按钮也会被禁用。</summary>
    private void OnNextMonthClick(object sender, RoutedEventArgs e)
        => (DataContext as DashboardViewModel)?.ShiftCalendarMonth(1);
}
