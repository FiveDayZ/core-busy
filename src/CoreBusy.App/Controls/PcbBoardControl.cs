using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CoreBusy.App.Controls;

/// <summary>
/// PCB 主板背景（v1.26.0）：静态 OnRender 绘制的主板底板 —— 板基、铜走线、焊盘、
/// 过孔、安装孔与丝印。芯片角色卡直接"焊"在这块板上。
/// <para>
/// 只在尺寸变化时重画一次，无每帧开销 —— 软件渲染下任何每帧重绘都是卡顿来源，
/// 背景必须是纯静态的。不参与命中测试，鼠标事件穿透给上层内容。
/// </para>
/// </summary>
/// <remarks>
/// PCB 配色（深绿板基 + 铜绿走线 + 金色焊盘）是**物理隐喻色**，
/// 刻意不随主题切换 —— 真实主板就长这样。
/// </remarks>
public sealed class PcbBoardControl : FrameworkElement
{
    private static readonly Brush BoardBase = Frozen("#0B3226");
    private static readonly Brush BoardEdge = Frozen("#124634");
    private static readonly Brush Trace = Frozen("#1D5C45");
    private static readonly Brush TraceBright = Frozen("#2E7B5D");
    private static readonly Brush Pad = Frozen("#B9963F");
    private static readonly Brush PadInner = Frozen("#0B3226");
    private static readonly Brush HoleRing = Frozen("#8F9A93");
    private static readonly Brush HoleInner = Frozen("#07251C");
    private static readonly Brush Silk = Frozen("#3E8A6C");

    private static readonly Pen TracePen = CreatePen(Trace, 2);
    private static readonly Pen TraceBrightPen = CreatePen(TraceBright, 1.2);
    private static readonly Pen EdgePen = CreatePen(BoardEdge, 1);
    private static readonly Pen HolePen = CreatePen(HoleRing, 2);

    public PcbBoardControl() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40)
            return;

        // 1. 板基 + 板边框线（双线像 keep-out 丝印框）。
        dc.DrawRectangle(BoardBase, null, new Rect(0, 0, w, h));
        dc.DrawRectangle(null, EdgePen, new Rect(6, 6, w - 12, h - 12));

        // 2. 四角安装孔：金属环 + 孔。
        foreach (var pt in new[]
                 {
                     new Point(22, 22), new Point(w - 22, 22),
                     new Point(22, h - 22), new Point(w - 22, h - 22),
                 })
        {
            dc.DrawEllipse(HoleRing, null, pt, 7, 7);
            dc.DrawEllipse(HoleInner, null, pt, 4, 4);
        }

        // 3. 底部总线：一束平行走线从左下进入、依次拐弯向上 ——
        //    芯片角色卡就排布在这束"主板总线"上方。
        var busX = w * 0.62;
        for (var i = 0; i < 6; i++)
        {
            var y = h - 30 - (i * 9);
            var topX = busX - (i * 26);
            var topY = h - 96 - (i * 6);

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(16, y), false, false);
                ctx.LineTo(new Point(topX, y), true, false);
                ctx.LineTo(new Point(topX, topY), true, false);
            }

            geo.Freeze();
            dc.DrawGeometry(null, i % 3 == 0 ? TraceBrightPen : TracePen, geo);

            // 走线端点焊盘（金环 + 孔）。
            dc.DrawEllipse(Pad, null, new Point(topX, topY), 3.5, 3.5);
            dc.DrawEllipse(PadInner, null, new Point(topX, topY), 1.5, 1.5);
        }

        // 4. 顶边下行走线（均匀间距，模拟信号排线），端点收焊盘。
        var topCount = Math.Max(4, (int)(w / 90));
        for (var i = 0; i < topCount; i++)
        {
            var x = 40 + (i * ((w - 80) / Math.Max(1, topCount - 1)));
            var depth = h * (0.32 + (0.10 * (i % 3)));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(x, 10), false, false);
                ctx.LineTo(new Point(x, depth), true, false);
            }

            geo.Freeze();
            dc.DrawGeometry(null, i % 2 == 0 ? TracePen : TraceBrightPen, geo);

            dc.DrawEllipse(Pad, null, new Point(x, depth), 3, 3);
            dc.DrawEllipse(PadInner, null, new Point(x, depth), 1.2, 1.2);
        }

        // 5. 过孔：板下部散布的小铜孔（固定确定性图案，不引入随机数）。
        for (var i = 0; i < 14; i++)
        {
            var x = 30 + (i * ((w - 60) / 13.0));
            var y = h * (i % 2 == 0 ? 0.86 : 0.90);
            dc.DrawEllipse(TraceBright, null, new Point(x, y), 2.2, 2.2);
            dc.DrawEllipse(BoardBase, null, new Point(x, y), 0.9, 0.9);
        }

        // 6. 丝印：板名 + 版本（Consolas 低对比小字，像真板上的白字油印）。
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawSilk(dc, "CORE-BUSY MAINBOARD", 26, h - 14, 11, dpi);
        DrawSilk(dc, "REV 2.6 · CPU CORE SOCKET ARRAY", w - 30, h - 14, 9, dpi, rightAlign: true);
    }

    private static void DrawSilk(
        DrawingContext dc, string text, double x, double y, double size, double dpi,
        bool rightAlign = false)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            Silk,
            dpi)
        {
            TextAlignment = rightAlign ? TextAlignment.Right : TextAlignment.Left,
        };

        dc.DrawText(ft, new Point(x, y - ft.Height));
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }
}
