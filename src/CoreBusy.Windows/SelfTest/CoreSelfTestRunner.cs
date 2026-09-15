namespace CoreBusy.Windows.SelfTest;

using System.Runtime.InteropServices;
using CoreBusy.Core.SelfTest;

/// <summary>
/// 逐核确定性自检的**执行器**（v1.18.0）：把纯逻辑负载
/// （<see cref="CoreSelfTestWorkload"/>）绑到指定物理核上跑，并如实处理"绑不成"的情况。
/// <para>
/// ──────────────────────────── 为什么必须单独起线程 ────────────────────────────
/// </para>
/// <para>
/// Windows 的处理器亲和性是**线程**属性。如果在调用方线程（比如 UI 线程）上改亲和性，
/// 一是会让整个 UI 被钉在一颗核上（界面直接卡住），二是异常路径下没法保证还原。
/// 自己起一条专用线程就同时解掉了这两点：线程随任务结束而消亡，亲和性随之消失，
/// 不存在"忘了还原"的状态泄漏。
/// </para>
/// <para>
/// ──────────────────────────── 为什么要如实报告"绑核失败" ────────────────────────────
/// </para>
/// <para>
/// 没绑成功的自检只是"在整机某处跑了一次计算"。把它当成"C3 这颗核算对了"是**把结论安到了
/// 错误的对象上** —— 而这正是本项目反复警惕的那类错误（错位在界面上看起来完全正常）。
/// 所以这里返回的是带 <see cref="CoreSelfTestOutcome.Error"/> 的结果，而不是一个"通过"。
/// </para>
/// </summary>
public static class CoreSelfTestRunner
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

    /// <summary>等待线程结束时的轮询间隔（毫秒）—— 保证取消能及时被观察到。</summary>
    private const int JoinPollMs = 50;

    /// <summary>
    /// 在指定的逻辑处理器集合上跑一次自检并等待结束。
    /// <para>
    /// <paramref name="logicalProcessors"/> 应是**该物理核的全部逻辑处理器**
    /// （SMT 双线程核传两个）：只绑一个兄弟线程会让另一半空着，测不出整核的负载行为。
    /// </para>
    /// </summary>
    /// <param name="coreId">核心显示名，仅用于结果标注。</param>
    /// <param name="logicalProcessors">目标物理核的逻辑处理器索引（0 基）。</param>
    /// <param name="duration">计算时长；会被夹到 (0, <see cref="CoreSelfTestArchive.MaxDurationSeconds"/>]。</param>
    /// <param name="cancellationToken">取消即中断（下一轮检查时退出）。</param>
    public static CoreSelfTestOutcome RunOn(
        string coreId,
        IReadOnlyList<int> logicalProcessors,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (logicalProcessors.Count == 0)
            return Failed(coreId, "未提供逻辑处理器索引");

        var seconds = Math.Clamp(duration.TotalSeconds, 0.2, CoreSelfTestArchive.MaxDurationSeconds);
        CoreSelfTestOutcome? outcome = null;

        var thread = new Thread(() =>
        {
            outcome = Execute(coreId, logicalProcessors, TimeSpan.FromSeconds(seconds), cancellationToken);
        })
        {
            IsBackground = true,
            Name = $"corebusy-selftest-{coreId}",
            // 尽量高但不实时：自检要的是一条不被打断的指令流，而 Realtime 优先级
            // 会饿死音频/输入线程，得不偿失。
            Priority = ThreadPriority.Highest,
        };

        thread.Start();

        // 不用 Join(TimeSpan) 一次性等待：那样取消要等到超时才生效。
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (thread.IsAlive && wait.Elapsed < TimeSpan.FromSeconds(seconds + 5))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // 负载内部每轮检查一次取消，通常几十微秒内就退出；这里仍给它一点时间收尾。
                if (thread.Join(500))
                    break;
            }

            thread.Join(JoinPollMs);
        }

        if (thread.IsAlive)
            return Failed(coreId, $"自检线程未在预期时间内结束（>{seconds + 5:0} 秒）");

        return outcome ?? Failed(coreId, "自检线程未返回结果");
    }

    private static CoreSelfTestOutcome Execute(
        string coreId,
        IReadOnlyList<int> logicalProcessors,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var mask = BuildMask(logicalProcessors);
        if (mask == 0)
            return Failed(coreId, $"逻辑处理器索引超出可表达范围：{string.Join(",", logicalProcessors)}");

        // 记录原掩码只是为了让"绑核"这件事可审计（本线程用完即弃，不存在还原需求）。
        var original = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask);
        if (original == UIntPtr.Zero)
        {
            return Failed(
                coreId,
                $"绑核失败（LP {string.Join(",", logicalProcessors)}；Win32 {Marshal.GetLastWin32Error()}）。"
                + "未绑核的自检不能指认某一颗核，故不记录为结果。");
        }

        try
        {
            var round = CoreSelfTestWorkload.Run(duration, cancellationToken);

            return new CoreSelfTestOutcome
            {
                CoreId = coreId,
                RunAt = DateTime.Now,
                DurationSeconds = round.Elapsed.TotalSeconds,
                Passes = round.Passes,
                IntegerMismatches = round.IntegerMismatches,
                FpBitInstabilities = round.FpBitInstabilities,
                FpMaxRelativeError = round.FpMaxRelativeError,
                IntegerChecksum = round.IntegerChecksum,
                FpChecksumBits = round.FpChecksumBits,
                LogicalProcessors = logicalProcessors.ToArray(),
                Error = round.Passes > 0 ? null : "计算轮数为 0（时长过短或被立即取消）",
            };
        }
        catch (Exception ex)
        {
            return Failed(coreId, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把逻辑处理器索引集合压成亲和性掩码。超出 64 个处理器时返回 0（本工具不声称支持）。</summary>
    private static ulong BuildMask(IReadOnlyList<int> logicalProcessors)
    {
        ulong mask = 0;
        foreach (var lp in logicalProcessors)
        {
            if (lp is < 0 or > 63)
                return 0;
            mask |= 1UL << lp;
        }

        return mask;
    }

    private static CoreSelfTestOutcome Failed(string coreId, string error) => new()
    {
        CoreId = coreId,
        RunAt = DateTime.Now,
        Error = error,
    };
}
