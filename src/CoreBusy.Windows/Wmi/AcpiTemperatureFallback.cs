namespace CoreBusy.Windows.Wmi;

using System.Management;

/// <summary>
/// ACPI 温度兜底读取（MSAcpi_ThermalZoneTemperature，root/wmi）。
/// LibreHardwareMonitor 在非管理员会话下无法加载 Ring0 驱动时，用 ACPI 温区提供真实温度值。
/// 带结果缓存（5 秒），避免高频 WMI 查询。
/// </summary>
public sealed class AcpiTemperatureFallback
{
    private readonly object _gate = new();
    private DateTime _lastReadUtc = DateTime.MinValue;
    private double? _lastValue;

    /// <summary>读取温区最高温度（℃）；不可用时为 null。</summary>
    public double? ReadTemperatureC()
    {
        lock (_gate)
        {
            if ((DateTime.UtcNow - _lastReadUtc).TotalSeconds < 5)
                return _lastValue;

            _lastReadUtc = DateTime.UtcNow;

            double? best = null;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\wmi",
                    "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

                foreach (var instance in searcher.Get().Cast<ManagementBaseObject>())
                {
                    // CurrentTemperature 单位为 0.1 K。
                    var tenthKelvin = Convert.ToDouble(instance["CurrentTemperature"]);
                    if (tenthKelvin <= 0)
                        continue;

                    var celsius = tenthKelvin / 10.0 - 273.15;
                    if (celsius is < -20 or > 150)
                        continue;

                    best = Math.Max(best ?? double.MinValue, celsius);
                }
            }
            catch
            {
                // 温区不可读（部分平台禁用该 WMI 类）按 null 处理。
            }

            _lastValue = best;
            return _lastValue;
        }
    }
}
