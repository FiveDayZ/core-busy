namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreBusy.App.Infrastructure;
using CoreBusy.Core.SelfTest;

/// <summary>
/// 逐核自检档案的 JSON 持久化（<c>%APPDATA%\CORE-BUSY\core-selftest.json</c>，v1.18.0）。
/// <para>
/// 存档内容是"期望值 + 首次通过时间 + 失败历史"，与 Prime95 把结果写进 <c>results.txt</c>
/// 是同一个目的：让"这台机器历史上算错过没有"这个问题有可追溯的答案。
/// 期望值（整数校验和 / 浮点位模式）在这里落盘，后续自检与它比对 ——
/// 同一条指令序列在健康硬件上必须每次给出同一个结果，**跨运行的不一致本身就是故障信号**。
/// </para>
/// <para>
/// 目录口径与 <see cref="CoreHealthStore"/> / <see cref="EnergyHistoryStore"/> 完全一致
/// （共用 <c>COREBUSY_DATA_DIR</c> 环境变量），取证脚本靠它把数据写到临时目录，
/// 不污染用户的真实档案。
/// </para>
/// </summary>
public static class CoreSelfTestStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // 同 CoreHealthStore：非提权/未执行的自检里浮点误差是 NaN，
        // 默认序列化器会拒绝写 NaN 并导致整个文件写不出去。
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private sealed class SelfTestFile
    {
        public int Version { get; set; } = CurrentVersion;

        public Dictionary<string, CoreSelfTestRecord> Cores { get; set; } = new();
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

    private static string FilePath => Path.Combine(DirectoryPath, "core-selftest.json");

    /// <summary>读回档案。文件缺失或损坏时返回空表（损坏会记日志，不静默吞掉）。</summary>
    public static Dictionary<string, CoreSelfTestRecord> Load()
    {
        var result = new Dictionary<string, CoreSelfTestRecord>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(FilePath))
                return result;

            var file = JsonSerializer.Deserialize<SelfTestFile>(File.ReadAllText(FilePath));
            if (file?.Cores is null)
                return result;

            if (file.Version != CurrentVersion)
                AppLog.Write($"[SELFTEST] 档案版本 {file.Version} ≠ {CurrentVersion}，按当前口径尽力读取");

            foreach (var pair in file.Cores)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                    continue;

                result[pair.Key] = pair.Value;
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[SELFTEST] 档案读取失败：{ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// 把一轮自检结果并进档案并落盘。
    /// <para>
    /// 期望值只在**首次通过**时写入，之后永不覆盖 —— 否则一次故障运行会把损坏的结果
    /// 变成新的"标准答案"，自检从此永远通过。这正是"基线不可被当前值拖低"的同一条原则，
    /// 与 <c>CoreHealthTracker.UpdateBaseline</c> 的 <c>BaselineRaiseMargin</c> 是一回事。
    /// </para>
    /// </summary>
    public static void Merge(
        Dictionary<string, CoreSelfTestRecord> archive,
        IReadOnlyList<CoreSelfTestOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            archive.TryGetValue(outcome.CoreId, out var existing);

            var expectedInteger = existing?.ExpectedIntegerChecksum ?? 0;
            var expectedFp = existing?.ExpectedFpChecksumBits ?? 0;
            var firstPassedAt = existing?.FirstPassedAt ?? default;
            var passCount = existing?.PassCount ?? 0;
            var failCount = existing?.FailCount ?? 0;
            var failures = existing?.Failures?.ToList() ?? [];

            if (outcome.Passed)
            {
                passCount++;
                if (firstPassedAt == default)
                {
                    firstPassedAt = outcome.RunAt;
                    expectedInteger = outcome.IntegerChecksum;
                    expectedFp = outcome.FpChecksumBits;
                }
                else if (outcome.IntegerChecksum != expectedInteger)
                {
                    // 通过了却与期望校验和不同：说明这次的计算结果与首次不同，
                    // 而这**不该**发生（同一输入、同一二进制）。如实记为失败而不是静默接受。
                    failCount++;
                    failures.Insert(0, Summarize(outcome, "校验和与期望值不一致（同一输入应给出同一结果）"));
                    TrimHistory(failures);
                }
            }
            else if (outcome.Executed)
            {
                failCount++;
                failures.Insert(0, Summarize(outcome, "计算出现不一致"));
                TrimHistory(failures);
            }

            archive[outcome.CoreId] = new CoreSelfTestRecord
            {
                CoreId = outcome.CoreId,
                ExpectedIntegerChecksum = expectedInteger,
                ExpectedFpChecksumBits = expectedFp,
                FirstPassedAt = firstPassedAt,
                LastRunAt = outcome.RunAt,
                PassCount = passCount,
                FailCount = failCount,
                LastOutcome = outcome,
                Failures = failures,
            };
        }

        Save(archive);
    }

    /// <summary>落盘。失败只记日志，不影响主流程。</summary>
    public static void Save(IReadOnlyDictionary<string, CoreSelfTestRecord> archive)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            var file = new SelfTestFile { Version = CurrentVersion };
            foreach (var pair in archive)
                file.Cores[pair.Key] = pair.Value;

            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Write($"[SELFTEST] 档案保存失败：{ex.Message}");
        }
    }

    private static string Summarize(CoreSelfTestOutcome outcome, string reason)
        => $"{outcome.RunAt:yyyy-MM-dd HH:mm:ss} {reason}："
           + $"轮次={outcome.Passes} 整数不一致={outcome.IntegerMismatches} "
           + $"浮点失稳={outcome.FpBitInstabilities} 浮点最大相对误差={outcome.FpMaxRelativeError:E2}";

    private static void TrimHistory(List<string> failures)
    {
        while (failures.Count > CoreSelfTestArchive.MaxFailureHistory)
            failures.RemoveAt(failures.Count - 1);
    }
}
