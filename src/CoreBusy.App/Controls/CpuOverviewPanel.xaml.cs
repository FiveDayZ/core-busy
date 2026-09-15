namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Controls;
using CoreBusy.App.ViewModels.Dashboard;
using CoreBusy.App.Views;

/// <summary>
/// CPU 概览面板：环形总使用率 + 温度/功耗/频率/风扇指标行。
/// 直接绑定上级 DataContext（DashboardViewModel）的属性。
/// </summary>
public partial class CpuOverviewPanel : UserControl
{
    public CpuOverviewPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 标题行的内核驱动提示（v1.16.1）：委托主窗口执行提权重启。
    /// 重启涉及托盘/退出收尾，属于窗口级职责，控件只负责把点击交上去（见 MainWindow.RestartElevated）。
    /// </summary>
    private void OnElevateClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.RestartElevated();

    /// <summary>
    /// 逐核确定性自检入口（v1.18.0，方案 P3）：把"用户点一下"翻译成 ViewModel 的启停。
    /// 同一个按钮在运行中变为「停止」—— 启停语义由 VM 的 ToggleSelfTest 决定，控件不做状态判断，
    /// 避免两处状态机打架（本项目既有教训：状态判断只能有一个权威来源）。
    /// </summary>
    private void OnSelfTestClick(object sender, RoutedEventArgs e)
        => (DataContext as DashboardViewModel)?.ToggleSelfTest();
}
