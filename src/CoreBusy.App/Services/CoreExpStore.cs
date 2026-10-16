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

    /// <summary>累计陪伴兜底（核·分）。保留原始统计，实际经验由逐段授奖量决定。</summary>
    public double FloorCoreMinutes { get; set; }

    /// <summary>逐区间累计的实际授奖量。缺省表示旧账本，使用旧 max 余额兼容。</summary>
    public double? AwardedCoreMinutes { get; set; }

    /// <summary>历史最高占用率（0-100）。从未观测到有效值时为 <see cref="double.NaN"/>。</summary>
    public double PeakPercent { get; set; } = double.NaN;

    /// <summary>历史最高有效频率（GHz）。读不到时为 <see cref="double.NaN"/>。</summary>
    public double MaxGhz { get; set; } = double.NaN;
    public double PeakSeconds { get; set; } = double.NaN;

    /// <summary>历史峰值发生时刻（ISO 8601 本地时间）。从未出现时为 null。</summary>
    public string? PeakAt { get; set; }

    /// <summary>累计获得过升级的次数（即曾抵达的等级数，1 起）。</summary>
    public int TotalLevels { get; set; } = 1;

    /// <summary>已授经验；旧账本保留原有 max 余额，绝不反推并虚增历史奖励。</summary>
    public double ExpCoreMinutes
    {
        get
        {
            var legacy = double.IsFinite(LoadCoreMinutes) && double.IsFinite(FloorCoreMinutes)
                ? Math.Max(0, Math.Max(LoadCoreMinutes, FloorCoreMinutes)) : 0;
            return AwardedCoreMinutes is double awarded && double.IsFinite(awarded)
                ? Math.Max(legacy, awarded) : legacy;
        }
    }

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

            using var document = JsonDocument.Parse(JsonFile.ReadAllText(FilePath));
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

                result[pair.Name] = RepairEntry(entry.ToEntry());
            }

            return (result, caliber);
        }
        catch (Exception)
        {
            return (result, CoreExpCaliber.CaliberId);
        }
    }

    /// <summary>落盘（带口径标识）。</summary>
    /// <remarks>
    /// <para>
    /// <b>写前盘账本合并（v1.36.0，等级回退 BUG 修复）。</b>程序此前没有单实例守卫，
    /// 旧实例滞留托盘时再启动一个新实例（如提权重启），两者各自持有独立账本、
    /// 整份互相覆盖 —— 后写者胜，先跑实例攒下的 EXP 被旧快照清掉。
    /// 本机 <c>core-exp.json</c> 实证：<c>totalLevels=8</c>（曾达 Lv.8，只升不降的高水位）
    /// 而 EXP 只够 Lv.5，即 EXP 曾从 ≥126.7 核·分回退到 65.9。
    /// 修复后写盘前先把盘上现有账本读回、逐核逐字段取有效最大值：
    /// 按文件路径串行化读、合并和原子替换，避免并发写入把旧快照覆盖回去。
    /// </para>
    /// </remarks>
    public static void Save(IReadOnlyDictionary<string, CoreExpEntry> cores)
    {
        try
        {
            JsonFile.WithLock(FilePath, () =>
            {
                Directory.CreateDirectory(EnergyHistoryStore.DataDirectory);

                // 合并的宿主是调用方账本本身的副本语义：MergeInto 只会把字段抬到
                // 「内存值与盘值的较大者」，正常运行时盘值 ≤ 内存值，等于零改动。
                var merged = new Dictionary<string, CoreExpEntry>(cores.Count, StringComparer.Ordinal);
                foreach (var pair in cores)
                    merged[pair.Key] = pair.Value;

                foreach (var pair in TryLoadForMerge())
                {
                    if (merged.TryGetValue(pair.Key, out var mine))
                        MergeInto(mine, pair.Value);
                    else
                        merged[pair.Key] = pair.Value;
                }

                var serializable = new Dictionary<string, CoreExpRecord>(merged.Count, StringComparer.Ordinal);
                foreach (var pair in merged)
                    serializable[pair.Key] = CoreExpRecord.FromEntry(pair.Value);

                // Caliber 声明在最前：人工翻这个文件时，第一眼就能看出这些数字是哪个口径的量。
                var payload = new CoreExpFile
                {
                    Caliber = CoreExpCaliber.CaliberId,
                    Cores = serializable,
                };
                JsonFile.WriteAllText(FilePath, JsonSerializer.Serialize(payload, JsonOptions));
            });
            CoreExpStoreTrace.LastSaveError = null;
        }
        catch (Exception ex)
        {
            // 持久化失败不该影响监控主流程，但**必须留痕**：静默 catch 会让
            //「账本根本没落盘」这件事完全不可见 —— 实现期实测到过一次（序列器拒绝 NaN），
            // 若不是取证脚本逐环节复现，这个 bug 会被带进发布版。
            CoreExpStoreTrace.LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Write("EXP save", ex);
        }
    }

    /// <summary>
    /// 读回盘上现有账本供 <see cref="Save"/> 合并。<b>无副作用</b>：不归档、不改文件、
    /// 不抛异常 —— 合并是纯增益，任何解析失败都按空账本处理，绝不能反过来阻塞保存。
    /// </summary>
    /// <remarks>
    /// 口径不符时返回空：口径切换的「归档 + 重新开始」语义由 <see cref="Load"/> 侧负责，
    /// 这里不抢 —— 否则写盘路径会把旧口径文件静默顶掉、绕过归档。
    /// </remarks>
    private static Dictionary<string, CoreExpEntry> TryLoadForMerge()
    {
        var result = new Dictionary<string, CoreExpEntry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(FilePath))
                return result;

            using var document = JsonDocument.Parse(JsonFile.ReadAllText(FilePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return result;

            var caliber = root.TryGetProperty("caliber", out var caliberElement)
                ? caliberElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(caliber, CoreExpCaliber.CaliberId, StringComparison.Ordinal))
                return result;

            if (!root.TryGetProperty("cores", out var cores) || cores.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var pair in cores.EnumerateObject())
            {
                var entry = pair.Value.Deserialize<CoreExpRecord>();
                if (entry is null)
                    continue;

                result[pair.Name] = RepairEntry(entry.ToEntry());
            }

            return result;
        }
        catch (Exception)
        {
            return new Dictionary<string, CoreExpEntry>(StringComparer.Ordinal);
        }
    }

    /// <summary>把盘上条目并入内存条目：读数类字段逐项取有效最大值（账本因此只升不降）。</summary>
    private static void MergeInto(CoreExpEntry mine, CoreExpEntry disk)
    {
        mine.AwardedCoreMinutes = Math.Max(mine.ExpCoreMinutes, disk.ExpCoreMinutes);
        mine.LoadCoreMinutes = MaxNat(mine.LoadCoreMinutes, disk.LoadCoreMinutes);
        mine.FloorCoreMinutes = MaxNat(mine.FloorCoreMinutes, disk.FloorCoreMinutes);
        mine.PeakPercent = MaxNat(mine.PeakPercent, disk.PeakPercent);
        mine.PeakSeconds = MaxNat(mine.PeakSeconds, disk.PeakSeconds);

        var mergedMaxGhz = MaxNat(mine.MaxGhz, disk.MaxGhz);
        // 峰值时刻跟随更大的 MaxGhz —— 峰值是谁创的，时刻就该是谁的。
        if (double.IsFinite(mergedMaxGhz) && (double.IsNaN(mine.MaxGhz) || mergedMaxGhz > mine.MaxGhz))
            mine.PeakAt = disk.PeakAt;
        mine.MaxGhz = mergedMaxGhz;

        if (mine.TotalLevels < disk.TotalLevels)
            mine.TotalLevels = disk.TotalLevels;
    }

    /// <summary>
    /// NaN 安全的取大：NaN = 「没有证据」，不参与比较。
    /// <see cref="Math.Max(double, double)"/> 在 .NET 里遇到 NaN 返回 NaN，
    /// 直接用会让一个缺失字段把真实读数吞掉。
    /// </summary>
    private static double MaxNat(double a, double b)
    {
        if (double.IsNaN(a))
            return double.IsNaN(b) ? double.NaN : b;
        if (double.IsNaN(b))
            return a;
        return Math.Max(a, b);
    }

    /// <summary>
    /// 等级下界修复（v1.36.0）：EXP 低于 <see cref="CoreExpEntry.TotalLevels"/>
    /// （曾抵达等级，只升不降的高水位）所对应的本级门槛时，说明账本曾被旧实例覆盖回退 ——
    /// 按已确证的下界恢复到该级门槛。
    /// </summary>
    /// <remarks>
    /// <b>只恢复到门槛，绝不虚增。</b>TotalLevels 是落盘的等级高水位，属已确证证据；
    /// 但它在当级内的真实进度无从考证，取本级门槛是保守下界（LevelOf 只会 ≥ TotalLevels，
    /// 因此修复幂等，不会在每次启动时反复加码）。一年配速曲线的各级阈值更低，
    /// 已有经验完整保留，旧等级高水位仍是有效下界。
    /// </remarks>
    private static CoreExpEntry RepairEntry(CoreExpEntry entry)
    {
        if (entry.TotalLevels > entry.Level)
        {
            var threshold = CoreLevelCurve.CumulativeExp(entry.TotalLevels);
            if (double.IsFinite(threshold) && threshold > entry.LoadCoreMinutes)
                entry.LoadCoreMinutes = threshold;
        }

        return entry;
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

        [JsonPropertyName("awardedCoreMinutes")]
        public double? AwardedCoreMinutes { get; set; }

        /// <summary>null = 从未观测到有效值（回读为 NaN，绝不按 0 处理）。</summary>
        [JsonPropertyName("peakPercent")]
        public string? PeakPercent { get; set; }

        /// <summary>null = 读不到（回读为 NaN）。</summary>
        [JsonPropertyName("maxGhz")]
        public string? MaxGhz { get; set; }
        public string? PeakSeconds { get; set; }

        [JsonPropertyName("peakAt")]
        public string? PeakAt { get; set; }

        [JsonPropertyName("totalLevels")]
        public int TotalLevels { get; set; } = 1;

        public static CoreExpRecord FromEntry(CoreExpEntry e) => new()
        {
            LoadCoreMinutes = e.LoadCoreMinutes,
            FloorCoreMinutes = e.FloorCoreMinutes,
            AwardedCoreMinutes = e.ExpCoreMinutes,
            PeakPercent = Fmt(e.PeakPercent),
            MaxGhz = Fmt(e.MaxGhz),
            PeakSeconds = Fmt(e.PeakSeconds),
            PeakAt = e.PeakAt,
            TotalLevels = e.TotalLevels,
        };

        // v1.32.1 存量账本修复：MaxGhz ≤ 0 不是合法读数（通电必 >0），它是
        // 「有效频率传感器恒报 0 被当读数」时期的污染值——峰值恒显 "0.00 GHz"，
        // 且峰值档阈值 0×0.99=0 让 PeakSeconds 把所有帧都累计进去（用户实测
        // "峰值 0、累计时长却大于 0"）。修复规则：
        //   · MaxGhz ≤ 0（含 NaN）→ NaN（回到"没有证据"）；
        //   · 该状态下已累计的 PeakSeconds 是在失效阈值下记的假证据 → 一并作废（NaN）。
        // MaxGhz > 0 的账本不受影响：其 PeakSeconds 是按 0.99×MaxGhz 的有效阈值累计的。
        // （随后由 >0 门与采集层 0 值守卫保证不再产生新的污染。）
        public CoreExpEntry ToEntry()
        {
            var maxGhz = Parse(MaxGhz);
            var peakSeconds = Parse(PeakSeconds);
            if (double.IsNaN(maxGhz) || maxGhz <= 0)
            {
                maxGhz = double.NaN;
                if (peakSeconds > 0)
                    peakSeconds = double.NaN;
            }

            return new CoreExpEntry
            {
                LoadCoreMinutes = LoadCoreMinutes,
                FloorCoreMinutes = FloorCoreMinutes,
                AwardedCoreMinutes = AwardedCoreMinutes,
                PeakPercent = Parse(PeakPercent),
                MaxGhz = maxGhz,
                PeakSeconds = peakSeconds,
                PeakAt = PeakAt,
                TotalLevels = TotalLevels,
            };
        }

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
