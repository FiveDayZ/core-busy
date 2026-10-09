namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreBusy.Core.Progression;

/// <summary>单个物理核的角色统计条目（跨进程累积）。</summary>
public sealed class CoreRoleEntry
{
    /// <summary>累计观测时长（核·分）。</summary>
    public double ObservedCoreMinutes { get; set; }

    /// <summary>累计负荷（核·分）。<b>纯负荷</b>，不含陪伴兜底 —— 角色要刻画"干了多少活"。</summary>
    public double LoadCoreMinutes { get; set; }

    /// <summary>累计负荷平方（核·分 × %²），用于算方差。只增不减。</summary>
    public double LoadSquaredCoreMinutes { get; set; }

    /// <summary>峰值占用率（0-100）。从未观测到时为 NaN。</summary>
    public double PeakPercent { get; set; } = double.NaN;

    /// <summary>白天（08:00–20:00）累计负荷（核·分）。</summary>
    public double DayLoadCoreMinutes { get; set; }

    /// <summary>夜间累计负荷（核·分）。</summary>
    public double NightLoadCoreMinutes { get; set; }

    /// <summary>转成纯逻辑侧的统计量。</summary>
    public CoreRoleStats ToStats() => new()
    {
        ObservedCoreMinutes = ObservedCoreMinutes,
        LoadCoreMinutes = LoadCoreMinutes,
        LoadSquaredCoreMinutes = LoadSquaredCoreMinutes,
        PeakPercent = PeakPercent,
        DayLoadCoreMinutes = DayLoadCoreMinutes,
        NightLoadCoreMinutes = NightLoadCoreMinutes,
    };
}

/// <summary>
/// 逐核角色统计的 JSON 持久化（<c>%APPDATA%\CORE-BUSY\core-roles.json</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不并入 <see cref="StatsStore"/>：</b>后者按<em>OS 逻辑处理器索引</em>（OsIndex）
/// 索引且<b>只留当天</b>，跨日即清空。角色要的是"长期行为画像"，
/// 跨日重置会让角色每天从头判、永远不稳定。
/// </para>
/// <para>
/// <b>为什么不并入 <c>core-exp.json</c>：</b>两者口径不同且生命周期不同 ——
/// EXP 的负荷<em>含陪伴兜底</em>（回答"挣了多少"），角色的负荷是<em>纯负荷</em>
/// （回答"干了多少活"）；同一个文件里混两个口径，迟早会有人把其中一个当成另一个用。
/// 口径混用是本项目已经栽过两次的坑（F5 的键名、v1.21.0 的能耗口径）。
/// </para>
/// <para>
/// 口径标识与归档惯例完全沿用 <see cref="CoreExpStore"/>：不符即整份另存为
/// <c>core-roles.{口径}.json</c> 并重新开始，<em>不做换算</em>。
/// </para>
/// </remarks>
public static class CoreRoleStore
{
    /// <summary>口径标识。角色判定规则一旦调整即视为换口径。</summary>
    public const string CaliberId = "pure-load-daily/v1";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(EnergyHistoryStore.DataDirectory, "core-roles.json");

    /// <summary>加载角色统计，并告知文件里的口径。</summary>
    public static (Dictionary<string, CoreRoleEntry> Cores, string Caliber) Load()
    {
        var result = new Dictionary<string, CoreRoleEntry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(FilePath))
                return (result, CaliberId);

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (result, CaliberId);

            var caliber = root.TryGetProperty("caliber", out var caliberElement)
                ? caliberElement.GetString() ?? string.Empty
                : string.Empty;

            if (!string.Equals(caliber, CaliberId, StringComparison.Ordinal))
            {
                ArchiveLegacy(caliber);
                return (result, caliber);
            }

            if (!root.TryGetProperty("cores", out var cores) || cores.ValueKind != JsonValueKind.Object)
                return (result, caliber);

            foreach (var pair in cores.EnumerateObject())
            {
                var record = pair.Value.Deserialize<CoreRoleRecord>();
                if (record is null)
                    continue;

                result[pair.Name] = record.ToEntry();
            }

            return (result, caliber);
        }
        catch (Exception)
        {
            return (result, CaliberId);
        }
    }

    /// <summary>落盘（带口径标识）。</summary>
    public static void Save(IReadOnlyDictionary<string, CoreRoleEntry> cores)
    {
        try
        {
            Directory.CreateDirectory(EnergyHistoryStore.DataDirectory);

            var serializable = new Dictionary<string, CoreRoleRecord>(cores.Count, StringComparer.Ordinal);
            foreach (var pair in cores)
                serializable[pair.Key] = CoreRoleRecord.FromEntry(pair.Value);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(new CoreRoleFile
            {
                Caliber = CaliberId,
                Cores = serializable,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            CoreRoleStoreTrace.LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 旧口径账本另存为<code>core-roles.{口径}.json</code>，不删原文件。
    /// </summary>
    /// <remarks>
    /// 口径串先经 <see cref="CoreExpStore.Sanitize"/> 消毒 —— 它含<code>/</code>，
    /// 直接拼进文件名会被当路径分隔符，导致归档静默失败（已在 EXP 侧踩过一次）。
    /// </remarks>
    private static void ArchiveLegacy(string caliber)
    {
        try
        {
            var target = Path.Combine(
                EnergyHistoryStore.DataDirectory, $"core-roles.{CoreExpStore.Sanitize(caliber)}.json");
            if (File.Exists(target))
                return;

            File.Copy(FilePath, target);
        }
        catch (Exception)
        {
            // 归档失败不影响主流程：旧数据仍在原文件里，下一次启动会再试。
        }
    }


    /// <summary>
    /// 把本次运行的统计量并入跨进程账本（各项<em>取 max</em>，而非相加）。
    /// </summary>
    /// <remarks>
    /// 取 max 的理由：累加器覆盖「上次落盘之后」这一段，账本覆盖「之前」，
    /// 两者区间不重叠，正常情况下不会重复；但一旦某个值因异常被读了两遍，
    /// max 是幂等的，而相加会把错误放大。若两者确实各自推进（跨零点重置等），
    /// 累加器里的值会更大，max 自然取到它。
    /// </remarks>
    public static void Merge(
        Dictionary<string, CoreRoleEntry> ledger, string id, CoreRoleStats stats)
    {
        if (string.IsNullOrEmpty(id))
            return;

        if (!ledger.TryGetValue(id, out var entry))
        {
            entry = new CoreRoleEntry();
            ledger[id] = entry;
        }

        entry.ObservedCoreMinutes = Math.Max(entry.ObservedCoreMinutes, stats.ObservedCoreMinutes);
        entry.LoadCoreMinutes = Math.Max(entry.LoadCoreMinutes, stats.LoadCoreMinutes);
        entry.LoadSquaredCoreMinutes = Math.Max(entry.LoadSquaredCoreMinutes, stats.LoadSquaredCoreMinutes);
        entry.DayLoadCoreMinutes = Math.Max(entry.DayLoadCoreMinutes, stats.DayLoadCoreMinutes);
        entry.NightLoadCoreMinutes = Math.Max(entry.NightLoadCoreMinutes, stats.NightLoadCoreMinutes);

        // 峰值只在新高时更新；读不到（NaN）时保持原值 —— 绝不写成 0。
        //
        // NaN 优先判断不可省：entry.PeakPercent 初值就是 NaN，而任何数与 NaN 比较恒为 false，
        // 写成 `stats.PeakPercent > entry.PeakPercent` 会让**首个条目**的峰值永远写不进去
        // —— 每次比较都是 NaN vs 数字，恒假。该bug 由roleprobe 的
        // 「峰值写入」断言抓到。
        if (double.IsNaN(stats.PeakPercent))
            return;

        if (double.IsNaN(entry.PeakPercent) || stats.PeakPercent > entry.PeakPercent)
            entry.PeakPercent = stats.PeakPercent;
    }

    /// <summary>磁盘格式。属性名显式钉成 camelCase，与读取键名逐字一致。</summary>
    private sealed class CoreRoleFile
    {
        [JsonPropertyName("caliber")]
        public string Caliber { get; set; } = CaliberId;

        [JsonPropertyName("cores")]
        public Dictionary<string, CoreRoleRecord> Cores { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 磁盘上的单条记录：读数字段存字符串，非有限值写<c>null</c>。
    /// </summary>
    /// <remarks>
    /// 与 <c>CoreExpStore.CoreExpRecord</c> 同理：<see cref="JsonSerializer"/> 拒绝写
    /// NaN/Infinity，而本项目用 NaN 表示「读不到」。读回时还原为 NaN，
    /// <b>绝不按 0 处理</b>。
    /// </remarks>
    private sealed class CoreRoleRecord
    {
        [JsonPropertyName("observedCoreMinutes")]
        public double ObservedCoreMinutes { get; set; }

        [JsonPropertyName("loadCoreMinutes")]
        public double LoadCoreMinutes { get; set; }

        [JsonPropertyName("loadSquaredCoreMinutes")]
        public double LoadSquaredCoreMinutes { get; set; }

        [JsonPropertyName("peakPercent")]
        public string? PeakPercent { get; set; }

        [JsonPropertyName("dayLoadCoreMinutes")]
        public double DayLoadCoreMinutes { get; set; }

        [JsonPropertyName("nightLoadCoreMinutes")]
        public double NightLoadCoreMinutes { get; set; }

        public static CoreRoleRecord FromEntry(CoreRoleEntry e) => new()
        {
            ObservedCoreMinutes = e.ObservedCoreMinutes,
            LoadCoreMinutes = e.LoadCoreMinutes,
            LoadSquaredCoreMinutes = e.LoadSquaredCoreMinutes,
            PeakPercent = Fmt(e.PeakPercent),
            DayLoadCoreMinutes = e.DayLoadCoreMinutes,
            NightLoadCoreMinutes = e.NightLoadCoreMinutes,
        };

        public CoreRoleEntry ToEntry() => new()
        {
            ObservedCoreMinutes = ObservedCoreMinutes,
            LoadCoreMinutes = LoadCoreMinutes,
            LoadSquaredCoreMinutes = LoadSquaredCoreMinutes,
            PeakPercent = Parse(PeakPercent),
            DayLoadCoreMinutes = DayLoadCoreMinutes,
            NightLoadCoreMinutes = NightLoadCoreMinutes,
        };

        private static string? Fmt(double value)
            => double.IsFinite(value)
                ? value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : null;

        private static double Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return double.NaN;

            return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : double.NaN;
        }
    }
}

/// <summary>角色账本落盘失败的留痕通道（发布版无界面消费，供取证脚本读取）。</summary>
public static class CoreRoleStoreTrace
{
    /// <summary>最近一次 <see cref="CoreRoleStore.Save"/> 的异常摘要；成功时为 null。</summary>
    public static string? LastSaveError { get; set; }
}