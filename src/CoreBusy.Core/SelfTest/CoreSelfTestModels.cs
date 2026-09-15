namespace CoreBusy.Core.SelfTest;

/// <summary>
/// 单颗核一次自检的结果（v1.18.0）。**只作事实记录，不参与健康度评分。**
/// <para>
/// 为什么不进评分：自检回答的是"这颗核在这一次计算里有没有算错"，而评分回答的是
/// "它现在相对同伴健不健康"。一次 2 秒的自检通过**不能**证明一台机器健康
/// （故障可能只在高温/高压/特定指令组合下出现），而一次失败虽然是硬证据，
/// 却属于"要么 0 要么 1"的事件 —— 把它折进 0–100 的连续分数里只会稀释掉它的锋利度。
/// 所以本工具的做法是：自检结论单独成文件、单独显示，让人自己看。
/// </para>
/// </summary>
public sealed record CoreSelfTestOutcome
{
    /// <summary>被检核心显示名（与 <c>CpuCoreSnapshot.Id</c> 同源）。</summary>
    public string CoreId { get; init; } = string.Empty;

    /// <summary>本次自检的时刻。</summary>
    public DateTime RunAt { get; init; }

    /// <summary>实际计算时长（秒）。</summary>
    public double DurationSeconds { get; init; }

    /// <summary>完成的计算轮数。0 表示没跑起来（被取消或绑核失败）。</summary>
    public int Passes { get; init; }

    /// <summary>整数两条独立路径不一致的轮数。**非 0 即硬故障证据**。</summary>
    public int IntegerMismatches { get; init; }

    /// <summary>浮点结果比特与首轮不同的轮数。**非 0 即硬故障证据**。</summary>
    public int FpBitInstabilities { get; init; }

    /// <summary>浮点结果相对整数精确结果的最大相对误差（信息量在趋势，不在单次值）。</summary>
    public double FpMaxRelativeError { get; init; } = double.NaN;

    /// <summary>整数路径校验和（跨运行可比）。</summary>
    public long IntegerChecksum { get; init; }

    /// <summary>浮点输出位模式异或（跨运行可比）。</summary>
    public long FpChecksumBits { get; init; }

    /// <summary>绑定到的逻辑处理器（"逐核"这个说法的依据；绑核失败时为空）。</summary>
    public IReadOnlyList<int> LogicalProcessors { get; init; } = [];

    /// <summary>未能执行的原因（绑核失败、异常等）。null 表示确实跑过。</summary>
    public string? Error { get; init; }

    /// <summary>是否确实执行过（与"通过"不同）。</summary>
    public bool Executed => Error is null && Passes > 0;

    /// <summary>
    /// 是否通过。<b>只认硬证据</b>：必须真的跑过，且两条独立校验都零不一致。
    /// 浮点误差幅值**不作为**通过判据（见 <see cref="CoreSelfTestWorkload"/> 的说明）。
    /// </summary>
    public bool Passed => Executed && IntegerMismatches == 0 && FpBitInstabilities == 0;
}

/// <summary>
/// 单颗核的自检档案（v1.18.0，持久化到 <c>%APPDATA%\CORE-BUSY\core-selftest.json</c>）。
/// <para>
/// 与 Prime95 把结果写进 <c>results.txt</c> 是同一个目的：让"这台机器历史上算错过吗"
/// 这个问题有可追溯的答案。期望值（校验和）在这里落盘，后续每次自检都与它比对 ——
/// 同一条指令序列在健康硬件上必须每次给出同一个结果，**跨运行的不一致本身就是故障信号**，
/// 不需要外部"标准答案"。
/// </para>
/// </summary>
public sealed record CoreSelfTestRecord
{
    /// <summary>核心显示名。</summary>
    public string CoreId { get; init; } = string.Empty;

    /// <summary>首次通过时记录的整数校验和（期望值）。未通过过时为 0。</summary>
    public long ExpectedIntegerChecksum { get; init; }

    /// <summary>首次通过时记录的浮点位模式（期望值）。未通过过时为 0。</summary>
    public long ExpectedFpChecksumBits { get; init; }

    /// <summary>首次通过的时间。</summary>
    public DateTime FirstPassedAt { get; init; }

    /// <summary>最近一次运行的时间。</summary>
    public DateTime LastRunAt { get; init; }

    /// <summary>累计通过次数。</summary>
    public int PassCount { get; init; }

    /// <summary>累计失败次数。</summary>
    public int FailCount { get; init; }

    /// <summary>最近一次结果（供界面直接显示，不必重跑）。</summary>
    public CoreSelfTestOutcome? LastOutcome { get; init; }

    /// <summary>
    /// 失败历史（最多 <see cref="CoreSelfTestArchive.MaxFailureHistory"/> 条，最新在前）。
    /// 只留摘要文本：这条记录要能被人工一眼读懂，而不是留一堆需要再解析的字段。
    /// </summary>
    public IReadOnlyList<string> Failures { get; init; } = [];
}

/// <summary>自检档案的整体口径常量（Core 与 App 共用，避免两处硬编码同一个数）。</summary>
public static class CoreSelfTestArchive
{
    /// <summary>每颗核保留的失败历史条数。</summary>
    public const int MaxFailureHistory = 8;

    /// <summary>单核自检的默认时长（秒）。它只是采样，不是压力测试。</summary>
    public const double DefaultDurationSeconds = 2.0;

    /// <summary>单核自检的时长上限（秒）—— 防止把"自检"用成烤机。</summary>
    public const double MaxDurationSeconds = 10.0;
}
