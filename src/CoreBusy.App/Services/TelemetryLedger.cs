namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 遥测账本的内存形态：整机累计量（v1.34.0）。
/// </summary>
/// <remarks>
/// 运行时长**不在这里**累计 —— <see cref="RuntimeStatsStore"/> 自 v1.26.5 起就是
/// 累计运行时长的唯一账本，遥测直接读它的"基量 + 本次"，避免同一口径出现两份账本
/// （两份账本必然在崩溃/落盘节律差异下漂移）。
/// </remarks>
public sealed class TelemetryEntry
{
    /// <summary>累计能耗（Wh）。从未观测到有效封装功率时为 <see cref="double.NaN"/>。</summary>
    public double EnergyTotalWh { get; set; } = double.NaN;

    /// <summary>历史峰值封装功率（W）。读不到时为 <see cref="double.NaN"/>。</summary>
    public double PowerPeakW { get; set; } = double.NaN;

    /// <summary>上一会话平均封装功率（W）。样本不足（&lt;60s）或从未有读数时为 NaN。</summary>
    public double PowerLastW { get; set; } = double.NaN;
}

/// <summary>
/// 整机遥测账本：累计能耗 / 峰值功率 / 上次会话平均功率（v1.34.0）。
/// </summary>
/// <remarks>
/// <para>
/// <b>读数字段落盘用 string 间接层</b>——与 <see cref="CoreExpStore"/> 同一套纪律：
/// System.Text.Json 拒绝写 NaN/±∞，而非有限值在本项目里表示"读不到"，
/// 绝不按 0 处理（0 会冒充"真实测得 0"）。
/// </para>
/// <para>
/// <b>合并语义与 EXP 账本一致</b>：基量（截止上次落盘）在构造期读回，
/// 会话累加器从 0 起步，两者区间不重叠故相加；峰值取 max（只升不降）。
/// </para>
/// <para>
/// <b>0 功率不是读数</b>：封装功率 NaN/≤0 的帧不累进任何量（采集层 AMD 无兜底机型
/// 会恒给 NaN，Intel 普通权限下也可能读不到），能耗口径只由有效读数构成。
/// </para>
/// </remarks>
public sealed class TelemetryLedger
{
    /// <summary>会话平均功率的最小观测时长（秒）。短于该值不出"上次功耗"，避免首几分钟的噪声。</summary>
    private const double MinAvgPowerSeconds = 60.0;

    private double _baseEnergyWh = double.NaN;
    private double _sessionEnergyWh;
    private double _sessionEnergySeconds;
    private double _sessionPeakW = double.NaN;

    /// <summary>构造期从磁盘读回基量。文件缺失/损坏/口径不符 = 空账本（旧文件归档留证）。</summary>
    public void Load()
    {
        var (entry, caliber) = TelemetryStore.Load();
        if (!string.Equals(caliber, TelemetryStore.CaliberId, System.StringComparison.Ordinal))
            return; // 口径不符：TelemetryStore.Load 已把旧文件归档，这里当空账本重新起步。

        _baseEnergyWh = entry.EnergyTotalWh;
        CurrentPeakW = entry.PowerPeakW;
        CurrentLastW = entry.PowerLastW;
    }

    /// <summary>历史峰值功率（跨会话只升不降）。</summary>
    public double CurrentPeakW { get; private set; } = double.NaN;

    /// <summary>上一会话平均功率（本次会话尚未攒满样本时=上上会话的值）。</summary>
    public double CurrentLastW { get; private set; } = double.NaN;

    /// <summary>当前累计能耗（基量 + 本次会话）。</summary>
    public double CurrentEnergyTotalWh
    {
        get
        {
            var total = _baseEnergyWh;
            if (_sessionEnergyWh > 0)
                total = double.IsNaN(total) ? _sessionEnergyWh : total + _sessionEnergyWh;
            return total;
        }
    }

    /// <summary>
    /// 逐帧观测。dt 为本帧墙钟差（秒，首帧 0、休眠恢复钳 60s 由调用方保证），
    /// powerW 为本帧封装功率（NaN = 读不到）。
    /// </summary>
    public void ObserveFrame(double powerW, double dtSeconds)
    {
        if (!double.IsFinite(dtSeconds) || dtSeconds <= 0)
            return;

        // 0 / 负值不是合法读数（通电功耗 >0）：恒 0 的失效读数会把能耗账本钉死在
        // "0×dt 永远加 0"或更糟的负账，与 EXP 账本 MaxGhz>0 门是同一类守卫。
        if (!double.IsFinite(powerW) || powerW <= 0)
            return;

        _sessionEnergyWh += powerW * dtSeconds / 3600.0;
        _sessionEnergySeconds += dtSeconds;
        if (double.IsNaN(_sessionPeakW) || powerW > _sessionPeakW)
            _sessionPeakW = powerW;
    }

    /// <summary>把本次会话增量并入账本并落盘。调用方负责节流（5 分钟）与退出时 force。</summary>
    public void Save()
    {
        // 峰值合并：账本峰值与会话峰值取 max（区间不重叠，max 即真值）。
        if (!double.IsNaN(_sessionPeakW)
            && (double.IsNaN(CurrentPeakW) || _sessionPeakW > CurrentPeakW))
            CurrentPeakW = _sessionPeakW;

        // 上次功耗 = 本会话"有效观测区间"的平均。口径是"观测到功率的那些时间的均值"，
        // 不是墙钟均值 —— 功率传感器缺席的时间段没有证据，摊进均值只会伪造偏低。
        if (_sessionEnergySeconds >= MinAvgPowerSeconds)
            CurrentLastW = _sessionEnergyWh * 3600.0 / _sessionEnergySeconds;

        TelemetryStore.Save(CurrentEnergyTotalWh, CurrentPeakW, CurrentLastW);
    }
}

/// <summary>
/// 遥测账本的 JSON 持久化（<c>%APPDATA%\CORE-BUSY\telemetry.json</c>）。
/// </summary>
/// <remarks>
/// 与 <see cref="CoreExpStore"/> 同一套落盘纪律：显式 camelCase 键名（程序必须能读回
/// 自己写的文件）、读数字段 string 间接层、口径不符整份归档绝不合并（档案不覆盖）。
/// </remarks>
public static class TelemetryStore
{
    public const string CaliberId = "telemetry/v1";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(EnergyHistoryStore.DataDirectory, "telemetry.json");

    public static (TelemetryEntry Entry, string Caliber) Load()
    {
        var entry = new TelemetryEntry();
        try
        {
            if (!File.Exists(FilePath))
                return (entry, CaliberId);

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (entry, CaliberId);

            var caliber = root.TryGetProperty("caliber", out var caliberElement)
                ? caliberElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(caliber, CaliberId, System.StringComparison.Ordinal))
            {
                ArchiveLegacy(caliber);
                return (entry, caliber);
            }

            if (root.TryGetProperty("energyTotalWh", out var energy))
                entry.EnergyTotalWh = SanitizeTotal(Parse(energy.GetString()));
            if (root.TryGetProperty("powerPeakW", out var peak))
                entry.PowerPeakW = SanitizeWatt(Parse(peak.GetString()));
            if (root.TryGetProperty("powerLastW", out var last))
                entry.PowerLastW = SanitizeWatt(Parse(last.GetString()));

            return (entry, caliber);
        }
        catch (Exception)
        {
            // 与 CoreExpStore 一致：读不回来当空账本，不让坏文件拖垮监控主流程。
            return (entry, CaliberId);
        }
    }

    public static void Save(double energyTotalWh, double powerPeakW, double powerLastW)
    {
        try
        {
            Directory.CreateDirectory(EnergyHistoryStore.DataDirectory);
            var payload = new TelemetryFile
            {
                Caliber = CaliberId,
                EnergyTotalWh = Fmt(energyTotalWh),
                PowerPeakW = Fmt(powerPeakW),
                PowerLastW = Fmt(powerLastW),
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            // 静默 catch 必须留痕（CoreExpStore 的教训：序列器拒绝 NaN 让 Save 悄悄失败）。
            TelemetryStoreTrace.LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void ArchiveLegacy(string caliber)
    {
        try
        {
            // 口径串先消毒再拼文件名（CoreExpStore.Sanitize 的既有教训：带 / 的口径串
            // 会被当目录分隔符，归档静默失败）。
            var target = Path.Combine(EnergyHistoryStore.DataDirectory, $"telemetry.{CoreExpStore.Sanitize(caliber)}.json");
            if (File.Exists(target))
                return;
            File.Copy(FilePath, target);
        }
        catch (Exception)
        {
            // 归档失败不影响主流程；调用方已按空账本继续。
        }
    }

    /// <summary>能耗总量 ≤0（含 NaN）不是合法账目 → NaN（"没有证据"）。</summary>
    private static double SanitizeTotal(double value) => double.IsFinite(value) && value > 0 ? value : double.NaN;

    /// <summary>功率 ≤0（含 NaN）不是合法读数 → NaN。</summary>
    private static double SanitizeWatt(double value) => double.IsFinite(value) && value > 0 ? value : double.NaN;

    private static double Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return double.NaN;

        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;
    }

    private static string? Fmt(double value)
        => double.IsFinite(value) ? value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : null;

    private sealed class TelemetryFile
    {
        [JsonPropertyName("caliber")]
        public string Caliber { get; set; } = CaliberId;

        /// <summary>null = 从未观测到有效功率（回读为 NaN，绝不按 0 处理）。</summary>
        [JsonPropertyName("energyTotalWh")]
        public string? EnergyTotalWh { get; set; }

        [JsonPropertyName("powerPeakW")]
        public string? PowerPeakW { get; set; }

        [JsonPropertyName("powerLastW")]
        public string? PowerLastW { get; set; }
    }
}

/// <summary>落盘失败的最后一次异常摘要（与 <see cref="CoreExpStoreTrace"/> 同 purpose）。</summary>
public static class TelemetryStoreTrace
{
    public static string? LastSaveError { get; set; }
}
