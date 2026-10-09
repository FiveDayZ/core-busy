namespace CoreBusy.App.Themes;

using System.Windows;
using System.Windows.Media;
using CoreBusy.App.Controls;
using CoreBusy.Core.Models;

/// <summary>
/// CPU 品牌主题装配（Fluent 2 深色令牌）。
/// <para>
/// 视觉层次由<b>明度分层</b>承担：背景 #1A1A1A → 面板 #232323 → 卡片 #2B2B2B → 描边 #3D3D3D。
/// 品牌差异只体现在<b>强调色</b>（AMD 红 / Intel 蓝 / 中性青），不再使用径向辉光，
/// 面板也不再半透明 —— 后者会让背景色透进内容区，把层次压平。
/// </para>
/// 高负载（≥85%）在三种品牌下都用红色表达，保持"危险"语义一致。
/// 必须在主窗口构建前调用 Apply（窗口 XAML 中的 StaticResource 在构建时解析）。
/// </summary>
public static class BrandTheme
{
    // ===== 中性基底（三品牌共用） =====
    private const string BgHex = "#1A1A1A";
    private const string PanelHex = "#232323";
    private const string CardHex = "#2B2B2B";
    private const string HoverHex = "#333333";
    private const string BorderHex = "#3D3D3D";
    private const string BorderWeakHex = "#2E2E2E";
    private const string TrackHex = "#3A3A3A";
    private const string TextPrimaryHex = "#F2F2F2";
    private const string TextSecondaryHex = "#A6A6A6";
    private const string TextWeakHex = "#737373";

    // ===== 语义色（跨品牌一致） =====
    private const string BlueHex = "#6FA8DC";
    private const string GreenHex = "#4EC48F";
    private const string YellowHex = "#E8C044";
    private const string OrangeHex = "#E8934A";
    private const string DangerHex = "#E5484D";

    /// <summary>
    /// 热力图 7 档色带：冷灰 → 橄榄 → 琥珀 → 橙 → 红。
    /// 语义化（越热越红），不随品牌变化；最低档刻意亮于卡片底色，空闲时网格仍可辨。
    /// </summary>
    private static readonly string[] HeatRamp =
    [
        "#333A42", "#46525E", "#6E6338", "#98801F", "#C08A22", "#DC6A28", "#E5484D",
    ];

    private static readonly string[] GlowKeys =
    [
        "CoreBusyGlow1Brush", "CoreBusyGlow2Brush", "CoreBusyGlow3Brush",
    ];

    /// <summary>当前生效的品牌（Auto 模式下即 CPU 厂商；显式主题下为主题对应厂商）。</summary>
    public static CpuVendor EffectiveVendor { get; private set; } = CpuVendor.Intel;

    /// <summary>当前是否 AMD 品牌。</summary>
    public static bool IsAmd => EffectiveVendor == CpuVendor.Amd;

    /// <summary>是否为未知厂商（Neutral 主题）。</summary>
    public static bool IsNeutral => EffectiveVendor == CpuVendor.Unknown;

    /// <summary>当前品牌标识文本（AMD / INTEL / CPU）。</summary>
    public static string BrandName { get; private set; } = "INTEL";

    /// <summary>状态栏品牌版本文案（AMD EDITION / Intel Edition / Standard Edition）。</summary>
    public static string EditionText { get; private set; } = "Intel Edition";

    /// <summary>当前主题名（用于设置回显与日志）。</summary>
    public static string ThemeName { get; private set; } = "Intel Blue";

    /// <summary>
    /// 应用主题。Auto 模式按 CPU 厂商切换（Intel / AMD / 未知→Neutral）；
    /// 显式模式忽略厂商强制锁定主题。
    /// </summary>
    public static void Apply(string vendor, ThemeMode mode = ThemeMode.Auto)
    {
        // 调试用：COREBUSY_FORCE_BRAND=amd|intel|neutral 可在任意机器上预览其它主题。
        var forced = Environment.GetEnvironmentVariable("COREBUSY_FORCE_BRAND");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            mode = forced.StartsWith("amd", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Amd
                : forced.StartsWith("intel", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Intel
                : ThemeMode.Auto;

            if (mode == ThemeMode.Auto)
                vendor = "unknown";
        }

        EffectiveVendor = mode switch
        {
            ThemeMode.Intel => CpuVendor.Intel,
            ThemeMode.Amd => CpuVendor.Amd,
            _ => vendor.ToCpuVendor(),
        };

        BrandName = EffectiveVendor switch
        {
            CpuVendor.Amd => "AMD",
            CpuVendor.Intel => "INTEL",
            _ => "CPU",
        };

        EditionText = EffectiveVendor switch
        {
            CpuVendor.Amd => "AMD EDITION",
            CpuVendor.Intel => "Intel Edition",
            _ => "Standard Edition",
        };

        ThemeName = EffectiveVendor switch
        {
            CpuVendor.Amd => "AMD Red",
            CpuVendor.Intel => "Intel Blue",
            _ => "Neutral",
        };

        // 三品牌共用中性层次基底，仅强调色不同。
        var (accent, accentDim) = EffectiveVendor switch
        {
            CpuVendor.Amd => ("#E0524A", "#33E0524A"),
            CpuVendor.Intel => ("#4CA0E0", "#334CA0E0"),
            _ => ("#5AB8E8", "#335AB8E8"),
        };

        ApplyNeutralBase(accent, accentDim, accent);
    }

    /// <summary>
    /// 装配中性层次基底。三品牌共用同一套明度分层，只替换强调色。
    /// </summary>
    private static void ApplyNeutralBase(string accent, string accentDim, string brand)
    {
        var r = Application.Current.Resources;

        r["CoreBusyBackgroundBrush"] = Frozen(BgHex);
        r["CoreBusyPanelBrush"] = Frozen(PanelHex);
        r["CoreBusyCardBrush"] = Frozen(CardHex);
        r["CoreBusyHoverBrush"] = Frozen(HoverHex);

        r["CoreBusyBorderBrush"] = Frozen(BorderHex);
        r["CoreBusyBorderWeakBrush"] = Frozen(BorderWeakHex);
        r["CoreBusyTrackBrush"] = Frozen(TrackHex);

        r["CoreBusyAccentBrush"] = Frozen(accent);
        r["CoreBusyAccentDimBrush"] = Frozen(accentDim);
        r["CoreBusyBlueBrush"] = Frozen(BlueHex);
        r["CoreBusyGreenBrush"] = Frozen(GreenHex);
        r["CoreBusyYellowBrush"] = Frozen(YellowHex);
        r["CoreBusyOrangeBrush"] = Frozen(OrangeHex);
        r["CoreBusyDangerBrush"] = Frozen(DangerHex);

        r["CoreBusyTextPrimaryBrush"] = Frozen(TextPrimaryHex);
        r["CoreBusyTextSecondaryBrush"] = Frozen(TextSecondaryHex);
        r["CoreBusyTextWeakBrush"] = Frozen(TextWeakHex);

        r["CoreBusyBlueDimBrush"] = Frozen("#266FA8DC");
        r["CoreBusyGreenDimBrush"] = Frozen("#264EC48F");
        r["CoreBusyYellowDimBrush"] = Frozen("#26E8C044");
        r["CoreBusyOrangeDimBrush"] = Frozen("#26E8934A");
        r["CoreBusyDangerDimBrush"] = Frozen("#33E5484D");
        r["CoreBusyTileDangerBrush"] = Frozen("#33262A");

        // 辉光已废弃：保留资源键但置空，避免任何残留引用解析失败。
        foreach (var key in GlowKeys)
            r[key] = Frozen("#00000000");

        UiTheme.Apply(
            tileCard: CardHex,
            tileBorder: BorderWeakHex,
            tileDangerCard: "#33262A",
            tileDangerBorder: DangerHex,
            accent: accent);

        SetHeat(r, HeatRamp);

        // 负载色阶：<50% 中性钢灰（低负载不抢视觉）→ 50-70% 琥珀 → 70-85% 橙 → ≥85% 红。
        UsagePalette.Apply(
            tier0: "#8C97A3",
            tier1: YellowHex,
            tier2: OrangeHex,
            danger: DangerHex,
            t1: 50,
            t2: 70,
            t3: 85,
            brand: brand,
            textNeutral: TextPrimaryHex,
            heat: HeatRamp);
    }

    // ============================ 辅助 ============================

    private static void SetHeat(ResourceDictionary r, string[] hex)
    {
        for (var i = 0; i < hex.Length; i++)
            r[$"CoreBusyHeat{i}Brush"] = Frozen(hex[i]);
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
