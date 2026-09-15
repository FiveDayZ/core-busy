namespace CoreBusy.App.Controls;

using System.Collections;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

/// <summary>主要负载来源面板（进程 TOP5：图标 + 进程名 + 占用 + 水平条）。</summary>
public partial class ProcessListPanel : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(ProcessListPanel), new PropertyMetadata(null));

    public IEnumerable ItemsSource
    {
        get => (IEnumerable)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ProcessListPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 「查看全部」（v1.10.4）：此前是无任何处理的静态 TextBlock，点击无效。
    /// 应用内没有完整进程列表视图，最贴合语义的行为是打开任务管理器；静默吞掉启动失败，
    /// 不打断主界面。
    /// </summary>
    private void OnViewAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
        }
        catch
        {
            // 被策略限制等场景：忽略。
        }
    }
}
