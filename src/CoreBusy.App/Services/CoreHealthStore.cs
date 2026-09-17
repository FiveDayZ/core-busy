namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Health;

/// <summary>
/// 核心健康度基线的 JSON 持久化（<c>%APPDATA%\CORE-BUSY\core-health.json</c>，v1.17.0）。
/// <para>
/// **持久化的用途**：记录每颗核历史上站到的最高"站位"（相对同封装中位数的比值），
/// 让界面能显示"这颗核相对自己的最好水平有没有退步"。
/// <para>
/// 注意这个纵向比较**不参与评分**："取历史最大值当锚"自带 ~2.5% 的系统性偏差，
/// 大于数年尺度的真实硅老化信号（详见 <see cref="CoreHealthBaselineEntry"/>）。
/// 评分只走同封装横向中位数，基线纯作信息展示。
/// </para>
/// </para>
/// <para>
/// 选型与 <see cref="EnergyHistoryStore"/> 完全一致（JSON 足够、不引 SQLite），
/// 目录口径也共用 <c>COREBUSY_DATA_DIR</c> 环境变量：
/// 取证脚本靠它把数据写到临时目录，不污染用户的真实档案。
/// </para>
/// </summary>
public static class CoreHealthStore
{
    /// <summary>
    /// 当前文件格式版本。
    /// <para>
    /// **v2（v1.17.0 定稿前）**：地基字段从"绝对峰值频率"换成"相对同伴的站位比值"
    /// （<see cref="CoreHealthBaselineEntry.RatioToPeer"/>）。v1 的文件里没有这个字段，
    /// 而它无法从旧数据推出来（当时没有同时记录同伴中位数），所以旧条目只能丢弃 ——
    /// 好在 v1 从未发布过，不会影响任何真实用户档案。
    /// </para>
    /// </summary>
    private const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // **没有这一行，非提权会话的基线根本存不下来。**
        // 未提权时封装温度读不到，基线里的 TempC 就是 NaN；而 System.Text.Json 默认
        // 拒绝把 NaN/±∞ 写成 JSON（抛"positive and negative infinity cannot be written as
        // valid JSON"），导致整个 core-health.json 写不出去、目录都建不出来。
        // 允许命名浮点字面量后，NaN 以字符串 "NaN" 落盘，读回仍是 NaN —— 往返一致。
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private sealed class HealthFile
    {
        public int Version { get; set; } = CurrentVersion;

        public Dictionary<string, CoreHealthBaselineEntry> Cores { get; set; } = new();
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

    private static string FilePath => Path.Combine(DirectoryPath, "core-health.json");

    /// <summary>读回基线。文件缺失或损坏时返回空表（损坏会记日志，不静默吞掉）。</summary>
    public static Dictionary<string, CoreHealthBaselineEntry> Load()
    {
        var result = new Dictionary<string, CoreHealthBaselineEntry>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(FilePath))
                return result;

            var file = JsonSerializer.Deserialize<HealthFile>(File.ReadAllText(FilePath));
            if (file?.Cores is null)
                return result;

            if (file.Version != CurrentVersion)

            foreach (var pair in file.Cores)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                    continue;

                // 逐条校验：站位比值是基线的唯一实质字段，NaN 或非正数都是无意义记录。
                // 它虽不参与评分，但留着会被界面渲染成"历史站位 -/当量 NaN"，白占一行又误导。
                if (double.IsNaN(pair.Value.RatioToPeer) || pair.Value.RatioToPeer <= 0)
                {
                    continue;
                }

                result[pair.Key] = pair.Value;
            }

            return result;
        }
        catch (Exception)
        {
            return result;
        }
    }

    /// <summary>落盘。失败只记日志，不影响监控主流程。</summary>
    public static void Save(IReadOnlyDictionary<string, CoreHealthBaselineEntry> baselines)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            var file = new HealthFile { Version = CurrentVersion };
            foreach (var pair in baselines)
                file.Cores[pair.Key] = pair.Value;

            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch (Exception)
        {
        }
    }
}
