namespace CoreBusy.App.Views;

using System.Windows;
using CoreBusy.App.Infrastructure;

/// <summary>
/// 核心选择器里的单个逻辑处理器（一个勾选框）（v1.15.0）。
/// </summary>
public sealed class CorePickerThread : ObservableObject
{
    private bool _isChecked;

    public CorePickerThread(int osIndex, string label, bool isChecked = false)
    {
        OsIndex = osIndex;
        Label = label;
        _isChecked = isChecked;
    }

    /// <summary>操作系统逻辑处理器编号（掩码第 n 位的口径）。</summary>
    public int OsIndex { get; }

    /// <summary>显示名，如 "CPU 3"（与任务管理器的编号一致，便于对照）。</summary>
    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>核心选择器里的一个物理核心卡片（标题 + 类别徽标 + 线程勾选框组）。</summary>
public sealed class CorePickerCore
{
    /// <summary>卡片标题，取该组首个逻辑处理器的拓扑显示名（如 "P0" / "E3" / "C5"）。</summary>
    public required string Title { get; init; }

    /// <summary>P/E 类别徽标文案；同构核为空串（不给 AMD / Intel 非混合冒充大小核）。</summary>
    public string ClassBadge { get; init; } = string.Empty;

    public bool IsPerformance { get; init; }

    public bool IsEfficiency { get; init; }

    /// <summary>徽标可见性（空徽标折叠，不留占位）。</summary>
    public Visibility BadgeVisibility =>
        string.IsNullOrEmpty(ClassBadge) ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<CorePickerThread> Threads { get; init; } = [];
}
