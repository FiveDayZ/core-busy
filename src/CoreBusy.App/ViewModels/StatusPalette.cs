namespace CoreBusy.App.ViewModels;

using System.Windows.Media;
using CoreBusy.Core.Models;

/// <summary>负载状态配色（浅色主题：由冷到热渐变）。Brush 冻结并缓存复用，避免采样周期内反复创建实例造成 GC 压力。</summary>
public static class StatusPalette
{
    private static readonly Dictionary<CoreStatus, Brush> BackgroundCache = new();
    private static readonly Dictionary<CoreStatus, Brush> ForegroundCache = new();

    public static Brush Background(CoreStatus status)
    {
        if (BackgroundCache.TryGetValue(status, out var cached))
            return cached;

        var brush = Frozen(status switch
        {
            CoreStatus.Slacking => "#F5F5F5",
            CoreStatus.Idle => "#E8F5E9",
            CoreStatus.Working => "#FFF9C4",
            CoreStatus.Busy => "#FFE0B2",
            CoreStatus.HighLoad => "#FFCCBC",
            CoreStatus.Overloaded => "#FFCDD2",
            _ => "#EEEEEE",
        });
        BackgroundCache[status] = brush;
        return brush;
    }

    public static Brush Foreground(CoreStatus status)
    {
        if (ForegroundCache.TryGetValue(status, out var cached))
            return cached;

        var brush = Frozen(status switch
        {
            CoreStatus.Slacking => "#616161",
            CoreStatus.Idle => "#2E7D32",
            CoreStatus.Working => "#F57F17",
            CoreStatus.Busy => "#E65100",
            CoreStatus.HighLoad => "#BF360C",
            CoreStatus.Overloaded => "#B71C1C",
            _ => "#424242",
        });
        ForegroundCache[status] = brush;
        return brush;
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
