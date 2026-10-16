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
/// v1.32.0 起：板基不动，**铜层色随整机养成阶段演变**（<see cref="ApplyStage"/>）——
/// 铜越养越亮，传说（全核满级）转金铜，是整机养成进度的最大画布。
/// </remarks>
public sealed class PcbBoardControl : FrameworkElement
{
    // 板基/板边/安装孔是"物理板子"，终身不变；**铜层随整机养成阶段越养越亮**（v1.32.0）
    // —— 封装阶段 = 全核平均等级所处阶段（UiTheme.StageIndexOf 单一边界源），
    // 同一块板，铜越用越亮；全核满级（传说）整板转金铜，主板即整机养成的最大画布。
    private static readonly Brush BoardBase = Frozen("#10271F");
    private static readonly Brush BoardEdge = Frozen("#365144");
    private static readonly Brush HoleRing = Frozen("#7B887E");
    private static readonly Brush HoleInner = Frozen("#081710");

    /// <summary>铜走线色阶：幼体 → 究极期逐级提亮；传说（全核满级）转金铜。</summary>
    private static readonly string[] TraceHexes =
        ["#234637", "#284E3D", "#2D5743", "#32604B", "#635532"];

    /// <summary>亮铜走线色阶（与常规走线同阶，恒高两档亮度）。</summary>
    private static readonly string[] TraceBrightHexes =
        ["#345B46", "#3A644D", "#406D54", "#46765B", "#85713F"];

    /// <summary>焊盘金色色阶：随阶段渐纯，传说为标准金。</summary>
    private static readonly string[] PadHexes =
        ["#B9963F", "#BFA044", "#C6A849", "#CEB04E", "#D4AF37"];

    /// <summary>丝印色阶：常规阶段随铜色微亮，传说转金。</summary>
    private static readonly string[] SilkHexes =
        ["#61816B", "#688872", "#708F79", "#789780", "#A49363"];

    private static Brush Trace = Frozen(TraceHexes[0]);
    private static Brush TraceBright = Frozen(TraceBrightHexes[0]);
    private static Brush Pad = Frozen(PadHexes[0]);
    private static Brush Silk = Frozen(SilkHexes[0]);

    private static Pen TracePen = CreatePen(Trace, 2);
    private static Pen TraceBrightPen = CreatePen(TraceBright, 1.2);
    private static readonly Pen EdgePen = CreatePen(BoardEdge, 1);

    private static int _stage;
    private static readonly List<PcbBoardControl> LiveInstances = [];

    /// <summary>
    /// 整板切换到阶段 0-4（幼体/成长期/成熟期/究极期/传说，与 UiTheme 阶段同界）。
    /// 阶段不变时是 no-op；变化时换铜层色并重画在册实例 —— 阶段跃迁是小时级事件，
    /// 重画开销可忽略，不破坏「背景无每帧开销」的软件渲染铁律。必须在 UI 线程调用。
    /// </summary>
    public static void ApplyStage(int stage)
    {
        if (stage == _stage)
            return;
        _stage = Math.Clamp(stage, 0, TraceHexes.Length - 1);
        Trace = Frozen(TraceHexes[_stage]);
        TraceBright = Frozen(TraceBrightHexes[_stage]);
        Pad = Frozen(PadHexes[_stage]);
        Silk = Frozen(SilkHexes[_stage]);
        TracePen = CreatePen(Trace, 2);
        TraceBrightPen = CreatePen(TraceBright, 1.2);
        foreach (var instance in LiveInstances)
            instance.InvalidateVisual();
    }

    public PcbBoardControl()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => LiveInstances.Add(this);
        Unloaded += (_, _) => LiveInstances.Remove(this);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40)
            return;

        // 圆角板边与安装孔始终可见；中心区域留给插座分区。
        dc.DrawRoundedRectangle(BoardBase, EdgePen, new Rect(1, 1, w - 2, h - 2), 14, 14);
        dc.DrawRoundedRectangle(null, EdgePen, new Rect(7, 7, w - 14, h - 14), 9, 9);
        foreach (var pt in new[] { new Point(22, 22), new Point(w - 22, 22), new Point(22, h - 22), new Point(w - 22, h - 22) })
        {
            dc.DrawEllipse(HoleRing, null, pt, 6, 6);
            dc.DrawEllipse(HoleInner, null, pt, 3, 3);
        }

        // 边缘信号束使用 45° 转角，避免旧版长直线穿过标题与核心读数。
        for (var i = 0; i < 5; i++)
        {
            var offset = 11 + i * 4;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(offset, 70 + i * 10), false, false);
                ctx.LineTo(new Point(offset, h - 90 - i * 6), true, false);
                ctx.LineTo(new Point(45 + i * 4, h - 56 - i * 6), true, false);
                ctx.LineTo(new Point(w * .42 + i * 25, h - 56 - i * 6), true, false);
                ctx.BeginFigure(new Point(w - offset, 80 + i * 12), false, false);
                ctx.LineTo(new Point(w - offset, h - 110 - i * 5), true, false);
                ctx.LineTo(new Point(w - 50 - i * 4, h - 71 - i * 5), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, i % 2 == 0 ? TracePen : TraceBrightPen, geometry);
            dc.DrawEllipse(Pad, null, new Point(w - offset, 80 + i * 12), 2, 2);
        }

        // 顶边的 VRM 元件与底部排针提示真实主板结构，全部为静态绘制。
        for (var i = 0; i < 8; i++)
        {
            var x = w * .46 + i * 29;
            if (x + 22 > w - 45) break;
            dc.DrawRoundedRectangle(HoleInner, EdgePen, new Rect(x, 12, 20, 11), 2, 2);
            dc.DrawRectangle(TraceBright, null, new Rect(x + 4, 15, 12, 5));
        }
        for (var i = 0; i < 18; i++)
        {
            var x = 50 + i * 8;
            dc.DrawRectangle(HoleInner, null, new Rect(x - 2, h - 34, 6, 12));
            dc.DrawRectangle(Pad, null, new Rect(x, h - 31, 2, 6));
        }
        for (var i = 0; i < 9; i++)
        {
            var x = w - 50 - i * 9;
            dc.DrawEllipse(TraceBright, null, new Point(x, h - 29), 2.2, 2.2);
            dc.DrawEllipse(BoardBase, null, new Point(x, h - 29), .9, .9);
        }

        // 6. 丝印：板名 + 版本（Consolas 低对比小字，像真板上的白字油印）。
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawSilk(dc, "CORE-BUSY MAINBOARD", 26, h - 14, 11, dpi);
        DrawSilk(dc, "CPU CORE SOCKET ARRAY", w - 30, h - 14, 9, dpi, rightAlign: true);
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
