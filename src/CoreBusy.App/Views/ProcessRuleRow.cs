namespace CoreBusy.App.Views;

using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Models;

/// <summary>
/// 设置窗口「进程规则」列表的一行（v1.12.0）。
/// <para>
/// 下拉框绑的是**下标**而不是枚举值本身：<c>SelectedIndex</c> 是 int，
/// 绑定链最短。若改成绑定枚举对象，WPF 用 <c>Equals</c> 判定选中项，
/// 而界面上的选项若来自另一份重建的列表，就会出现"数据是对的、
/// 但下拉框显示空白"的经典静默失效。下标没有这个问题 ——
/// 唯一的约束是选项表的顺序必须与枚举的映射保持一致，
/// 这一点由 <see cref="OptimizationLabels"/> 统一生成来保证。
/// </para>
/// <para>
/// v1.15.0 起支持「自定义核心」：<see cref="AffinityIndex"/> 选中
/// <see cref="CoreAffinityMode.Custom"/> 时，「选择核心…」按钮与摘要可见，
/// 掩码由核心选择器写回 <see cref="CustomAffinityMask"/>。
/// </para>
/// </summary>
public sealed class ProcessRuleRow : ObservableObject
{
    private readonly IReadOnlyList<CoreAffinityMode> _affinityModes;
    private readonly IReadOnlyList<ProcessPriorityLevel> _priorityLevels;

    private string _processName = string.Empty;
    private int _affinityIndex;
    private int _priorityIndex;
    private ulong _customMask;
    private bool _enabled = true;
    private bool _kernelEnforced;
    private bool _lowerIoPriority;
    private bool _gameOnly;

    public ProcessRuleRow(
        IReadOnlyList<CoreAffinityMode> affinityModes,
        IReadOnlyList<ProcessPriorityLevel> priorityLevels,
        ProcessOptimizationRule? source = null)
    {
        _affinityModes = affinityModes;
        _priorityLevels = priorityLevels;
        AffinityOptions = OptimizationLabels.AffinityOptions(affinityModes);
        PriorityOptions = OptimizationLabels.PriorityOptions(priorityLevels);

        // 新建行默认停在「不干预」，让用户必须显式选一个动作，避免误加一条空规则。
        _affinityIndex = IndexOf(affinityModes, CoreAffinityMode.None);
        _priorityIndex = IndexOf(priorityLevels, ProcessPriorityLevel.None);

        if (source is null)
            return;

        _processName = source.ProcessName;
        _affinityIndex = Math.Max(0, IndexOf(affinityModes, source.Affinity));
        _priorityIndex = Math.Max(0, IndexOf(priorityLevels, source.Priority));
        _enabled = source.Enabled;
        _kernelEnforced = source.KernelEnforced;
        _lowerIoPriority = source.LowerIoPriority;
        _gameOnly = source.GameOnly;
        _customMask = source.CustomAffinityMask;
    }

    /// <summary>亲和性下拉框选项（顺序与 <see cref="CoreAffinityMode"/> 的可用子集一致）。</summary>
    public IReadOnlyList<string> AffinityOptions { get; }

    /// <summary>优先级下拉框选项。</summary>
    public IReadOnlyList<string> PriorityOptions { get; }

    /// <summary>目标进程名（含 .exe）。</summary>
    public string ProcessName
    {
        get => _processName;
        set => SetProperty(ref _processName, value);
    }

    /// <summary>亲和性选项下标（TwoWay 绑定到 ComboBox.SelectedIndex）。</summary>
    public int AffinityIndex
    {
        get => _affinityIndex;
        set
        {
            // 亲和性档位变化会连带决定「选择核心…」按钮的可见性与摘要文案，
            // 两个派生属性必须一起通知，否则切到/切出「自定义核心」时界面不刷新
            //（绑定静默失效的一个变体：数据变了、通知没发）。
            if (SetProperty(ref _affinityIndex, Math.Clamp(value, 0, Math.Max(0, _affinityModes.Count - 1))))
            {
                OnPropertyChanged(nameof(IsCustomAffinity));
                OnPropertyChanged(nameof(AffinitySummary));
            }
        }
    }

    /// <summary>优先级选项下标。</summary>
    public int PriorityIndex
    {
        get => _priorityIndex;
        set => SetProperty(ref _priorityIndex, Math.Clamp(value, 0, Math.Max(0, _priorityLevels.Count - 1)));
    }

    /// <summary>
    /// 自定义核心掩码（<see cref="CoreAffinityMode.Custom"/> 时生效）：
    /// 第 n 位 = 逻辑处理器 n，与拓扑 <c>OsIndex</c> 同口径；0 = 未配置。
    /// </summary>
    public ulong CustomAffinityMask
    {
        get => _customMask;
        set
        {
            if (SetProperty(ref _customMask, value))
                OnPropertyChanged(nameof(AffinitySummary));
        }
    }

    /// <summary>当前是否选中「自定义核心」（决定选择器按钮与摘要的可见性）。</summary>
    public bool IsCustomAffinity => Affinity == CoreAffinityMode.Custom;

    /// <summary>自定义核心摘要文案；仅 Custom 时非空。</summary>
    public string AffinitySummary =>
        IsCustomAffinity ? OptimizationLabels.DescribeCustomMask(_customMask) : string.Empty;

    /// <summary>是否启用该规则。</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>是否用作业对象内核强制亲和。</summary>
    public bool KernelEnforced
    {
        get => _kernelEnforced;
        set => SetProperty(ref _kernelEnforced, value);
    }

    /// <summary>是否把磁盘 I/O 优先级降到最低。</summary>
    public bool LowerIoPriority
    {
        get => _lowerIoPriority;
        set => SetProperty(ref _lowerIoPriority, value);
    }

    /// <summary>
    /// 是否仅在游戏模式激活期间套用（v1.13.0）。
    /// 位于本行的第三个勾选框 —— 它是让"后台降级"能用在开发机上的开关。
    /// </summary>
    public bool GameOnly
    {
        get => _gameOnly;
        set => SetProperty(ref _gameOnly, value);
    }

    /// <summary>当前选中的亲和性模式。</summary>
    public CoreAffinityMode Affinity => _affinityModes[Math.Clamp(_affinityIndex, 0, _affinityModes.Count - 1)];

    /// <summary>当前选中的优先级档位。</summary>
    public ProcessPriorityLevel Priority => _priorityLevels[Math.Clamp(_priorityIndex, 0, _priorityLevels.Count - 1)];

    /// <summary>导出为持久化模型。</summary>
    public ProcessOptimizationRule ToRule() => new()
    {
        ProcessName = ProcessName,
        Enabled = Enabled,
        Affinity = Affinity,
        CustomAffinityMask = CustomAffinityMask,
        Priority = Priority,
        KernelEnforced = KernelEnforced,
        LowerIoPriority = LowerIoPriority,
        GameOnly = GameOnly,
    };

    /// <summary>
    /// 该行是否包含至少一个动作（无动作的行会被服务忽略，界面据此提示）。
    /// Custom 掩码为 0 视为无动作 —— 服务端同样按此口径拒绝。
    /// </summary>
    public bool HasAction =>
        (Affinity == CoreAffinityMode.Custom
            ? CustomAffinityMask != 0
            : Affinity != CoreAffinityMode.None)
        || Priority != ProcessPriorityLevel.None
        || LowerIoPriority;

    private static int IndexOf<T>(IReadOnlyList<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(list[i], value))
                return i;
        }

        return 0;
    }
}
