namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using CoreBusy.App.Infrastructure;

/// <summary>单核心当日累计统计条目。</summary>
public sealed class CoreStatEntry
{
    public double Sum { get; set; }
    public double Max { get; set; }
    public long Count { get; set; }
}

/// <summary>
/// 劳模排行"今日"口径持久化（Phase 5，方案 4.2 节遗留项）。
/// 按日保存到 %APPDATA%\CORE-BUSY\stats\stats-YYYY-MM-DD.json，重启后延续当日累计，
/// 跨日自动重置。选型说明：数据量小（每核 3 个数值），JSON 足够，避免引入 SQLite 依赖。
/// </summary>
public static class StatsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CORE-BUSY", "stats");

    private static string FilePath(DateTime day) => Path.Combine(DirectoryPath, $"stats-{day:yyyy-MM-dd}.json");

    /// <summary>加载当日核心统计；键为逻辑处理器 OsIndex。文件缺失/损坏返回 null。</summary>
    public static Dictionary<int, CoreStatEntry>? LoadToday()
    {
        var path = FilePath(DateTime.Today);
        try
        {
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<Dictionary<int, CoreStatEntry>>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void SaveToday(IReadOnlyDictionary<int, CoreStatEntry> stats)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath(DateTime.Today), JsonSerializer.Serialize(stats, JsonOptions));
        }
        catch (Exception)
        {
        }
    }
}
