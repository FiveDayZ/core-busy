namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using CoreBusy.App.Infrastructure;

/// <summary>单个游戏会话的性能报告数据。</summary>
public sealed class GameReport
{
    public string Game { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public double DurationSeconds { get; set; }
    public double CpuAveragePercent { get; set; }
    public string? MaxCoreName { get; set; }
    public double MaxCoreUsagePercent { get; set; }
    public double? MaxTemperatureC { get; set; }
}

/// <summary>
/// 游戏会话性能报告（Phase 5，方案 4.1/13 节"游戏性能报告"）。
/// 会话结束自动写入 %APPDATA%\CORE-BUSY\reports\game-yyyyMMdd-HHmmss.json。
/// </summary>
public static class GameReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CORE-BUSY", "reports");

    /// <summary>保存报告并返回文件路径；失败返回 null（不打断主循环）。</summary>
    public static string? Save(GameReport report)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, $"game-{report.EndedAt:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
