namespace CoreBusy.Core.Models;

/// <summary>
/// 核心频率读数的来源（v1.18.0）。**每一处频率展示与判定都必须能回答"这个数从哪来"**。
/// <para>
/// 为什么要把来源变成一等公民：v1.17.x 排查"8 颗核频率全是 4.52 GHz"花了两轮往返，
/// 根因不是读数错误，而是**没人能说出这一列走的哪条路径** —— `LogCpuSensorInventory`
/// 当时只打印温度/功耗，日志里没有任何"时钟传感器是否存在"的证据，
/// 于是"日志没有 Clock 行"被误读成"LHM 没有时钟传感器"。
/// </para>
/// <para>
/// 取值语义（AMD R9 5900HX 实测，见 .workbuddy/health-diag/）：
/// </para>
/// <list type="bullet">
///   <item><see cref="SensorEffective"/>：LHM 的 <c>Clock | Core #N (Effective)</c>，
///         **硬件驻留加权**的有效频率 —— 空闲核读到 39–855 MHz、满载核读到 2102+ MHz。
///         这是唯一能回答"这段时间它实际跑多快"的量，健康评分采用它。</item>
///   <item><see cref="SensorPState"/>：LHM 的 <c>Clock | Core #N</c>，取值是
///         **P-state 倍频 × 总线频率**（本机 45.75 × 99.82 = 4567 MHz）。它是离散档位，
///         与负载无关：所有非停放核都会读到同一个最高档 —— 界面 Tile 显示的是它，
///         但它**不能**用来判分（8 核全同值 → 比值恒 1 → 人人满分）。</item>
///   <item><see cref="PerfCounterRatio"/>：<c>% Processor Performance</c> × 基频。实测不可信
///         （16 LP × 5 轮：85%–122% 浮动且与使用率无相关性；同族 <c>Processor Frequency</c>
///         恒为基频），仅作最后回退并在界面标注。</item>
///   <item><see cref="BaseClock"/>：基频常数，兜底。</item>
/// </list>
/// </summary>
public enum CoreFrequencySource
{
    /// <summary>未知（Mock 数据、或采集层尚未就绪）。</summary>
    Unknown = 0,

    /// <summary>传感器逐核**有效频率**（LHM，硬件驻留加权）。健康评分采用此口径。</summary>
    SensorEffective,

    /// <summary>传感器逐核 P-state 时钟（LHM）。界面显示用，**不参与判分**。</summary>
    SensorPState,

    /// <summary>性能计数器比值 × 基频（回退路径，实测与负载不相关）。</summary>
    PerfCounterRatio,

    /// <summary>基频常数（最后兜底）。</summary>
    BaseClock,
}
