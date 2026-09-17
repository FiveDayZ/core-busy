namespace CoreBusy.Core.Energy;

/// <summary>
/// 显卡在整机功耗模型里的角色（v1.20.1）。
/// </summary>
public enum GpuRole
{
    /// <summary>未能判别：既不按独显加功耗，也不声称它已含在封装里 —— 提示文案必须如实说明"未计入"。</summary>
    Unknown = 0,

    /// <summary>独立显卡：功耗在 CPU 封装之外，必须单独计（实测优先，否则按型号估算）。</summary>
    Discrete = 1,

    /// <summary>核显：与 CPU 同封装/同 die，功耗已经包含在封装功率读数里，**再加就是重复计**。</summary>
    Integrated = 2,
}

/// <summary>
/// 显卡「型号 → 额定功率」表与核显/独显判别（v1.20.1）。
/// <para>
/// <b>为什么需要它：</b>整机功耗的逐部件模型里，显卡是最大的一块，也是最有希望实测的一块 ——
/// 独显经 NVML（NVIDIA）/ ADL（AMD）在 LHM 里会暴露 <c>SensorType.Power</c>（如 <c>GPU Package</c>），
/// 可以直接读；但驱动未装、虚拟化、混合输出、或型号不在驱动覆盖范围内时读不到，
/// 此时按型号取额定功率再乘负载估算，而不是把整台机器退化成"读不到"。
/// </para>
/// <para>
/// <b>判别的后果不是显示好看与否，而是算不算重复计：</b>核显的功耗已在封装读数内，
/// 独显的功耗完全在封装之外。判错分别是"凭空多算一二十瓦"与"少算一两百瓦"。
/// </para>
/// <para>
/// <b>表的性质与局限：</b>数值是厂商公布的板卡功耗 / TGP（瓦），属公开规格而非实测；
/// 同型号非公版可有 ±20% 偏差；笔记本（名称含 Laptop / Mobile / Max-Q 等）的 TGP 由 OEM
/// 在 35–150 W 之间自由配置，故统一按 <see cref="MobileFactor"/> 折算 ——
/// 目的是**量级正确，不是精确**。
/// </para>
/// <para>
/// 查表规则：名称归一化（去 (R)/(TM)/(C)、折空白、转大写）后做<b>子串匹配</b>，
/// 长键优先（"RTX 4070 Ti" 必须先于 "RTX 4070" 命中，否则 Ti 型号会被判成基础型号）。
/// </para>
/// </summary>
public static class GpuRatedPower
{
    /// <summary>笔记本型号的额定功率折算比：OEM 可配 TGP 明显低于同型桌面版的板卡功耗。</summary>
    public const double MobileFactor = 0.6;

    /// <summary>笔记本型号的判别词（归一化后按子串匹配）。</summary>
    private static readonly string[] MobileMarkers =
        ["LAPTOP", "MOBILE", "MAX-Q", "NOTEBOOK", "M-GPU", "M GPU"];

    /// <summary>
    /// NVIDIA 独有的品牌词 —— NVIDIA 不做 x86 核显，命中即独显。
    /// 单独列出是为了兜住**型号表尚未收录**的新卡（例如未来的 "RTX 5090 Ti"）。
    /// </summary>
    private static readonly string[] NvidiaDiscreteMarkers =
        ["GEFORCE", "QUADRO", "TITAN", "RTX ", "GTX "];

    /// <summary>核显家族的判别词（归一化后）。"GRAPHICS" 覆盖 Radeon Graphics / UHD Graphics / Iris Xe Graphics / Arc Graphics（核显版）。</summary>
    private static readonly string[] IntegratedMarkers =
        ["GRAPHICS", "VEGA", "UHD", "IRIS", "GMA", "INTEL HD"];

    /// <summary>型号 → 额定功率（W，板卡功耗 / TGP）。</summary>
    private static readonly (string Key, double Watt)[] Rated =
    [
        // NVIDIA GeForce RTX 50 / 40 / 30 / 20、GTX 16 / 10
        ("RTX 5090", 575), ("RTX 5080", 360), ("RTX 5070 TI", 300), ("RTX 5070", 250),
        ("RTX 5060 TI", 180), ("RTX 5060", 145),
        ("RTX 4090", 450), ("RTX 4080 SUPER", 320), ("RTX 4080", 320),
        ("RTX 4070 TI SUPER", 285), ("RTX 4070 TI", 285), ("RTX 4070 SUPER", 220), ("RTX 4070", 200),
        ("RTX 4060 TI", 160), ("RTX 4060", 115),
        ("RTX 3090 TI", 450), ("RTX 3090", 350), ("RTX 3080 TI", 350), ("RTX 3080", 320),
        ("RTX 3070 TI", 290), ("RTX 3070", 220), ("RTX 3060 TI", 200), ("RTX 3060", 170), ("RTX 3050", 130),
        ("RTX 2080 TI", 260), ("RTX 2080 SUPER", 250), ("RTX 2080", 215),
        ("RTX 2070 SUPER", 215), ("RTX 2070", 175),
        ("RTX 2060 SUPER", 175), ("RTX 2060", 160),
        ("GTX 1660 SUPER", 125), ("GTX 1660 TI", 120), ("GTX 1660", 120),
        ("GTX 1650 SUPER", 100), ("GTX 1650", 75), ("GTX 1630", 30),
        ("GTX 1080 TI", 250), ("GTX 1080", 180), ("GTX 1070 TI", 180), ("GTX 1070", 150),
        ("GTX 1060", 120), ("GTX 1050 TI", 75), ("GTX 1050", 75), ("GT 1030", 30),

        // AMD Radeon RX 7000 / 6000 / 5000 / Vega / 500 / 400
        ("RX 7900 XTX", 355), ("RX 7900 XT", 315), ("RX 7900 GRE", 260),
        ("RX 7800 XT", 263), ("RX 7700 XT", 245), ("RX 7600 XT", 190), ("RX 7600", 165),
        ("RX 6950 XT", 335), ("RX 6900 XT", 300), ("RX 6800 XT", 300), ("RX 6800", 250),
        ("RX 6750 XT", 250), ("RX 6700 XT", 230), ("RX 6700", 175),
        ("RX 6650 XT", 180), ("RX 6600 XT", 160), ("RX 6600", 132),
        ("RX 6500 XT", 107), ("RX 6400", 53),
        ("RX 5700 XT", 225), ("RX 5700", 180), ("RX 5600 XT", 150), ("RX 5500 XT", 130),
        ("RX VEGA 64", 295), ("RX VEGA 56", 210),
        ("RX 590", 225), ("RX 580", 185), ("RX 570", 150), ("RX 560", 80), ("RX 550", 50),
        ("RX 480", 150), ("RX 470", 120), ("RX 460", 75),
        ("RADEON VII", 300),

        // Intel Arc（独显；核显版的 "Arc Graphics" 不含型号数字，不会命中本表）
        ("ARC B580", 190), ("ARC B570", 150),
        ("ARC A770", 225), ("ARC A750", 225), ("ARC A580", 185), ("ARC A380", 75),
    ];

    /// <summary>按长度降序的查表顺序（静态初始化一次，避免每次采样排序）。</summary>
    private static readonly (string Key, double Watt)[] OrderedByKeyLength =
        [.. Rated.OrderByDescending(entry => entry.Key.Length)];

    /// <summary>
    /// 判别显卡角色。判定次序（前面的信号强于后面的）：
    /// <list type="number">
    ///   <item>名称命中型号表 → 独显（表里只有独显型号）；</item>
    ///   <item>名称含 NVIDIA 品牌词 → 独显（NVIDIA 无 x86 核显）；</item>
    ///   <item>"ARC A"/"ARC B" + 数字 → 独显；</item>
    ///   <item>含 "VEGA" → 核显（真 Vega 独显已被第 1 条命中）；</item>
    ///   <item>"RX " + 数字 → 独显；</item>
    ///   <item>名称含核显家族词 → 核显；</item>
    ///   <item>都不命中 → <see cref="GpuRole.Unknown"/>（不猜测）。</item>
    /// </list>
    /// </summary>
    public static GpuRole Classify(string? name)
    {
        var text = Normalize(name);
        if (text.Length == 0)
            return GpuRole.Unknown;

        if (ResolveRatedWatt(text) is not null)
            return GpuRole.Discrete;

        foreach (var marker in NvidiaDiscreteMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
                return GpuRole.Discrete;
        }

        if ((text.Contains("ARC A", StringComparison.Ordinal) || text.Contains("ARC B", StringComparison.Ordinal))
            && HasDigit(text))
        {
            return GpuRole.Discrete;
        }

        // Vega 必须**先于**下面的 "RX + 数字" 规则判定：AMD 把移动端核显也命名为
        // "Radeon RX Vega 8/10/11 Graphics"（Ryzen 2000U/3000U 机型实测名称），
        // 它同时满足"含 RX "与"含数字"，会被误判成独显并凭空多算一二百瓦；
        // 而真正的 Vega 独显（RX Vega 56/64）已由上面的型号表先行命中。
        if (text.Contains("VEGA", StringComparison.Ordinal))
            return GpuRole.Integrated;

        // "RX 6600M" 这类"品牌 + 数字型号"：数字是型号而非核显的固定代号。
        // 只认这一个字母前缀，避免把 "Radeon 780M"（核显，型号里也有数字）误判成独显。
        if (text.Contains("RX ", StringComparison.Ordinal) && HasDigit(text))
            return GpuRole.Discrete;

        foreach (var marker in IntegratedMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
                return GpuRole.Integrated;
        }

        return GpuRole.Unknown;
    }

    /// <summary>名称是否指向独立显卡（<see cref="Classify"/> 的布尔视图，供状态栏排序复用）。</summary>
    public static bool LooksDiscrete(string? name) => Classify(name) == GpuRole.Discrete;

    /// <summary>
    /// 型号 → 额定功率（W）；名称未命中型号表时返回 null（由调用方回落兜底功率）。
    /// 笔记本型号按 <see cref="MobileFactor"/> 折算。
    /// </summary>
    public static double? ResolveRatedWatt(string? name)
    {
        var text = Normalize(name);
        if (text.Length == 0)
            return null;

        var mobile = false;
        foreach (var marker in MobileMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                mobile = true;
                break;
            }
        }

        foreach (var (key, watt) in OrderedByKeyLength)
        {
            if (text.Contains(key, StringComparison.Ordinal))
                return mobile ? watt * MobileFactor : watt;
        }

        return null;
    }

    private static bool HasDigit(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch))
                return true;
        }

        return false;
    }

    /// <summary>归一化：去商标标记、折空白、转大写（查表与判别的统一入口）。</summary>
    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw
            .Replace("(R)", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("(C)", " ", StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();

        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);

        return text.Trim();
    }
}
