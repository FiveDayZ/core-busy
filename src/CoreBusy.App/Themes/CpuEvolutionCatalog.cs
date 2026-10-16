namespace CoreBusy.App.Themes;

using CoreBusy.Core.Models;

/// <summary>2000 年之后的实物封装原型。图鉴形态不改变本机硬件、经验或性能。</summary>
public sealed record CpuEvolutionForm(string Name, string Model, int Year, string Socket, double Width, double Height);

public static class CpuEvolutionCatalog
{
    private static readonly CpuEvolutionForm[] Intel =
    [
        new("Pentium 4", "2.0A · Northwood", 2002, "Socket 478", 35, 35),
        new("Core 2 Duo", "E6600", 2006, "LGA775", 37.5, 37.5),
        new("Core i7", "i7-3960X", 2011, "LGA2011", 45, 52.5),
        new("Core i9", "i9-12900K", 2021, "LGA1700", 37.5, 45),
        new("Xeon W", "w9-3495X", 2023, "LGA4677", 56.5, 77.5),
    ];
    private static readonly CpuEvolutionForm[] Amd =
    [
        new("Athlon XP", "1800+ · Palomino", 2001, "Socket A", 49.5, 49.5),
        new("Athlon 64", "3200+ · ClawHammer", 2003, "Socket 754", 40, 40),
        new("Ryzen 7", "1800X", 2017, "AM4", 40, 40),
        new("Ryzen 9", "7950X", 2022, "AM5", 40, 40),
        new("TR PRO", "7995WX", 2023, "sTR5", 75.4, 58.5),
    ];
    private static readonly CpuEvolutionForm Unknown = new("CPU", "厂商未识别", 0, "通用封装", 40, 40);

    public static CpuEvolutionForm For(CpuVendor vendor, int stage) => vendor switch
    {
        CpuVendor.Intel => Intel[Math.Clamp(stage, 0, 4)],
        CpuVendor.Amd => Amd[Math.Clamp(stage, 0, 4)],
        _ => Unknown,
    };
}
