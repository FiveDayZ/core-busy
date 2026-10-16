namespace CoreBusy.App.Themes;

using System.Windows.Media;
using CoreBusy.Core.Progression;

/// <summary>
/// 主题相关的控件级用色（核心 Tile 底色/边框等）。
/// 由 <see cref="BrandTheme.Apply"/> 在主窗口构建前装配，
/// ViewModel 通过静态属性读取，避免在每帧刷新里重复创建画刷。
/// </summary>
public static class UiTheme
{
    public static Brush TileCardBrush { get; private set; } = Frozen("#1B232C");
    public static Brush TileBorderBrush { get; private set; } = Frozen("#212B35");
    public static Brush TileDangerCardBrush { get; private set; } = Frozen("#2A2126");
    public static Brush TileDangerBorderBrush { get; private set; } = Frozen("#E56570");

    /// <summary>
    /// 成长阶段描边：兼容既有调用；主卡以真实 CPU 封装表现阶段。
    /// 与负载告警分离；CoreEvolutionVisual 根据硬件厂商选择实物原型。
    /// </summary>
    private static Brush[] _stageBorders =
        [TileBorderBrush, Frozen("#91A6AE"), Frozen("#80B3CB"), Frozen("#71CFFF"), Frozen("#E8C56A")];

    /// <summary>
    /// 成长阶段索引 0–4：对应 Intel / AMD 各自的五款真实 CPU 原型。
    /// 边界与 <see cref="CoreLevelVm.StageText"/> 逐档一致（10 / 40 / 75 / 99），
    /// 单核边框与整板铜层都从这里取阶段，杜绝两处口径漂移。
    /// </summary>
    public static int StageIndexOf(int level)
        => CoreProgressionRules.FormIndexOf(level);

    /// <summary>按等级取阶段边框（边界由 CoreProgressionRules 统一提供）。</summary>
    public static Brush StageBorderFor(int level) => _stageBorders[StageIndexOf(level)];

    internal static void Apply(
        string tileCard, string tileBorder, string tileDangerCard, string tileDangerBorder,
        string accent)
    {
        TileCardBrush = Frozen(tileCard);
        TileBorderBrush = Frozen(tileBorder);
        TileDangerCardBrush = Frozen(tileDangerCard);
        TileDangerBorderBrush = Frozen(tileDangerBorder);

        var c = (Color)ColorConverter.ConvertFromString(accent);
        Brush Tint(byte alpha)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        _stageBorders =
        [
            TileBorderBrush,
            Frozen("#91A6AE"),
            Tint(0xDD),
            Tint(0xFF),
            Frozen("#E8C56A"),
        ];
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
