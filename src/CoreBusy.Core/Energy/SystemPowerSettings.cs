namespace CoreBusy.Core.Energy;

/// <summary>
/// 整机功耗估算的**逐部件**可校准系数（v1.20.1）
/// </summary>
/// <remarks>
/// <b>为什么系数要分项，而不是 v1.20.0 的"一个斜率 + 一个底数"：</b>
/// 单一斜率是"整机 ≈ k×CPU封装 + b"这种整体外推的产物。用户实测后如果整机偏小，
/// 他无法判断该修哪一项 —— 是主板开销估低了，还是内存系数不对，还是显卡根本没计。
/// 分项之后，每个系数对应一个**有物理含义、可用单点实测校准**的量：
/// 独显机的用户改 <see cref="GpuFallbackWatts"/>，跑虚拟机的改 <see cref="DramWattsPerGb"/>，
/// 而只想知道"总共差多少"的用户只动 <see cref="Calibration"/> 即可。
/// <para>
/// 全部字段的越界处理与全线约定一致：**回落默认值，而不是截断**。截断会把"填错了"
/// 伪装成"填了个极端值"，用户永远查不出自己的配置没生效。
/// </para>
/// </remarks>
public sealed record SystemPowerSettings
{
    /// <summary>整机总量校准乘数（默认 1.0，合法 0.3–3.0）。有功率计的用户只调这一个。</summary>
    public double Calibration { get; init; } = SystemPowerEstimator.DefaultCalibration;

    /// <summary>主板/芯片组/网卡/USB 供电等固定开销（W，默认 10，合法 0–100）。</summary>
    public double BoardWatts { get; init; } = SystemPowerEstimator.DefaultBoardWatts;

    /// <summary>每个转动风扇的功耗（W，默认 2，合法 0–20）。</summary>
    public double FanWatts { get; init; } = SystemPowerEstimator.DefaultFanWatts;

    /// <summary>独显无功率传感器且型号不在表内时的整卡额定功率兜底（W，默认 150，合法 0–600）。</summary>
    public double GpuFallbackWatts { get; init; } = SystemPowerEstimator.DefaultGpuFallbackWatts;

    /// <summary>内存每 GB 的**满载**功耗系数（W/GB，默认 0.392，合法 0–2）。</summary>
    public double DramWattsPerGb { get; init; } = SystemPowerEstimator.DefaultDramWattsPerGb;

    /// <summary>全默认设置。</summary>
    public static SystemPowerSettings Default { get; } = new();
}
