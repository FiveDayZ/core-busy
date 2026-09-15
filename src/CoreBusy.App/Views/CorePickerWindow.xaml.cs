namespace CoreBusy.App.Views;

using System.Windows;
using System.Windows.Controls;
using CoreBusy.Core.Models;

/// <summary>
/// 核心选择器（v1.15.0）：把「自定义核心」从手填掩码变成按物理核勾选。
/// <para>
/// 按物理核分组而不是平铺全部勾选框 —— SMT 平台上"关掉某个核"实际是关掉
/// 一对线程，平铺会诱导用户只勾一半；分组后一眼可见哪些线程同属一个物理核。
/// P/E 徽标仅混合架构显示，同构机不冒充大小核（与热力图分区同一口径）。
/// </para>
/// <para>
/// 「至少保留 1 个核心」在两处强制：确定按钮在勾选数为 0 时禁用（守卫），
/// OnOk 里再挡一次（防御事件绕过）。服务端 <c>AffinityMaskBuilder.ValidateCustom</c>
/// 最终复核 —— 三道闸共用同一语义，手改 settings.json 也绕不过。
/// </para>
/// </summary>
public partial class CorePickerWindow : Window
{
    private readonly IReadOnlyList<CorePickerCore> _cores;

    public CorePickerWindow(IReadOnlyList<LogicalProcessorTopology> topology, ulong initialMask)
    {
        InitializeComponent();

        var hasPerformance = topology.Any(t => t.CoreClass == CoreClass.Performance);
        var hasEfficiency = topology.Any(t => t.CoreClass == CoreClass.Efficiency);
        var physicalCount = topology.Select(t => t.PhysicalCoreIndex).Distinct().Count();
        var hasSmt = topology.Count > physicalCount;

        _cores = BuildCores(topology, initialMask);
        CoresList.ItemsSource = _cores;

        // 快捷按钮按硬件能力显示：同构机不出现「仅 P 核」，无 SMT 不出现「仅物理核」。
        PerformanceButton.Visibility = hasPerformance ? Visibility.Visible : Visibility.Collapsed;
        EfficiencyButton.Visibility = hasEfficiency ? Visibility.Visible : Visibility.Collapsed;
        PhysicalButton.Visibility = hasSmt ? Visibility.Visible : Visibility.Collapsed;

        UpdateCount();
    }

    /// <summary>选择结果：第 n 位 = 逻辑处理器 n。取消时保持 0，调用方不使用。</summary>
    public ulong SelectedMask { get; private set; }

    private IEnumerable<CorePickerThread> AllThreads => _cores.SelectMany(c => c.Threads);

    /// <summary>
    /// 按物理核折算初始勾选。掩码里指向不存在处理器的位（换了 CPU 后的旧配置）
    /// 在这里自然丢弃 —— 摘要会显示清洗后的掩码，服务端校验也不会再被越界位绊住。
    /// </summary>
    private static List<CorePickerCore> BuildCores(
        IReadOnlyList<LogicalProcessorTopology> topology, ulong initialMask)
    {
        return topology
            .GroupBy(t => t.PhysicalCoreIndex)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var first = g.OrderBy(t => t.OsIndex).First();
                return new CorePickerCore
                {
                    Title = string.IsNullOrWhiteSpace(first.DisplayName)
                        ? $"物理核 {g.Key}"
                        : first.DisplayName,
                    ClassBadge = first.CoreClass switch
                    {
                        CoreClass.Performance => "P 核",
                        CoreClass.Efficiency => "E 核",
                        _ => string.Empty,
                    },
                    IsPerformance = first.CoreClass == CoreClass.Performance,
                    IsEfficiency = first.CoreClass == CoreClass.Efficiency,
                    Threads = g.OrderBy(t => t.OsIndex)
                        .Select(t => new CorePickerThread(
                            t.OsIndex,
                            $"CPU {t.OsIndex}",
                            (initialMask & (1UL << t.OsIndex)) != 0))
                        .ToList(),
                };
            })
            .ToList();
    }

    private void OnThreadCheck(object sender, RoutedEventArgs e) => UpdateCount();

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(_ => true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(_ => false);

    private void OnPhysicalOnly(object sender, RoutedEventArgs e)
    {
        foreach (var core in _cores)
        {
            var keep = core.Threads.OrderBy(t => t.OsIndex).First();
            foreach (var thread in core.Threads)
                thread.IsChecked = ReferenceEquals(thread, keep);
        }

        UpdateCount();
    }

    private void OnPerformanceOnly(object sender, RoutedEventArgs e) => SetByCore(c => c.IsPerformance);

    private void OnEfficiencyOnly(object sender, RoutedEventArgs e) => SetByCore(c => c.IsEfficiency);

    private void SetAll(Func<CorePickerThread, bool> predicate)
    {
        foreach (var thread in AllThreads)
            thread.IsChecked = predicate(thread);

        UpdateCount();
    }

    private void SetByCore(Func<CorePickerCore, bool> predicate)
    {
        foreach (var core in _cores)
        {
            foreach (var thread in core.Threads)
                thread.IsChecked = predicate(core);
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        var threads = AllThreads.ToList();
        var selected = threads.Count(t => t.IsChecked);
        CountText.Text = $"已选 {selected} / {threads.Count} 个逻辑处理器 —— 至少保留 1 个";
        OkButton.IsEnabled = selected > 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ulong bits = 0;
        foreach (var thread in AllThreads)
        {
            if (thread.IsChecked)
                bits |= 1UL << thread.OsIndex;
        }

        if (bits == 0)
            return; // OkButton 已禁用；此处只防御事件路径绕过。

        SelectedMask = bits;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
