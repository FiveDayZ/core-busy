namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreBusy.Core.Progression;

/// <summary>
/// 单个物理核的 EXP 账本条目。
/// </summary>
/// <remarks>
/// <para>
/// <b>读数字段一律用 <see cref="string"/>，不直接存 double。</b>
/// 这是被序列化器逼出来的设计：<see cref="System.Text.Json"/> <em>拒绝</em>写
/// NaN / 正负无穷（"cannot be written as valid JSON"），而本工具通篇用 NaN 表示
/// 「读不到」（沿用全线"不拿 0 冒充"的约定）。二者直接冲突 ——
/// 实测中它让 <c>Save</c> 静默失败（内部 catch 吞掉异常），账本根本落不了盘。
/// </para>
/// <para>
/// 故落盘时把非有限值存成 <c>null</c>，读回时还原为 NaN：
/// 语义不变（仍是"没有证据"），而文件保持合法 JSON。
/// <b>不要</b>改成"存 0"或"存 null 后按 0 读回" —— 那正是本项目最忌讳的以 0 冒充缺失。
/// </para>
/// </remarks>
public sealed class CoreExpEntry
{
    /// <summary>累计负荷经验（核·分）。</summary>
    public double LoadCoreMinutes { get; set; }

    /// <summary>累计陪伴兜底（核·分）。与 <see cref="LoadCoreMinutes"/> 取max 才是实际 EXP。</summary>
    public double FloorCoreMinutes { get; set; }

    /// <summary>历史最高占用率（0-100）。从未观测到有效值时为 <see cref="double.NaN"/>。</summary>
    public double PeakPercent { get; set; } = double.NaN;

    /// <summary>历史最高有效频率（GHz）。读不到时为 <see cref="double.NaN"/>。</summary>
    public double MaxGhz { get; set; } = double.NaN;

    /// <summary>历史峰值发生时刻（ISO 8601 本地时间）。从未出现时为 null。</summary>
    public string? PeakAt { get; set; }

    /// <summary>累计获得过升级的次数（即曾抵达的等级数，1 起）。</summary>
    public int TotalLevels { get; set; } = 1;

    /// <summary>实际累计 EXP（核·分）= max(负荷, 兜底)。</summary>
    public double ExpCoreMinutes =>
        double.IsNaN(LoadCoreMinutes) || double.IsNaN(FloorCoreMinutes)
            ? 0.0
            : Math.Max(LoadCoreMinutes, FloorCoreMinutes);

    /// <summary>当前等级。</summary>
    public int Level => CoreLevelCurve.LevelOf(ExpCoreMinutes);

    /// <summary>距下一级的 EXP 缺口。满级为 0。</summary>
    public double ExpToNext => CoreLevelCurve.ExpToNextLevel(ExpCoreMinutes);

    /// <summary>本级进度比例 [0,1]。</summary>
    public double Progress => CoreLevelCurve.LevelProgress(ExpCoreMinutes);
}

/// <summary>
/// 逐核 EXP 账本的 JSON 持久化（<c>%APPDATA%\CORE-BUSY\core-exp.json</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不并入 <see cref="StatsStore"/>：</b>后者按日重置、只留"今天"，
/// 而 EXP 必须跨日累积（跨日重置等于每天回到 Lv.1，成长感归零）。故另开一份账本。
/// </para>
/// <para>
/// <b>口径标识沿用 <see cref="EnergyHistoryStore"/> 的教训。</b>文件头写明
/// <c>caliber</c>；读取时不符即把旧文件另存为 <c>core-exp.{口径}.json</c> 并重新开始，
/// <em>不做任何换算</em>。EXP 口径一定会变（如从纯负荷改成负荷 + 陪伴兜底），
/// 而旧口径的 EXP 是"当时按另一套规则挣的"，折算需要当时的全部系数，已无从复原。
/// </para>
/// <para>
/// <b>属性名一律用显式 camelCase。</b>不能靠 C# 属性名的默认 PascalCase，
/// 也不能用全局 <c>PropertyNamingPolicy</c> —— 那会连内层条目一起改掉。
/// 这与 <see cref="EnergyHistoryStore"/> 在 v1.21.0 踩过的"落盘 PascalCase / 读取 camelCase"
/// 是同一类坑：<b>程序必须能读回自己写的文件</b>。每次改格式后必跑往返取证。
/// </para>
/// </remarks>
public static class CoreExpStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(EnergyHistoryStore.DataDirectory, "core-exp.json");

    /// <summary>
    /// 加载账本，并告知文件里的口径。
    /// </summary>
    /// <remarks>
    /// 返回的 <c>Caliber</c> 与 <see cref="CoreExpCaliber.CaliberId"/> 不符时，
    /// <c>Cores</c> 一定是空的 —— 旧文件已归档，调用方无需再判口径。
    /// </remarks>
    public static (Dictionary<string, CoreExpEntry> Cores, string Caliber) Load()
    {
        var result = new Dictionary<string, CoreExpEntry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(FilePath))
                return (result, CoreExpCaliber.CaliberId);

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (result, CoreExpCaliber.CaliberId);

            var caliber = root.TryGetProperty("caliber", out var caliberElement)
                ? caliberElement.GetString() ?? string.Empty
                : string.Empty;

            if (!string.Equals(caliber, CoreExpCaliber.CaliberId, StringComparison.Ordinal))
            {
                ArchiveLegacy(caliber);
                return (result, caliber);
            }

            if (!root.TryGetProperty("cores", out var cores) || cores.ValueKind != JsonValueKind.Object)
                return (result, caliber);

            foreach (var pair in cores.EnumerateObject())
            {
                var entry = pair.Value.Deserialize<CoreExpRecord>();
                if (entry is null)
                    continue;

                result[pair.Name] = entry.ToEntry();
            }

            return (result, caliber);
        }
        catch (Exception)
        {
            return (result, CoreExpCaliber.CaliberId);
        }
    }

    /// <summary>落盘（带口径标识）。</summary>
    public static void Save(IReadOnlyDictionary<string, CoreExpEntry> cores)
    {
        try
        {
            Directory.CreateDirectory(EnergyHistoryStore.DataDirectory);

            var serializable = new Dictionary<string, CoreExpRecord>(cores.Count, StringComparer.Ordinal);
            foreach (var pair in cores)
                serializable[pair.Key] = CoreExpRecord.FromEntry(pair.Value);

            // Caliber 声明在最前：人工翻这个文件时，第一眼就能看出这些数字是哪个口径的量。
            var payload = new CoreExpFile
            {
                Caliber = CoreExpCaliber.CaliberId,
                Cores = serializable,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            // 持久化失败不该影响监控主流程，但**必须留痕**：静默 catch 会让
            //「账本根本没落盘」这件事完全不可见 —— 实现期实测到过一次（序列器拒绝 NaN），
            // 若不是取证脚本逐环节复现，这个 bug 会被带进发布版。
            CoreExpStoreTrace.LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 把<em>旧口径</em>的账本另存为 <c>core-exp.{口径}.json</c>，不删原文件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 目标已存在时<em>不覆盖</em> —— 那是上一次口径切换留下的档案，比本次这份更值钱。
    /// 与 <see cref="EnergyHistoryStore"/> 的同名校验行为保持一致。
    /// </para>
    /// <para>
    /// <b>口径串必须先消毒再拼进文件名。</b>实测踩过一次：口径写成
    /// <c>load-core-minute/v1</c>（带版本号是好事）后，归档目标变成
    /// <c>core-exp.load-core-minute/v1.json</c> —— 其中的 <c>/</c> 被当作目录分隔符，
    /// <c>File.Copy</c> 抛 <c>DirectoryNotFoundException</c>，又被下面的 catch 吞掉，
    /// 结果是<em>旧账本既没归档、也没被清空</em>，静默丢失。
    /// <see cref="EnergyHistoryStore"/> 侥幸没暴露这个问题，只因它的口径标识不含斜杠。
    /// </para>
    /// </remarks>
    private static void ArchiveLegacy(string caliber)
    {
        try
        {
            var target = Path.Combine(EnergyHistoryStore.DataDirectory, $"core-exp.{Sanitize(caliber)}.json");
            if (File.Exists(target))
                return;

            File.Copy(FilePath, target);
        }
        catch (Exception)
        {
            // 归档失败不影响主流程：旧数据仍在原文件里，下一次启动会再试。
            // 但需注意此时调用方会把账本当空账本继续跑，故必须在注释里写明该风险。
        }
    }

    /// <summary>
    /// 把口径标识转成安全的文件名片段：路径分隔符与非法字符一律替换为 <c>_</c>。
    /// </summary>
    /// <remarks>
    /// 空串返回 <c>unknown</c>（而不是空），否则会生成 <c>core-exp..json</c> 这种畸形名。
    /// </remarks>
    public static string Sanitize(string caliber)
    {
        if (string.IsNullOrWhiteSpace(caliber))
            return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = caliber.Length <= 128 ? stackalloc char[caliber.Length] : new char[caliber.Length];
        var length = 0;
        foreach (var ch in caliber)
            buffer[length++] = Array.IndexOf(invalid, ch) >= 0 || ch == '/' || ch == '\\' ? '_' : ch;

        var safe = new string(buffer[..length]);
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    /// <summary>磁盘格式。属性名显式钉成 camelCase，且与 <see cref="Load"/> 读取的键名逐字一致。</summary>
    private sealed class CoreExpFile
    {
        [JsonPropertyName("caliber")]
        public string Caliber { get; set; } = CoreExpCaliber.CaliberId;

        [JsonPropertyName("cores")]
        public Dictionary<string, CoreExpRecord> Cores { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 磁盘上的单条记录：读数字段是 <see cref="string"/>，非有限值存 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 存在的唯一理由是 <see cref="System.Text.Json"/> 不能写 NaN/Infinity。
    /// 字符串侧一律用不变文化格式化，读时用 <c>double.TryParse</c> 的不变文化重载，
    /// 保证任何区域设置下都能读回自己写的文件。
    /// </remarks>
    private sealed class CoreExpRecord
    {
        [JsonPropertyName("loadCoreMinutes")]
        public double LoadCoreMinutes { get; set; }

        [JsonPropertyName("floorCoreMinutes")]
        public double FloorCoreMinutes { get; set; }

        /// <summary>null = 从未观测到有效值（回读为 NaN，绝不按 0 处理）。</summary>
        [JsonPropertyName("peakPercent")]
        public string? PeakPercent { get; set; }

        /// <summary>null = 读不到（回读为 NaN）。</summary>
        [JsonPropertyName("maxGhz")]
        public string? MaxGhz { get; set; }

        [JsonPropertyName("peakAt")]
        public string? PeakAt { get; set; }

        [JsonPropertyName("totalLevels")]
        public int TotalLevels { get; set; } = 1;

        public static CoreExpRecord FromEntry(CoreExpEntry e) => new()
        {
            LoadCoreMinutes = e.LoadCoreMinutes,
            FloorCoreMinutes = e.FloorCoreMinutes,
            PeakPercent = Fmt(e.PeakPercent),
            MaxGhz = Fmt(e.MaxGhz),
            PeakAt = e.PeakAt,
            TotalLevels = e.TotalLevels,
        };

        public CoreExpEntry ToEntry() => new()
        {
            LoadCoreMinutes = LoadCoreMinutes,
            FloorCoreMinutes = FloorCoreMinutes,
            PeakPercent = Parse(PeakPercent),
            MaxGhz = Parse(MaxGhz),
            PeakAt = PeakAt,
            TotalLevels = TotalLevels,
        };

        /// <summary>非有限值 → null（合法 JSON）；有限值 → 不变文化字符串。</summary>
        private static string? Fmt(double value)
            => double.IsFinite(value) ? value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : null;

        /// <summary>null / 空串 → NaN（"没有证据"），绝不用 0 顶替。</summary>
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
/// <summary>
/// 落盘失败的最后一次异常摘要。仅供取证脚本与诊断使用 ——
/// <b>发布版没有界面消费它</b>，但保留它比把异常彻底丢掉更有价值：
/// 「账本根本没落盘」这件事若完全静默，用户会以为等级一直在涨。
/// </summary>
public static class CoreExpStoreTrace
{
    /// <summary>最近一次 <see cref="CoreExpStore.Save"/> 的异常摘要；成功时为 null。</summary>
    public static string? LastSaveError { get; set; }
}
