namespace CoreBusy.App.Controls;

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CoreBusy.App.Themes;
using CoreBusy.Core.Models;

/// <summary>实物 CPU 封装肖像。冻结矢量图在卡片间共享，保持真实长宽比，无常驻动画。</summary>
public sealed class CoreEvolutionVisual : FrameworkElement
{
    public static readonly DependencyProperty StageProperty = DependencyProperty.Register(
        nameof(Stage), typeof(int), typeof(CoreEvolutionVisual),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, null, (_, value) => Math.Clamp((int)value, 0, 4)));
    public static readonly DependencyProperty VendorProperty = DependencyProperty.Register(
        nameof(Vendor), typeof(CpuVendor), typeof(CoreEvolutionVisual),
        new FrameworkPropertyMetadata(CpuVendor.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsSilhouetteProperty = DependencyProperty.Register(
        nameof(IsSilhouette), typeof(bool), typeof(CoreEvolutionVisual),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EvolutionVersionProperty = DependencyProperty.Register(
        nameof(EvolutionVersion), typeof(int), typeof(CoreEvolutionVisual), new PropertyMetadata(0, OnEvolutionChanged));
    private static readonly DependencyProperty AssemblyProgressProperty = DependencyProperty.Register(
        "AssemblyProgress", typeof(double), typeof(CoreEvolutionVisual),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Stage { get => (int)GetValue(StageProperty); set => SetValue(StageProperty, value); }
    public CpuVendor Vendor { get => (CpuVendor)GetValue(VendorProperty); set => SetValue(VendorProperty, value); }
    public bool IsSilhouette { get => (bool)GetValue(IsSilhouetteProperty); set => SetValue(IsSilhouetteProperty, value); }
    public int EvolutionVersion { get => (int)GetValue(EvolutionVersionProperty); set => SetValue(EvolutionVersionProperty, value); }
    internal Rect PortraitBounds { get; private set; }

    // 仅由 WPF Dispatcher 访问。共享冻结图像，EXP 采样不重建封装几何。
    private static readonly Dictionary<(CpuVendor, int), DrawingImage> Portraits = new();
    private static readonly Brush Board = Solid("#216749"), Brown = Solid("#79573E"), Dark = Solid("#23352B");
    private static readonly Brush Gold = Solid("#D4B766"), Etch = Solid("#46534B"), Highlight = Solid("#E5ECE6");
    private static readonly Brush MetalEdge = Solid("#829087"), Metal = MakeMetal();

    public CoreEvolutionVisual()
    {
        IsHitTestVisible = false;
        Unloaded += (_, _) => BeginAnimation(AssemblyProgressProperty, null);
    }

    private static void OnEvolutionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var visual = (CoreEvolutionVisual)sender;
        if (!visual.IsLoaded || visual.IsSilhouette || (int)e.NewValue <= (int)e.OldValue || !SystemParameters.ClientAreaAnimation) return;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) => visual.BeginAnimation(AssemblyProgressProperty, null);
        visual.BeginAnimation(AssemblyProgressProperty, animation);
    }

    internal static DrawingImage PortraitFor(CpuVendor vendor, int stage)
    {
        stage = Math.Clamp(stage, 0, 4);
        if (vendor is not (CpuVendor.Intel or CpuVendor.Amd)) { vendor = CpuVendor.Unknown; stage = 0; }
        if (!Portraits.TryGetValue((vendor, stage), out var image))
        {
            var group = new DrawingGroup();
            using (var dc = group.Open()) DrawPortrait(dc, vendor, stage);
            group.Freeze(); image = new DrawingImage(group); image.Freeze();
            Portraits.Add((vendor, stage), image);
        }
        return image;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var image = PortraitFor(Vendor, Stage);
        var scale = Math.Min(ActualWidth / image.Width, ActualHeight / image.Height) * .96;
        var width = image.Width * scale; var height = image.Height * scale;
        PortraitBounds = new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
        var progress = (double)GetValue(AssemblyProgressProperty);
        dc.PushTransform(new TranslateTransform(0, (1 - progress) * 7));
        dc.PushOpacity(IsSilhouette ? .45 : .4 + .6 * progress);
        // 预告保留低对比度的封装细节；方形 CPU 的纯色剪影会退化成没有辨识度的方块。
        dc.DrawImage(image, PortraitBounds);
        dc.Pop(); dc.Pop();
    }

    private static void DrawPortrait(DrawingContext dc, CpuVendor vendor, int stage)
    {
        var form = CpuEvolutionCatalog.For(vendor, stage); var w = form.Width; var h = form.Height;
        var xp = vendor == CpuVendor.Amd && stage == 0;
        dc.DrawRoundedRectangle(xp ? Brown : Board, new Pen(Dark, .6), new Rect(0, 0, w, h), .7, .7);
        Polygon(dc, Gold, null, (1, h - 1), (3, h - 1), (1, h - 3));
        if (vendor == CpuVendor.Unknown)
        {
            Cover(dc, 5, 5, w - 10, h - 10); Label(dc, ["CPU"], w / 2, h / 2 - 2, 4); return;
        }
        if (xp)
        {
            foreach (var x in new[] { w * .16, w * .84 })
                foreach (var y in new[] { h * .16, h * .84 }) dc.DrawEllipse(Dark, null, new Point(x, y), 3.2, 3.2);
            for (var row = 0; row < 4; row++) for (var col = 0; col < 5; col++)
            {
                dc.DrawRectangle(Gold, null, new Rect(w / 2 + 13 + row * 1.8, h / 2 - 12 + col * 1.8, .45, .45));
                dc.DrawRectangle(Gold, null, new Rect(w / 2 - 9 + col * 2.2, h / 2 + 12 + row * 1.4, .45, .45));
            }
            dc.DrawRectangle(Dark, null, new Rect(w / 2 - 6.5, h / 2 - 6.5, 13, 13));
            var die = new LinearGradientBrush(Color.FromRgb(82, 107, 135), Color.FromRgb(51, 47, 74), 45); die.Freeze();
            dc.DrawRectangle(die, new Pen(MetalEdge, .3), new Rect(w / 2 - 5.8, h / 2 - 5.8, 11.6, 11.6));
            Label(dc, ["AMD"], w / 2, 8, 5, Gold); return;
        }
        if (vendor == CpuVendor.Amd && stage == 3)
        {
            // AM5 顶盖的八个支脚与缺口；正面不绘制底部针脚。
            var q = 16.5; var a = 10; var b = 4.5; var n = 12;
            (double, double)[] points = [(-q,-q),(-a,-q),(-a,-n),(-b,-n),(-b,-q),(b,-q),(b,-n),(a,-n),(a,-q),(q,-q),
                (q,-a),(n,-a),(n,-b),(q,-b),(q,b),(n,b),(n,a),(q,a),(q,q),
                (a,q),(a,n),(b,n),(b,q),(-b,q),(-b,n),(-a,n),(-a,q),(-q,q),
                (-q,a),(-n,a),(-n,b),(-q,b),(-q,-b),(-n,-b),(-n,-a),(-q,-a)];
            dc.PushTransform(new TranslateTransform(w / 2, h / 2));
            foreach (var x in new[] { -16.0, 13.0 }) foreach (var y in new[] { -7.0, 6.0 }) Cap(dc, x, y, 3, 1.4);
            MetalShape(dc, points); Label(dc, ["AMD"], 0, -3, 5); dc.Pop(); return;
        }
        if (vendor == CpuVendor.Intel && stage == 4)
        {
            var x = 1.0; var y = 1.0; var l = w - 2; var t = h - 2;
            MetalShape(dc, [(x+2,y),(x+15,y),(x+17,y+4),(x+l-17,y+4),(x+l-15,y),(x+l-2,y),
                (x+l,y+2),(x+l,y+t-2),(x+l-2,y+t),(x+l-15,y+t),(x+l-17,y+t-4),
                (x+17,y+t-4),(x+15,y+t),(x+2,y+t),(x,y+t-2),(x,y+2)]);
            for (var i = 0; i < 7; i++) Cap(dc, w / 2 - 9 + i * 2.7, 1.3, 1.8, 2.2);
            for (var i = 0; i < 3; i++) Cap(dc, w / 2 - 4 + i * 2.7, h - 3.5, 1.8, 2.2);
            Label(dc, ["INTEL"], w / 2, h / 2 - 3, 5); return;
        }
        var iw = w - 6; var ih = h - 6;
        if (vendor == CpuVendor.Intel)
        {
            if (stage == 0) { iw = ih = 23; for (var i = 0; i < 4; i++) Cap(dc, 1, h / 2 - 8 + i * 5, 1.7, 2.4); }
            else if (stage == 1) iw = ih = 30;
            else if (stage == 2) { iw = w - 4; ih = h - 9; }
            else { iw = 29; ih = 37; }
        }
        else if (stage == 4) { iw = w - 5; ih = h - 5; }
        else iw = ih = 35;
        var left = (w - iw) / 2; var top = (h - ih) / 2;
        if (vendor == CpuVendor.Amd && stage == 2)
            MetalShape(dc, [(left+3,top),(left+iw-3,top),(left+iw-3,top+2),(left+iw,top+2),
                (left+iw,top+ih-2),(left+iw-3,top+ih-2),(left+iw-3,top+ih),(left+3,top+ih),
                (left+3,top+ih-2),(left,top+ih-2),(left,top+2),(left+3,top+2)]);
        else Cover(dc, left, top, iw, ih);
        Label(dc, [vendor == CpuVendor.Intel ? "INTEL" : "AMD"], w / 2, h / 2 - 3, 5);
    }

    private static void Cover(DrawingContext dc, double x, double y, double w, double h)
    {
        dc.DrawRoundedRectangle(MetalEdge, null, new Rect(x, y + 1, w, h), 1.5, 1.5);
        dc.DrawRoundedRectangle(Metal, new Pen(Highlight, .4), new Rect(x, y, w, h), 1.5, 1.5);
    }
    private static void MetalShape(DrawingContext dc, (double X, double Y)[] points)
    {
        dc.PushTransform(new TranslateTransform(0, 1)); Polygon(dc, MetalEdge, null, points); dc.Pop();
        Polygon(dc, Metal, new Pen(Highlight, .4), points);
    }
    private static void Polygon(DrawingContext dc, Brush fill, Pen? pen, params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(points[0].X, points[0].Y), true, true);
            path.PolyLineTo(points.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, false);
        }
        geometry.Freeze(); dc.DrawGeometry(fill, pen, geometry);
    }
    private static void Cap(DrawingContext dc, double x, double y, double w, double h)
    {
        dc.DrawRectangle(Dark, null, new Rect(x - .2, y - .2, w + .4, h + .4));
        dc.DrawRectangle(MetalEdge, null, new Rect(x, y, w, h));
        dc.DrawRectangle(Highlight, null, new Rect(x, y, .5, h));
        dc.DrawRectangle(Highlight, null, new Rect(x + w - .5, y, .5, h));
    }
    private static void Label(DrawingContext dc, string[] lines, double x, double y, double size, Brush? brush = null)
    {
        foreach (var line in lines)
        {
            var text = new FormattedText(line, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush ?? Etch, 1);
            dc.DrawText(text, new Point(x - text.Width / 2, y)); y += size * 1.45;
        }
    }
    private static Brush MakeMetal()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (offset, color) in new[] { (0.0, "#D8DFD9"), (.22, "#F1F3F1"), (.48, "#B5BFBA"), (.7, "#D8DFD9"), (1.0, "#87968D") })
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        brush.Freeze(); return brush;
    }
    private static Brush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush;
    }
}
