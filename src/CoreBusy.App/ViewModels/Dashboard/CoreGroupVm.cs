namespace CoreBusy.App.ViewModels.Dashboard;

using System.Windows;
using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Models;

/// <summary>
/// 主界面一段核心分区的视图模型（规范 §52）：P-Core / E-Core / CPU Core / CCD n。
/// 分区由 <see cref="CpuCoreGroup"/> 数据推导，界面不硬编码固定分组。
/// <para>
/// 本类同时承载「实时负载 / 累积负载」两种视图下的**分区级**状态：
/// 副标题口径、右侧说明的显隐、切换控件的可见性。真正的模式开关是全局唯一的，
/// 只在第一段分区上挂出控件（避免多段平台出现两个功能完全相同的开关）。
/// </para>
/// </summary>
public sealed class CoreGroupVm : ObservableObject
{
    private readonly string _naturalSubtitle;
    /// <summary>
    /// 呈现中的核心集合。<b>实例永不替换</b>，变化只通过
    /// <see cref="ObservableCollection{T}"/> 的增删接口发生。
    /// </summary>
    /// <remarks>
    /// 这是焦点展开的第三版方案，前两版都实测失败：
    /// ① 换集合引用（<c>Cores = [target]</c>）——通知发了、ItemsControl 不重建容器；
    /// ② 保持全量 + 逐核 Visibility 折叠——绑定求值了、值正确（getter 探针实测
    ///    返回 Collapsed），但视觉树不应用。两者的共同点是都依赖
    ///    <see cref="System.ComponentModel.INotifyPropertyChanged"/> 的属性通知。
    /// 本方案改用 <c>INotifyCollectionChanged</c>：Remove/Insert 是 ItemsControl
    /// 原生的容器增删协议，不经过属性绑定求值路径。
    /// </remarks>
    private readonly System.Collections.ObjectModel.ObservableCollection<DashboardCoreVm> _cores = new();
    private string _subtitle;
    private bool _isCumulativeMode;

    public CoreGroupVm(
        CpuCoreGroup group,
        bool isCompact,
        int tileColumns,
        IReadOnlyList<DashboardCoreVm> cores,
        bool isFirstGroup)
    {
        Kind = group.Kind;
        Title = group.Title;
        IsCompact = isCompact;
        TileColumns = tileColumns;

        NaturalCores = cores;
        foreach (var core in cores)
            _cores.Add(core);

        _naturalSubtitle = BuildSubtitle(group);
        _subtitle = _naturalSubtitle;
        RightNote = BuildRightNote(group);

        ModeSwitchVisibility = isFirstGroup ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>分区类别（决定标题下缀文案与卡片密度）。</summary>
    public CoreGroupKind Kind { get; }

    /// <summary>分区标题（P-Core / E-Core / CPU Core / CCD 0）。</summary>
    public string Title { get; }

    /// <summary>分区右侧说明文案（累积视图下隐藏，让位给模式开关）。</summary>
    public string RightNote { get; }

    /// <summary>分区副标题：实时视图为核数/线程数，累积视图为运行时长与排序口径。</summary>
    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    /// <summary>是否使用紧凑卡片布局（E-Core，或分段较多时整体收一档）。</summary>
    public bool IsCompact { get; }

    /// <summary>Tile 网格列数（随核心数自适应）。</summary>
    public int TileColumns { get; }


    /// <summary>该分区包含的核心 Tile，**按当前视图顺序**呈现（累积视图下按名次降序）。</summary>
    /// <remarks>
    /// 焦点态下<b>只含焦点核</b>（其余被移出），取消时按自然序插回。
    /// 变化通过 <see cref="System.Collections.Specialized.INotifyCollectionChanged"/>
    /// 通知 UI，见 <see cref="_cores"/> 的 remarks。
    /// </remarks>
    public System.Collections.ObjectModel.ObservableCollection<DashboardCoreVm> Cores => _cores;

    /// <summary>
    /// 把呈现顺序对齐到 <paramref name="ordered"/>。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="System.Collections.ObjectModel.ObservableCollection{T}.Move"/>
    /// 而非换引用：Move 让 ItemsControl 只重排既有容器，不销毁重建。
    /// 顺序已一致时是空操作（调用方每帧都会进来，不能每帧重排）。
    /// </remarks>
    public void SetCoresOrder(IReadOnlyList<DashboardCoreVm> ordered)
    {
        if (ordered.Count != _cores.Count)
            return;   // 焦点态下数量不同，重排无意义（调用方在焦点态本就会跳过）

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ReferenceEquals(_cores[i], ordered[i]))
                continue;

            var from = _cores.IndexOf(ordered[i]);
            if (from < 0)
                return;   // 目标序列里出现了不在集合中的元素，防御：放弃本次重排

            _cores.Move(from, i);
        }
    }


    /// <summary>核心的自然顺序（按核心编号），切回实时视图时用它复位。</summary>
    public IReadOnlyList<DashboardCoreVm> NaturalCores { get; }

    /// <summary>当前是否为累积负载视图（由 DashboardViewModel 统一下发）。</summary>
    public bool IsCumulativeMode
    {
        get => _isCumulativeMode;
        private set
        {
            if (!SetProperty(ref _isCumulativeMode, value))
                return;

            OnPropertyChanged(nameof(RightNoteVisibility));
            OnPropertyChanged(nameof(ResetVisibility));
        }
    }

    /// <summary>
    /// 模式开关的可见性。只在第一段分区上显示：开关本身是全局的，
    /// 多段平台（Intel P/E）各挂一个完全同步的开关只会造成重复与歧义。
    /// </summary>
    public Visibility ModeSwitchVisibility { get; }

    /// <summary>右侧说明的可见性。累积视图下让位给模式开关，避免标题行拥挤。</summary>
    public Visibility RightNoteVisibility
        => _isCumulativeMode ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>「重置累计」按钮的可见性：仅累积视图下出现。</summary>
    public Visibility ResetVisibility
        => _isCumulativeMode ? Visibility.Visible : Visibility.Collapsed;


    /// <summary>
    /// 下发当前视图模式与运行时长文案。
    /// 副标题在累积视图下改述为"运行 H:MM:SS · 按累积均值降序"——
    /// 这句话同时解释了统计窗口与列表顺序，是理解排名最省事的落点。
    /// </summary>
    public void ApplyLoadMode(bool cumulative, string uptimeText)
    {
        IsCumulativeMode = cumulative;
        Subtitle = cumulative
            ? $"运行 {uptimeText} · 按累积均值降序"
            : _naturalSubtitle;
    }

    /// <summary>
    /// 副标题口径（规范 §19/§32/§33）：
    /// Intel 混合 → "性能核心（6核 / 12线程）" / "能效核心（8核 / 8线程）"；
    /// 同构平台 → "8 核 / 16 线程"。
    /// </summary>
    private static string BuildSubtitle(CpuCoreGroup group) => group.Kind switch
    {
        CoreGroupKind.Performance => $"性能核心（{group.CoreCount}核 / {group.ThreadCount}线程）",
        CoreGroupKind.Efficiency => $"能效核心（{group.CoreCount}核 / {group.ThreadCount}线程）",
        _ => $"{group.CoreCount} 核 / {group.ThreadCount} 线程",
    };

    private static string BuildRightNote(CpuCoreGroup group) => group.Kind switch
    {
        CoreGroupKind.Performance => "强劲性能 · 处理重要任务",
        CoreGroupKind.Efficiency => "高能效 · 处理后台任务",
        _ => "同构核心 · 统一调度",
    };
}
