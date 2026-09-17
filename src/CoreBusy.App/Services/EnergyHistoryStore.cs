namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreBusy.App.Infrastructure;

/// <summary>单日能耗条目（焦耳 + 该日读数有效的秒数）。</summary>
/// <remarks>
/// 焦耳的量纲相同不代表可比：**口径**由文件的 <see cref="EnergyHistoryStore.CurrentCaliber"/> 决定
/// （v1.21.0 起是整机估算功率）。跨口径的两个 Joules 相差约两倍，不能相减相加。
/// </remarks>
public sealed class DayEnergyRecord
{
    /// <summary>当日累计能耗（焦耳，口径见文件头的 caliber 字段）。</summary>
    public double Joules { get; set; }

    /// <summary>
    /// 当日**读数有效**的时长（秒）——这一天的「观测窗口」。
    /// 与「日历上的 24 小时」不是一回事：本程序只在自己运行且传感器可用时才观测得。
    /// 保留它是为了让「该日平均功率」之类的派生量有诚实的分母。
    /// </summary>
    public double Seconds { get; set; }
}

/// <summary>
/// 每日能耗历史的 JSON 持久化（%APPDATA%\CORE-BUSY\energy-history.json）。
/// <para>
/// 选型与 <see cref="StatsStore"/> 一致：数据量小（每天两个数值，上限
/// <see cref="RetentionDays"/> 天），JSON 足够，不为它引入 SQLite 依赖。
/// </para>
/// <para>
/// 字典键用字符串 "yyyy-MM-dd" 而非 <c>DateOnly</c>：
/// System.Text.Json 对非字符串字典键的转换支持随类型而异，用 ISO 字符串做键是
/// 唯一不会被框架版本差异坑到的写法，反序列化时自己解析。
/// </para>
/// <para>
/// <b>v1.21.0 起文件带口径标识</b>（见 <see cref="CurrentCaliber"/>）：账本记的量从
/// 「CPU 封装功耗」换成了「整机估算功率」，两者不可比，而日历的色阶与参照都是**相对**的 ——
/// 把两种口径的日子混在一个月里，旧日会被系统性判成"明显偏少"、还会把参照中位数一起拖低。
/// 故读取时发现口径不符就**归档旧文件并重新开始**，不做任何换算（换算需要当时的分项系数，
/// 已经无从复原；按比例硬折出来的历史值只是看起来完整）。
/// </para>
/// </summary>
public static class EnergyHistoryStore
{
    /// <summary>
    /// 保留天数上限。400 天 ≈ 13 个月，足以支撑往回翻一年的日历，
    /// 同时把文件大小钉在几十 KB 量级（每天约一条 60 字节的记录）。
    /// </summary>
    public const int RetentionDays = 400;

    /// <summary>日期键格式。</summary>
    public const string KeyFormat = "yyyy-MM-dd";

    /// <summary>
    /// 口径标识：账本记的是 **CPU 封装功耗**累计量（v1.0 – v1.20.1，日历建账时的初始口径）。
    /// </summary>
    public const string CaliberCpuPackage = "cpu-package";

    /// <summary>
    /// 口径标识：账本记的是**整机估算功率**累计量（v1.21.0 起，逐部件模型，见
    /// <see cref="CoreBusy.Core.Energy.SystemPowerEstimator"/>）。
    /// </summary>
    public const string CaliberSystemEstimate = "system-estimate";

    /// <summary>当前写入的口径。日历、状态栏「整机」路两者同源。</summary>
    public const string CurrentCaliber = CaliberSystemEstimate;

    /// <summary>口径字段名（写入时排在最前，便于人工查看）。</summary>
    public const string CaliberProperty = "caliber";

    /// <summary>天数映射字段名。</summary>
    public const string DaysProperty = "days";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>环境变量名：整体重定向数据目录（取证隔离用）。</summary>
    public const string DataDirEnvVar = "COREBUSY_DATA_DIR";

    /// <summary>
    /// 数据目录，默认 <c>%APPDATA%\CORE-BUSY</c>。
    /// <para>
    /// <c>COREBUSY_DATA_DIR</c> 可整体重定向 —— 与 <c>COREBUSY_MOCK_CPU</c> / <c>COREBUSY_FORCE_BRAND</c>
    /// 同一约定：发布版可用，不做 UI 入口。
    /// </para>
    /// <para>
    /// 为什么必须有这条：打包后的 EXE 用 <c>Environment.GetFolderPath</c> 取路径，
    /// 而该 API 走 SHGetKnownFolderPath，**不读 APPDATA 环境变量** —— 所以
    /// 「设 APPDATA 到临时目录」这种常见隔离手法对本程序无效，取证脚本会直接把
    /// 合成历史写进用户真实的 energy-history.json。这类污染一旦发生就无法再区分
    /// 哪些是真实记录、哪些是造出来的。
    /// </para>
    /// </summary>
    private static string DirectoryPath
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirEnvVar);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CORE-BUSY")
                : overridden;
        }
    }

    /// <summary>
    /// 数据目录的公开只读入口。给"在资源管理器里打开数据文件夹"这类界面动作使用 ——
    /// 它们必须与写盘用的是**同一个**目录（含 <see cref="DataDirEnvVar"/> 覆盖），
    /// 否则按钮打开的地方可能不是程序真正写文件的地方。
    /// </summary>
    public static string DataDirectory => DirectoryPath;

    private static string FilePath => Path.Combine(DirectoryPath, "energy-history.json");

    /// <summary>
    /// 加载历史，并告知**文件里的口径**。
    /// <para>
    /// 返回的 <c>Caliber</c> 与 <see cref="CurrentCaliber"/> 不符时，<c>Days</c> 一定是空的 ——
    /// 旧文件已经被归档（见 <see cref="ArchiveLegacy"/>），调用方无需再判口径，
    /// 拿到空账本照常运行即可。
    /// </para>
    /// <para>
    /// 兼容 v1.20.1 及以前的**裸字典**格式（无 <c>caliber</c> 包装）：
    /// 那种文件一律按 <see cref="CaliberCpuPackage"/> 处理。
    /// </para>
    /// </summary>
    public static (Dictionary<DateOnly, DayEnergyRecord> Days, string Caliber) Load()
    {
        var result = new Dictionary<DateOnly, DayEnergyRecord>();
        try
        {
            if (!File.Exists(FilePath))
                return (result, CurrentCaliber);

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return (result, CurrentCaliber);

            // 旧格式是裸字典：直接以日期为键，没有 caliber 字段。
            var isLegacy = !root.TryGetProperty(CaliberProperty, out _);
            var caliber = isLegacy
                ? CaliberCpuPackage
                : root.TryGetProperty(CaliberProperty, out var caliberElement)
                    ? caliberElement.GetString() ?? CaliberCpuPackage
                    : CaliberCpuPackage;

            if (!string.Equals(caliber, CurrentCaliber, StringComparison.Ordinal))
            {
                ArchiveLegacy(caliber);
                return (result, caliber);
            }

            if (!root.TryGetProperty(DaysProperty, out var days) || days.ValueKind != JsonValueKind.Object)
                return (result, caliber);

            foreach (var pair in days.EnumerateObject())
            {
                // 单条损坏不该拖垮整份历史：解析不了的日期键跳过。
                if (!DateOnly.TryParseExact(pair.Name, KeyFormat, out var day))
                    continue;

                var record = pair.Value.Deserialize<DayEnergyRecord>();
                if (record is null)
                    continue;

                result[day] = record;
            }

            return (Prune(result), caliber);
        }
        catch (Exception)
        {
            return (result, CurrentCaliber);
        }
    }

    /// <summary>
    /// 把**旧口径**的历史另存为 <c>energy-history.{口径}.json</c>，不删原文件。
    /// <para>
    /// 原文件保留在原地：随后的 <see cref="Save"/> 会用新格式覆盖它，而在此之前若程序
    /// 一直没有有效读数（传感器关着），它仍是磁盘上唯一的记录副本。
    /// 归档目标已存在时**不覆盖** —— 那是上一次口径切换留下的档案，比本次正在翻的这份更值钱。
    /// </para>
    /// </summary>
    private static void ArchiveLegacy(string caliber)
    {
        try
        {
            var target = Path.Combine(DirectoryPath, $"energy-history.{caliber}.json");
            if (File.Exists(target))
                return;

            File.Copy(FilePath, target);
        }
        catch (Exception)
        {
            // 归档失败不影响主流程：旧数据仍留在原文件里，下一次启动会再试一次。
        }
    }

    /// <summary>落盘（带口径标识）。<paramref name="days"/> 内的条目会被裁剪到 <see cref="RetentionDays"/> 天。</summary>
    public static void Save(IReadOnlyDictionary<DateOnly, DayEnergyRecord> days)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            var serializable = new Dictionary<string, DayEnergyRecord>(days.Count);
            foreach (var pair in days)
                serializable[pair.Key.ToString(KeyFormat)] = pair.Value;

            // Caliber 声明在最前：人工翻这个文件时，第一眼就能看出这些数字是哪个口径的量。
            var payload = new EnergyHistoryFile { Caliber = CurrentCaliber, Days = serializable };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception)
        {
            // 持久化失败不该影响监控主流程，但必须留痕：否则用户会以为历史在累积。
        }
    }

    /// <summary>磁盘格式。<see cref="Caliber"/> 在序列化时排在最前（属性声明顺序即输出顺序）。</summary>
    /// <remarks>
    /// <b>两个属性名必须显式钉成小写</b>（<c>caliber</c> / <c>days</c>），不能靠 C# 属性名默认的 PascalCase。
    /// <para>
    /// v1.21.0 首次上线时就漏了这一步，后果很严重：<see cref="Save"/> 写出的是 <c>"Caliber"/"Days"</c>，
    /// 而 <see cref="Load"/> 按 <see cref="CaliberProperty"/>/<see cref="DaysProperty"/> 找的是 <c>"caliber"/"days"</c> ——
    /// <b>程序读不回自己写的文件</b>。于是每次启动都把上一轮写下的文件判成「旧口径裸字典」，
    /// 归档成 <c>energy-history.cpu-package.json</c>（把一个整机口径的文件错标成 CPU 口径）
    /// 并把账本清零：用户的历史每天重开一次就没了，而界面上只表现为日历一直很浅，看不出是坏了。
    /// </para>
    /// <para>
    /// 为什么不用全局 <c>PropertyNamingPolicy = CamelCase</c>：那样会连
    /// <see cref="DayEnergyRecord.Joules"/>/<see cref="DayEnergyRecord.Seconds"/> 一起改成小写，
    /// 而 <see cref="Load"/> 是用**默认选项**（大小写敏感）反序列化单条记录的，改完就再也读不回旧文件。
    /// 只钉外层两个键名，内层记录维持 PascalCase 不变。
    /// </para>
    /// </remarks>
    private sealed class EnergyHistoryFile
    {
        [JsonPropertyName(CaliberProperty)]
        public string Caliber { get; set; } = CurrentCaliber;

        [JsonPropertyName(DaysProperty)]
        public Dictionary<string, DayEnergyRecord> Days { get; set; } = new();
    }

    /// <summary>裁掉超出保留期的旧记录（按日期升序，只留最近 <see cref="RetentionDays"/> 天）。</summary>
    private static Dictionary<DateOnly, DayEnergyRecord> Prune(Dictionary<DateOnly, DayEnergyRecord> days)
    {
        if (days.Count <= RetentionDays)
            return days;

        var keep = days.Keys.OrderByDescending(d => d).Take(RetentionDays).ToHashSet();
        return days
            .Where(pair => keep.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
