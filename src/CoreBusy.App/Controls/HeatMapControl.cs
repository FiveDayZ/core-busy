namespace CoreBusy.App.Controls;

using System.Globalization;
using System.Windows;
using System.Windows.Media;

/// <summary>
/// 60 秒核心负载热力图：每行一个核心、每列 1 秒，颜色越亮负载越高。
/// 行标签内嵌绘制保证与网格逐像素对齐；网格底层铺一块略深的底板，
/// 确保低负载（0-8%）时格子依然与背景有明确边界，不会"看不见"。
/// 纯 OnRender 绘制，每秒仅一次重绘。
/// </summary>
public sealed class HeatMapControl : FrameworkElement
{
    public static readonly DependencyProperty RowsDataProperty = DependencyProperty.Register(
        nameof(RowsData), typeof(double[][]), typeof(HeatMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RowLabelsProperty = DependencyProperty.Register(
        nameof(RowLabels), typeof(string), typeof(HeatMapControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth), typeof(double), typeof(HeatMapControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[][]? RowsData
    {
        get => (double[][])GetValue(RowsDataProperty);
        set => SetValue(RowsDataProperty, value);
    }

    /// <summary>逗号分隔的行标签（"P0,P1,..."）。</summary>
    public string RowLabels
    {
        get => (string)GetValue(RowLabelsProperty);
        set => SetValue(RowLabelsProperty, value);
    }

    /// <summary>行标签预留宽度。</summary>
    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    private static readonly Brush LabelBrush = Make("#64788A");
    private static readonly Brush GridBackBrush = Make("#0C1218");
    private static readonly Typeface LabelTypeface =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>行标签文字缓存：仅在尺寸/标签变化时重建（避免每秒重排文字）。</summary>
    private FormattedText[] _labelCache = [];
    private string _labelCacheKey = string.Empty;
    private double _labelCachePixelsPerDip;

    protected override void OnRender(DrawingContext dc)
    {
        var rows = RowsData;
        if (rows is null || rows.Length == 0 || ActualWidth < 8 || ActualHeight < 8)
            return;

        var rowCount = rows.Length;
        var colCount = 0;
        foreach (var row in rows)
            colCount = Math.Max(colCount, row.Length);
        if (colCount == 0)
            return;

        var labelWidth = LabelWidth;

        // 行/列越密，格间距越小：32 条线程行也要能在一屏内铺满而不互相挤压。
        var gapY = rowCount > 24 ? 1.0 : rowCount > 16 ? 1.6 : 2.5;
        var gapX = colCount > 48 ? 1.0 : 2.0;

        // 行高只取决于总高与行数（与标签宽无关），据此推字号——
        // 这样"字号 ↔ 标签宽 ↔ 网格宽"不会形成循环依赖。
        var cellH = Math.Max(1, (ActualHeight - (rowCount - 1) * gapY) / rowCount);
        var selfFontSize = Math.Clamp(cellH * 0.62, 6.5, 10.5);

        // 行标签（与网格逐行对齐，带缓存）。LabelWidth 传 0 时按最长标签实测宽度自适应，
        // 保证 "C15·1" 这类长标签在 32 线程场景下也不会被截断。
        var labels = (RowLabels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length > 0)
        {
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            EnsureLabelCache(labels, rowCount, selfFontSize, pixelsPerDip);

            if (labelWidth <= 0)
            {
                var maxTextWidth = 0.0;
                foreach (var text in _labelCache)
                    maxTextWidth = Math.Max(maxTextWidth, text.Width);
                labelWidth = maxTextWidth + 6;
            }
        }

        var gridWidth = Math.Max(1, ActualWidth - labelWidth);
        var cellW = Math.Max(1, (gridWidth - (colCount - 1) * gapX) / colCount);

        // 网格底板：让整块热力图区域成为一块可见的画布，
        // 即使某行全为 0 负载也能看出"这是一行 60 秒的格子"。
        dc.DrawRoundedRectangle(GridBackBrush, null,
            new Rect(labelWidth, 0, gridWidth, ActualHeight), 3, 3);

        if (labels.Length > 0 && labelWidth > 8)
        {
            for (var r = 0; r < _labelCache.Length; r++)
            {
                var text = _labelCache[r];
                var y = r * (cellH + gapY) + (cellH - text.Height) / 2;
                dc.DrawText(text, new Point(Math.Max(0, labelWidth - text.Width - 4), y));
            }
        }

        // 单元格用直角矩形：3~6px 的格子上圆角不可见，但 DrawRoundedRectangle 的几何构造
        // 远贵于 DrawRectangle（每秒 960 格 × 软件渲染），这里换取实打实的重绘开销下降。
        var offsetX = labelWidth;
        for (var r = 0; r < rowCount; r++)
        {
            var data = rows[r];
            var y = r * (cellH + gapY);
            for (var c = 0; c < data.Length; c++)
            {
                var x = offsetX + c * (cellW + gapX);
                var brush = UsagePalette.HeatBrush(data[c]);
                dc.DrawRectangle(brush, null, new Rect(x, y, cellW, cellH));
            }
        }
    }

    private void EnsureLabelCache(string[] labels, int rowCount, double fontSize, double pixelsPerDip)
    {
        var key = $"{fontSize:0.##}|{string.Join('|', labels)}";
        if (key == _labelCacheKey && Math.Abs(pixelsPerDip - _labelCachePixelsPerDip) < 1e-6)
            return;

        var count = Math.Min(labels.Length, rowCount);
        var cache = new FormattedText[count];
        for (var r = 0; r < count; r++)
        {
            cache[r] = new FormattedText(
                labels[r],
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                fontSize,
                LabelBrush,
                pixelsPerDip);
        }

        _labelCache = cache;
        _labelCacheKey = key;
        _labelCachePixelsPerDip = pixelsPerDip;
    }

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
