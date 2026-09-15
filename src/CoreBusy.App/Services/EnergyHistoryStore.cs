namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using CoreBusy.App.Infrastructure;

/// <summary>单日能耗条目（焦耳 + 该日读数有效的秒数）。</summary>
public sealed class DayEnergyRecord
{
    /// <summary>当日累计能耗（焦耳）。</summary>
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

    private static string FilePath => Path.Combine(DirectoryPath, "energy-history.json");

    /// <summary>加载历史；文件缺失或内容损坏时返回空字典（解析失败会写日志，不静默吞掉）。</summary>
    public static Dictionary<DateOnly, DayEnergyRecord> Load()
    {
        var result = new Dictionary<DateOnly, DayEnergyRecord>();
        try
        {
            if (!File.Exists(FilePath))
                return result;

            var raw = JsonSerializer.Deserialize<Dictionary<string, DayEnergyRecord>>(
                File.ReadAllText(FilePath));

            if (raw is null)
                return result;

            foreach (var pair in raw)
            {
                // 单条损坏不该拖垮整份历史：解析不了的日期键跳过，并留一行日志。
                if (!DateOnly.TryParseExact(pair.Key, KeyFormat, out var day))
                {
                    AppLog.Write($"energy history: skip malformed key '{pair.Key}'");
                    continue;
                }

                if (pair.Value is null)
                    continue;

                result[day] = pair.Value;
            }

            return Prune(result);
        }
        catch (Exception ex)
        {
            AppLog.Write($"energy history load failed: {ex.Message}");
            return result;
        }
    }

    /// <summary>落盘。<paramref name="days"/> 内的条目会被裁剪到 <see cref="RetentionDays"/> 天。</summary>
    public static void Save(IReadOnlyDictionary<DateOnly, DayEnergyRecord> days)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            var serializable = new Dictionary<string, DayEnergyRecord>(days.Count);
            foreach (var pair in days)
                serializable[pair.Key.ToString(KeyFormat)] = pair.Value;

            File.WriteAllText(FilePath, JsonSerializer.Serialize(serializable, JsonOptions));
        }
        catch (Exception ex)
        {
            // 持久化失败不该影响监控主流程，但必须留痕：否则用户会以为历史在累积。
            AppLog.Write($"energy history save failed: {ex.Message}");
        }
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
