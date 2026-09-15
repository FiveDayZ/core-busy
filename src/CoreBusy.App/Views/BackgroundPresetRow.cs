namespace CoreBusy.App.Views;

using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Models;

/// <summary>
/// 设置窗口「一键降级后台负载」中一个类别的一行（v1.13.0）。
/// </summary>
public sealed class BackgroundPresetRow : ObservableObject
{
    private bool _selected;

    public BackgroundPresetRow(BackgroundLoadCategory category)
    {
        Category = category;
        Title = $"{category.Title} · {category.ProcessNames.Count} 项";
    }

    /// <summary>类别定义。按钮回调按 <c>Category.Key</c> 取值，不依赖显示文本。</summary>
    public BackgroundLoadCategory Category { get; }

    /// <summary>列表显示文本，形如「容器与虚拟机 · 12 项」。</summary>
    public string Title { get; }

    /// <summary>悬停提示：说明这一类被降级之后会付出什么代价。</summary>
    public string Hint => Category.Hint;

    /// <summary>
    /// 是否勾选。只决定"点按钮时要不要把这一类加进规则列表"，**不落盘** ——
    /// 真正的配置是规则列表本身。若勾选状态也当配置存起来，就会出现
    /// "同一个事实有两个存放处"，改了一边忘了另一边时无从判断哪边是真相。
    /// </summary>
    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}
