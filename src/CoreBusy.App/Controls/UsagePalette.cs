namespace CoreBusy.App.Controls;

using System.Windows.Media;
using CoreBusy.Core;
using CoreBusy.Core.Models;

/// <summary>
/// 负载 → 颜色/状态 画刷映射。色阶随 CPU 品牌变化（由 Themes.BrandTheme 装配）：
/// <list type="bullet">
///   <item>AMD：低负载偏冷白，中段金色，高段橙，≥80% 红。</item>
///   <item>Intel：低负载青蓝，中段金色，≥80% 红。</item>
/// </list>
/// 高负载统一为红色（危险语义跨品牌一致）。全部画刷冻结并缓存共享，高频刷新零分配。
/// </summary>
public static class UsagePalette
{
    // ===== 负载分档画刷（品牌装配，见 Apply） =====
    private static Brush _tier0 = Make("#29B6E8"); // 极低
    private static Brush _tier1 = Make("#29B6E8"); // 低
    private static Brush _tier2 = Make("#F2C84E"); // 中
    private static Brush _danger = Make("#F2505E"); // 高
    private static int _t1 = 50, _t2 = 50, _t3 = 80;

    // 与上面四档一一对应的半透明底色（角标 / 徽章底）。同样随品牌装配。
    private static Brush _tierDim0 = Make("#3329B6E8");
    private static Brush _tierDim1 = Make("#3329B6E8");
    private static Brush _tierDim2 = Make("#33F2C84E");
    private static Brush _tierDimDanger = Make("#33F2505E");

    /// <summary>品牌强调色（进程条 / 中性进度条）。</summary>
    public static Brush BrandBrush { get; private set; } = Make("#22B4E8");

    /// <summary>低负载时的数值文字色（近白，避免数字随品牌色偏色）。</summary>
    private static Brush _textNeutral = Make("#EAF4FF");

    // ===== 热力图 7 档色带（品牌装配） =====
    private static Brush[] _heat =
    [
        Make("#00142E"), Make("#003A6E"), Make("#00508C"), Make("#0068AC"),
        Make("#0082C8"), Make("#00A0E0"), Make("#5CE8F0"),
    ];

    // ===== 状态标签用色（跨品牌通用语义） =====
    private static readonly Brush SlackingBrush = Make("#6FA8DC");
    private static readonly Brush IdleBrush = Make("#8A94A2");
    private static readonly Brush WorkingBrush = Make("#E8B44A");
    private static readonly Brush BusyBrush = Make("#F0A83C");
    private static readonly Brush HighLoadBrush = Make("#F2504F");
    private static readonly Brush OverloadedBrush = Make("#F2504F");

    // ===== 状态徽章底色缓存（避免每次刷新新建画刷） =====
    private static readonly Dictionary<CoreStatus, Brush> DimCache = new();

    /// <summary>由品牌主题装配负载色阶与热力图色带。必须在界面构建前调用。</summary>
    public static void Apply(
        string tier0, string tier1, string tier2, string danger,
        int t1, int t2, int t3, string brand, string textNeutral, string[] heat)
    {
        _tier0 = Make(tier0);
        _tier1 = Make(tier1);
        _tier2 = Make(tier2);
        _danger = Make(danger);
        _tierDim0 = Make(Dim(tier0));
        _tierDim1 = Make(Dim(tier1));
        _tierDim2 = Make(Dim(tier2));
        _tierDimDanger = Make(Dim(danger));
        _t1 = t1;
        _t2 = t2;
        _t3 = t3;
        BrandBrush = Make(brand);
        _textNeutral = Make(textNeutral);

        if (heat.Length == 7)
            _heat = [.. heat.Select(Make)];

        DimCache.Clear();
    }

    /// <summary>核心数值文本颜色：低负载保持近白，中高负载随色阶点亮。</summary>
    public static Brush UsageTextBrush(double usage)
        => usage < 50 ? _textNeutral : UsageBrush(usage);

    /// <summary>核心使用率 → 颜色（Tile 进度条 / 排行榜条 / 数值文本）。</summary>
    public static Brush UsageBrush(double usage) => usage switch
    {
        _ when usage < _t1 => _tier0,
        _ when usage < _t2 => _tier1,
        _ when usage < _t3 => _tier2,
        _ => _danger,
    };

    /// <summary>
    /// 负载档位的半透明底色（与 <see cref="UsageBrush"/> 同档同源）。
    /// 用于排名角标这类"底色 + 前景同色系"的小徽章；分档阈值与 <see cref="UsageBrush"/> 严格一致，
    /// 不会出现"文字是红色档、底色是黄色档"的错配。
    /// </summary>
    public static Brush UsageDimBrush(double usage) => usage switch
    {
        _ when usage < _t1 => _tierDim0,
        _ when usage < _t2 => _tierDim1,
        _ when usage < _t3 => _tierDim2,
        _ => _tierDimDanger,
    };

    /// <summary>
    /// 热力图单元格颜色。阈值针对真实负载分布调优（日常多在 5%-30%），
    /// 低区段加密保证空闲波动也能看出明暗变化：
    /// 0-8 / 8-20 / 20-35 / 35-55 / 55-75 / 75-90 / 90-100。
    /// </summary>
    public static Brush HeatBrush(double usage) => usage switch
    {
        < 8 => _heat[0],
        < 20 => _heat[1],
        < 35 => _heat[2],
        < 55 => _heat[3],
        < 75 => _heat[4],
        < 90 => _heat[5],
        _ => _heat[6],
    };

    /// <summary>状态 → 画刷。</summary>
    public static Brush StatusBrush(CoreStatus status) => status switch
    {
        CoreStatus.Slacking => SlackingBrush,
        CoreStatus.Idle => IdleBrush,
        CoreStatus.Working => WorkingBrush,
        CoreStatus.Busy => BusyBrush,
        CoreStatus.HighLoad => HighLoadBrush,
        CoreStatus.Overloaded => OverloadedBrush,
        _ => SlackingBrush,
    };

    /// <summary>状态 → 半透明底色画刷（徽章背景，冻结缓存）。</summary>
    public static Brush StatusDimBrush(CoreStatus status)
    {
        if (DimCache.TryGetValue(status, out var cached))
            return cached;

        var brush = Make(status switch
        {
            CoreStatus.Slacking => "#336FA8DC",
            CoreStatus.Idle => "#338A94A2",
            CoreStatus.Working => "#33E8B44A",
            CoreStatus.Busy => "#33F0A83C",
            CoreStatus.HighLoad => "#40F2504F",
            CoreStatus.Overloaded => "#40F2504F",
            _ => "#336FA8DC",
        });
        DimCache[status] = brush;
        return brush;
    }

    /// <summary>状态 → 表情图标（徽章内的小图标，见设计稿）。</summary>
    public static string StatusEmoji(CoreStatus status) => status switch
    {
        CoreStatus.Slacking => "😴",
        CoreStatus.Idle => "💤",
        CoreStatus.Working => "💼",
        CoreStatus.Busy => "👷",
        CoreStatus.HighLoad => "🔥",
        CoreStatus.Overloaded => "🔥",
        _ => "😴",
    };

    /// <summary>状态 → 中文标签。</summary>
    public static string StatusLabel(CoreStatus status) => CoreStatusClassifier.ToLabel(status);

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>把 #RRGGBB 转成 20% 不透明的 #33RRGGBB。已是 ARGB 或非法格式则原样返回。</summary>
    private static string Dim(string hex)
        => hex.Length == 7 && hex[0] == '#' ? "#33" + hex[1..] : hex;
}
