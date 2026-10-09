namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;

/// <summary>
/// 程序运行时长持久化（v1.26.5，v1.27.1 扩展）：<c>%APPDATA%\CORE-BUSY\stats\runtime.json</c>。
/// <para>
/// 两个标量：<see cref="RuntimeEntry.TotalSeconds"/> 跨会话累计运行秒数（状态栏主显）、
/// <see cref="RuntimeEntry.LastSessionSeconds"/> 上一次会话的时长（悬浮提示用）。
/// 口径与"运行"一致：墙钟（含系统休眠时段），仅统计本程序运行期间 —— 不是电脑开机时长。
/// </para>
/// <para>
/// 落盘节奏由调用方节流（约 2 分钟一次 + 退出强制），崩溃最多丢最后一段，
/// 与能耗账本的取舍相同：数据是累计量，少量回退可接受，不值得逐帧写盘。
/// </para>
/// </summary>
public static class RuntimeStatsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CORE-BUSY", "stats");

    private static string FilePath => Path.Combine(DirectoryPath, "runtime.json");

    private sealed class RuntimeEntry
    {
        public double TotalSeconds { get; set; }

        public double LastSessionSeconds { get; set; }
    }

    /// <summary>
    /// 加载（累计秒数, 上次会话秒数）；文件缺失/损坏/字段异常一律按 0 计 ——
    /// 展示用累计量，不值得为它做归档/恢复流程。
    /// </summary>
    public static (double TotalSeconds, double LastSessionSeconds) Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return (0, 0);

            var entry = JsonSerializer.Deserialize<RuntimeEntry>(File.ReadAllText(FilePath));
            double Total(double v) => double.IsFinite(v) && v > 0 ? v : 0;

            return entry is null ? (0, 0) : (Total(entry.TotalSeconds), Total(entry.LastSessionSeconds));
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 写入（累计秒数, 上次会话秒数）—— 绝对值，非增量；
    /// 与调用方的"基量 + 本次"算法配套，崩溃/写坏都能在下一个节流点自愈。
    /// </summary>
    public static void Save(double totalSeconds, double lastSessionSeconds)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(
                    new RuntimeEntry
                    {
                        TotalSeconds = totalSeconds,
                        LastSessionSeconds = lastSessionSeconds,
                    },
                    JsonOptions));
        }
        catch (Exception)
        {
            // 落盘失败静默：下次节流点/退出时会再试（取的是当时的绝对值，天然自愈）。
        }
    }
}
