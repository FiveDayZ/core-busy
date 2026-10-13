namespace CoreBusy.App.Themes;

using System.Windows.Media;

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
    /// 成长阶段边框阶梯（v1.27.2）：幼体 = 默认边框（新芯片不抢眼）；
    /// 成长期 / 成熟期 / 究极期 = 品牌强调色逐级提亮；传说 = 金（与 PCB 金引脚同源，品牌无关）。
    /// 高负载红框优先级更高（见 DashboardCoreVm.ApplyBorder）。
    /// </summary>
    private static Brush[] _stageBorders =
        [TileBorderBrush, TileBorderBrush, TileBorderBrush, TileBorderBrush, Frozen("#E8C56A")];

    /// <summary>
    /// 成长阶段索引（v1.32.0 抽取为单一边界源）：幼体0 / 成长期1 / 成熟期2 / 究极期3 / 传说4。
    /// 边界与 <see cref="CoreLevelVm.StageText"/> 逐档一致（10 / 30 / 60 / 99），
    /// 单核边框与整板铜层都从这里取阶段，杜绝两处口径漂移。
    /// </summary>
    public static int StageIndexOf(int level)
        => level switch { >= 99 => 4, >= 60 => 3, >= 30 => 2, >= 10 => 1, _ => 0 };

    /// <summary>按等级取阶段边框：幼体(1-9) / 成长期(10-29) / 成熟期(30-59) / 究极期(60-98) / 传说(99)。</summary>
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
            TileBorderBrush,   // 幼体：默认边框
            Tint(0x55),        // 成长期
            Tint(0x88),        // 成熟期
            Tint(0xCC),        // 究极期
            Frozen("#E8C56A"), // 传说：金
        ];
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
