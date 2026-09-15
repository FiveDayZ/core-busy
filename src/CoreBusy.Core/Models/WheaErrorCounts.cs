namespace CoreBusy.Core.Models;

/// <summary>
/// WHEA（Windows 硬件错误架构）事件统计（v1.17.0）。
/// <para>
/// 这是健康度体系里**唯一直接指向硅退化**的信号：温度、频率、抖动都是间接推断，
/// 而机器检查异常是硬件自己报出来的。它也是最早出现的 —— 一颗开始劣化的核心往往
/// 先抛出可纠正的缓存校验错误，几个月后才表现为不稳定。
/// </para>
/// <para>
/// <b>边界（必须如实呈现）</b>：WHEA-Logger 不止记录 CPU，PCIe、内存、平台控制器
/// 的错误也走同一个提供程序。因此 <see cref="CpuRelated"/> 与总数的差别是有意义的：
/// 能把来源判成处理器时优先用它，判不出来时退回总数，并在界面上说明口径。
/// </para>
/// </summary>
/// <param name="Corrected">窗口内**已纠正**的硬件错误数（机器检查异常，硬件已自行恢复）。</param>
/// <param name="Fatal">窗口内**未纠正/致命**的硬件错误数。</param>
/// <param name="CpuRelated">其中可判定来源为处理器/缓存的条数；无法判定来源时为 -1。</param>
/// <param name="Available">是否成功读到日志。**读不到与"读到 0 条"必须区分** ——
/// 前者是"不知道"，后者是"很干净"，绝不是一回事。</param>
/// <param name="Detail">取证用的一句话（事件 ID 明细 / 失败原因）。</param>
public readonly record struct WheaErrorCounts(
    int Corrected,
    int Fatal,
    int CpuRelated,
    bool Available,
    string Detail)
{
    /// <summary>未读取过 / 读取失败。</summary>
    public static WheaErrorCounts Unavailable(string reason) => new(0, 0, -1, false, reason);

    /// <summary>参与评分的纠错数：优先用可判定为处理器的条数，判不出来时退回总数。</summary>
    public int ScoringCorrected => CpuRelated >= 0 ? CpuRelated : Corrected;

    /// <summary>参与评分的致命错误数。</summary>
    public int ScoringFatal => Fatal;
}
