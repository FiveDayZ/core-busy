namespace CoreBusy.App.ViewModels.Dashboard;

using System.Windows.Media;
using CoreBusy.App.Controls;
using CoreBusy.App.Infrastructure;

/// <summary>主要负载进程行视图模型。</summary>
public sealed class ProcessRowVm : ObservableObject
{
    private static readonly Brush GameBrush = Frozen("#F2504F");
    private static readonly Brush BrowserBrush = Frozen("#F2C84E");
    private static readonly Brush SystemBrush = Frozen("#3B8FE0");
    private static readonly Brush ChatBrush = Frozen("#9E7BFF");
    private static readonly Brush SteamBrush = Frozen("#3FC9A0");
    private static readonly Brush AppBrush = Frozen("#22B4E8");

    private string _usageText = "0%";
    private double _barValue;

    public ProcessRowVm(string name, double usage, string iconKind)
    {
        Name = name;
        (IconBrush, IconDimBrush, IconText) = IconFor(iconKind);
        Update(usage);
    }

    public string Name { get; }

    public Brush IconBrush { get; }

    public Brush IconDimBrush { get; }

    public string IconText { get; }

    public string UsageText
    {
        get => _usageText;
        private set => SetProperty(ref _usageText, value);
    }

    /// <summary>水平条比例（最大 40% 满条）。</summary>
    public double BarValue
    {
        get => _barValue;
        private set => SetProperty(ref _barValue, value);
    }

    /// <summary>进度条颜色：品牌强调色（AMD 红 / Intel 青）。</summary>
    public Brush BarBrush => UsagePalette.BrandBrush;

    public void Update(double usage)
    {
        UsageText = $"{usage:0.0}%";
        BarValue = usage;
    }

    private static (Brush, Brush, string) IconFor(string kind) => kind switch
    {
        "game" => (GameBrush, Frozen("#40F2504F"), "G"),
        "browser" => (BrowserBrush, Frozen("#40F2C84E"), "C"),
        "system" => (SystemBrush, Frozen("#403B8FE0"), "S"),
        "chat" => (ChatBrush, Frozen("#409E7BFF"), "D"),
        "steam" => (SteamBrush, Frozen("#403FC9A0"), "S"),
        _ => (AppBrush, Frozen("#4022B4E8"), "A"),
    };

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
