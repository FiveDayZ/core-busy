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

    internal static void Apply(
        string tileCard, string tileBorder, string tileDangerCard, string tileDangerBorder)
    {
        TileCardBrush = Frozen(tileCard);
        TileBorderBrush = Frozen(tileBorder);
        TileDangerCardBrush = Frozen(tileDangerCard);
        TileDangerBorderBrush = Frozen(tileDangerBorder);
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
