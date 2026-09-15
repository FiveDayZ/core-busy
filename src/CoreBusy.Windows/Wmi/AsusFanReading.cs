namespace CoreBusy.Windows.Wmi;

/// <summary>
/// ASUS WMI 风扇读数（v1.16.3）。
/// <para><c>null</c> 表示该端点在本机型不存在或查询失败；<c>0</c> 是"风扇确实停转"的真实读数，
/// 两者不可混为一谈（读不到不许拿 0 冒充）。</para>
/// </summary>
/// <param name="CpuRpm">CPU 风扇转速（RPM）。</param>
/// <param name="GpuRpm">GPU 风扇转速（RPM）。</param>
/// <param name="Detail">来源说明，或不可用时的原因（进界面 Tooltip 与 debug.log）。</param>
public readonly record struct AsusFanReading(double? CpuRpm, double? GpuRpm, string Detail)
{
    /// <summary>两个端点都拿不到时的读数。</summary>
    public static AsusFanReading Unavailable(string reason) => new(null, null, reason);

    /// <summary>是否至少拿到一个风扇转速。</summary>
    public bool HasValue => CpuRpm is not null || GpuRpm is not null;
}
