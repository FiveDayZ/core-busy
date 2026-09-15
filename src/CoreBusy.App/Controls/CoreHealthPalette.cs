namespace CoreBusy.App.Controls;

using System.Windows.Media;
using CoreBusy.Core.Health;

/// <summary>
/// 健康度评级 → 画刷映射（v1.17.0）。与 <see cref="UsagePalette"/> 同一约定：
/// 全部画刷**在类型初始化时一次性建好并冻结**，逐帧刷新零分配。
/// <para>
/// 配色刻意与**负载色阶解耦**：负载用"冷 → 金 → 橙 → 红"表示"忙不忙"，
/// 健康度用"绿 → 蓝 → 琥珀 → 红"表示"好不好"。两者共用一套色带会造成
/// "满载 = 红色 = 不健康"的误读 —— 满载本身完全正常，健康度才是异常与否的判据。
/// </para>
/// </summary>
public static class CoreHealthPalette
{
    private static readonly Brush GoodFg = Make("#2E8B57");
    private static readonly Brush NormalFg = Make("#2E7BC4");
    private static readonly Brush WatchFg = Make("#C77D0A");
    private static readonly Brush PoorFg = Make("#D1453B");
    private static readonly Brush UnknownFg = Make("#8A94A2");

    private static readonly Brush GoodBg = Make("#1A2E8B57");
    private static readonly Brush NormalBg = Make("#1A2E7BC4");
    private static readonly Brush WatchBg = Make("#1FC77D0A");
    private static readonly Brush PoorBg = Make("#1FD1453B");
    private static readonly Brush UnknownBg = Make("#1A8A94A2");

    /// <summary>评级 → 前景（文字 / 数值）色。</summary>
    public static Brush Foreground(CoreHealthGrade grade) => grade switch
    {
        CoreHealthGrade.Good => GoodFg,
        CoreHealthGrade.Normal => NormalFg,
        CoreHealthGrade.Watch => WatchFg,
        CoreHealthGrade.Poor => PoorFg,
        _ => UnknownFg,
    };

    /// <summary>评级 → 半透明底色（小徽章背景）。</summary>
    public static Brush Background(CoreHealthGrade grade) => grade switch
    {
        CoreHealthGrade.Good => GoodBg,
        CoreHealthGrade.Normal => NormalBg,
        CoreHealthGrade.Watch => WatchBg,
        CoreHealthGrade.Poor => PoorBg,
        _ => UnknownBg,
    };

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
