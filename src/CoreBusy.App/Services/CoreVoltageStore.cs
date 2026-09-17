namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreBusy.App.Infrastructure;

/// <summary>
/// 逐核电压档案的 JSON 持久化（<c>%APPDATA%\CORE-BUSY\core-voltage.json</c>，v1.18.0）。
/// <para>
/// ──────────────────────────── 为什么要存，以及为什么只有这一个数 ────────────────────────────
/// </para>
/// <para>
/// "同频率所需电压上升"比"频率下降"对硅老化更灵敏，所以电压是值得留档的。但要把它变成
/// 可比较的量，需要一个**固定工况**：同一颗核、同一档频率下，现在需要多少压。
/// 本工具不写 MSR、不控频，拿不到这样的标定点，所以这里只记录一个口径明确、
/// 不会被工况轻易污染的派生量：
/// </para>
/// <list type="bullet">
///   <item><b>高活跃电压峰值</b>：只在硬件活跃度 ≥ <see cref="HighActivityPercent"/> 的帧上取电压最大值。
///         它回答的是"这颗核在接近满速时需要多少压"—— 后续年月里这个值缓慢上升是退化的信号。</item>
///   <item>同时留 <b>最近一次的裸读数</b>（含当时活跃度）供人工判读：只看峰值不看工况会误判。</item>
/// </list>
/// <para>
/// **它不参与评分**：口径里仍混着工况（同样"活跃度 90%"对应的真实频率点会随散热/功耗档漂移），
/// 折成 0–100 的分数会把工况差包装成退化结论。
/// </para>
/// <para>
/// 读不到一律缺席（不写 0）：沿用全线约定，0 与"没读到"必须在档案里也分得开。
/// </para>
/// </summary>
public static class CoreVoltageStore
{
    /// <summary>计入"高活跃电压"的硬件活跃度门槛（%）。</summary>
    public const double HighActivityPercent = 80.0;

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>单颗核的电压档案。</summary>
    public sealed record CoreVoltageRecord
    {
        /// <summary>核心显示名。</summary>
        public string CoreId { get; init; } = string.Empty;

        /// <summary>高活跃（≥ <see cref="HighActivityPercent"/>%）帧上见过的最高电压（V）。无样本时为 NaN。</summary>
        public double PeakVoltageHighActivityV { get; init; } = double.NaN;

        /// <summary>达到该峰值的时间。</summary>
        public DateTime PeakObservedAt { get; init; }

        /// <summary>高活跃帧的样本数（判断峰值的可信度）。</summary>
        public int HighActivitySamples { get; init; }

        /// <summary>最近一次看到的裸读数（V）。</summary>
        public double LastVoltageV { get; init; } = double.NaN;

        /// <summary>最近一次裸读数时的硬件活跃度（%）。</summary>
        public double LastActivityPercent { get; init; } = double.NaN;

        /// <summary>最近一次更新时间。</summary>
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class VoltageFile
    {
        public int Version { get; set; } = CurrentVersion;

        public Dictionary<string, CoreVoltageRecord> Cores { get; set; } = new();
    }

    private static string DirectoryPath
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(EnergyHistoryStore.DataDirEnvVar);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CORE-BUSY")
                : overridden;
        }
    }

    private static string FilePath => Path.Combine(DirectoryPath, "core-voltage.json");

    /// <summary>读回档案；缺失或损坏时返回空表。</summary>
    public static Dictionary<string, CoreVoltageRecord> Load()
    {
        var result = new Dictionary<string, CoreVoltageRecord>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(FilePath))
                return result;

            var file = JsonSerializer.Deserialize<VoltageFile>(File.ReadAllText(FilePath));
            if (file?.Cores is null)
                return result;

            foreach (var pair in file.Cores)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    result[pair.Key] = pair.Value;
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>
    /// 记一帧（就地更新并落盘）。只在**有电压读数**的帧上动档案：
    /// 读不到的帧不写 0、不清空既有峰值。
    /// </summary>
    public static void Observe(
        Dictionary<string, CoreVoltageRecord> archive,
        string coreId,
        double voltageV,
        double activityPercent,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(coreId) || double.IsNaN(voltageV))
            return;

        archive.TryGetValue(coreId, out var existing);

        var peak = existing?.PeakVoltageHighActivityV ?? double.NaN;
        var peakAt = existing?.PeakObservedAt ?? default;
        var samples = existing?.HighActivitySamples ?? 0;

        if (!double.IsNaN(activityPercent) && activityPercent >= HighActivityPercent)
        {
            samples++;
            if (double.IsNaN(peak) || voltageV > peak)
            {
                peak = voltageV;
                peakAt = now;
            }
        }

        archive[coreId] = new CoreVoltageRecord
        {
            CoreId = coreId,
            PeakVoltageHighActivityV = peak,
            PeakObservedAt = peakAt,
            HighActivitySamples = samples,
            LastVoltageV = voltageV,
            LastActivityPercent = activityPercent,
            UpdatedAt = now,
        };
    }

    /// <summary>落盘（由宿主按与健康基线相同的节流节奏调用）。</summary>
    public static void Save(IReadOnlyDictionary<string, CoreVoltageRecord> archive)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            var file = new VoltageFile { Version = CurrentVersion };
            foreach (var pair in archive)
                file.Cores[pair.Key] = pair.Value;

            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch (Exception)
        {
        }
    }
}
