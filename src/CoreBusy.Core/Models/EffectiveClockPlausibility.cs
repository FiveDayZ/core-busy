namespace CoreBusy.Core.Models;

/// <summary>
/// 核心频率读数的**物理合理性**判定（v1.38.1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它。</b>用户实测（AMD R9 7940HS）弹卡显示「峰值 13.24 GHz」——
/// 该 SKU 的最大加速频率是 5.2 GHz，13.24 GHz 不可能是任何真实读数。
/// 来源是 LHM 的 <c>Clock | Core #N (Effective)</c>：它在部分 AMD 平台用
/// ΔAPERF/ΔMPERF 比值算有效频率，MPERF 增量极小（调度抖动、核停放切回、
/// 计数器回绕）时比值会短时爆到数倍 —— 尖峰只是一帧噪声，
/// 但峰值账本「只升不降」，一帧就把它永久钉死，且此后峰值档阈值
/// （0.99×13.24）永远不可达，峰值累计时长也就永远停在旧值。
/// </para>
/// <para>
/// <b>两道门，缺一不可。</b>
/// </para>
/// <list type="number">
///   <item><b>绝对上限</b>（<see cref="MaxPlausibleMhz"/>）：消费级平台的同频天花板
///         远低于 10 GHz（量产最高约 6 GHz，液氮超频才够得着 9 GHz 量级），
///         超过它必是解码/比值故障。这条门不依赖任何运行时参照，首帧即可用。</item>
///   <item><b>相对上限</b>（<see cref="PStateTolerance"/> × 本会话最高 P-state）：
///         有效频率是**时间平均量**，物理上不可能超过该核真正跑到的最高档；
///         而 P-state 时钟正是"离散档位"的无损证据。留 5% 余量吸收
///         P-state 传感器自身的量化与读取滞后。参照未建立（首帧 / 该平台无 P-state
///         传感器）时此门自动跳过，只走绝对门。</item>
/// </list>
/// <para>
/// <b>为什么不用 SKU 标称值当上界。</b>OEM 改功耗档 / 用户限频后标称值会算出一个恒偏的门，
/// 与 <c>_maxCoreClockMhz</c>（硬件活跃度分母）保持同一思路：参照只取**本机实测**。
/// </para>
/// </remarks>
public static class EffectiveClockPlausibility
{
    /// <summary>
    /// 有效频率的绝对上限（MHz）= 10 GHz。超过它的读数在任何消费级平台上都不可能成立。
    /// </summary>
    public const double MaxPlausibleMhz = 10_000.0;

    /// <summary>
    /// 相对上限的容差倍数：有效频率允许超出本会话最高 P-state 的 5%。
    /// </summary>
    public const double PStateTolerance = 1.05;

    /// <summary>
    /// 参考值下限（MHz）：低于它视为"参照尚未建立"（P-state 传感器缺席或首帧），
    /// 只做绝对门判定。100 MHz 与显示口径的 <c>mhz &gt; 100</c> 门同源。
    /// </summary>
    private const double ReferenceFloorMhz = 100.0;

    /// <summary>
    /// 判定一个逐核频率读数（MHz）是否物理上可能成立。
    /// </summary>
    /// <param name="mhz">待判定的读数（MHz）。</param>
    /// <param name="referenceMaxMhz">
    /// 参照上限（MHz），传本会话观测到的最高 P-state 时钟；未知时传
    /// <see cref="double.NaN"/> 或 0 —— 此时只做绝对门判定。
    /// </param>
    /// <returns>为 true 表示读数可用；false 表示应按"没有证据"降级（NaN），绝不冒充读数。</returns>
    public static bool IsPlausible(double mhz, double referenceMaxMhz)
    {
        // NaN / ∞ / 非正数都不是读数（通电必 > 0，与 v1.32.1 的 0 MHz 守卫同源）。
        if (!double.IsFinite(mhz) || mhz <= 0 || mhz > MaxPlausibleMhz)
            return false;

        // 参照不可用时放行绝对门内的读数：宁可保留真实读数，也不因参照缺席而整列降级。
        if (!double.IsFinite(referenceMaxMhz) || referenceMaxMhz < ReferenceFloorMhz)
            return true;

        return mhz <= referenceMaxMhz * PStateTolerance;
    }
}
