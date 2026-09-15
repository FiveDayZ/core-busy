namespace CoreBusy.Sensor;

using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

/// <summary>
/// 基于 LibreHardwareMonitorLib 的 CPU 传感器实现（方案 7.3 节）。
/// 温度/功耗读取依赖 Ring0 内核驱动，必须以管理员身份运行，否则对应字段为 null，UI 降级显示。
/// </summary>
public sealed class LibreHardwareMonitorSensorService : IHardwareSensorService
{
    private readonly Computer _computer;

    public LibreHardwareMonitorSensorService()
    {
        // 主板节点用于读取 SuperIO 风扇转速；GPU / Storage 节点提供显卡温度与占用、
        // 硬盘 SMART 温度（v1.16.0 状态栏扩展）。启用更多节点会让首次 Open 稍慢，
        // 但 Start() 本就在后台线程执行，不影响 UI。
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsMotherboardEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
        };
    }

    public void Start()
    {
        _computer.Open();

        // 取证（v1.16.1）：一次性落盘「内核驱动状态 + CPU 温度/功耗传感器原始读数」。
        // 非提权时 LHM 拿不到 Ring0，AMD 的 SMU 读数会被填成 0（**不是 null**），
        // 界面上只剩一个"-"/"0 W"，看不出"为什么读不到"。日志要能直接回答这个问题。
        try
        {
            SensorLog.Write($"[SENSOR] 内核访问：{ProbeKernelAccess().Detail}");
            LogCpuSensorInventory();
            LogStorageSensorInventory();
        }
        catch (Exception ex)
        {
            SensorLog.Write($"[SENSOR] 自检失败（不影响采集）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 一次性打印 **CPU 的全部传感器**（v1.16.1 起只打印温度/功耗，v1.18.0 补齐）。
    /// <para>
    /// 为什么必须打全：v1.17.x 排查"8 颗核频率全是 4.52 GHz"时，日志里**没有一行 Clock**，
    /// 于是"日志没有 Clock 行"被误读成"LHM 没有时钟传感器"，白跑了两轮实测。
    /// 事实是本机有 19 个 Clock 传感器（含每核的 <c>Core #N</c> 与 <c>Core #N (Effective)</c>）
    /// 与 10 个 Voltage 传感器 —— 只打印两个类型就等于把这部分可观测性藏起来了。
    /// 这条日志只写一次，多几十行的代价换来的是"以后不必再猜"。
    /// </para>
    /// </summary>
    private void LogCpuSensorInventory()
    {
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu)
                continue;

            hardware.Update();
            SensorLog.Write($"[SENSOR] CPU 传感器清单：{hardware.Name}");
            foreach (var sensor in hardware.Sensors)
            {
                SensorLog.Write($"[SENSOR]   {sensor.SensorType} | {sensor.Name} = {sensor.Value?.ToString("0.##") ?? "null"}");
            }
        }
    }

    /// <summary>
    /// 一次性打印每块盘的**全部**温度传感器原始读数与被采用值（v1.16.2）。
    /// </summary>
    /// <remarks>
    /// 盘温这个字段踩过的坑说明"只写结果"不够：同一块盘上同时存在真实读数与阈值读数，
    /// 只有把原始清单摊开，"采用 46 而不是 87"才可复核，下次再出现离谱数字也能一眼定位是
    /// 哪块盘、哪个传感器名。同 v1.16.1 的 CPU 取证：降级/取舍路径必须带出真实原因。
    /// </remarks>
    private void LogStorageSensorInventory()
    {
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Storage)
                continue;

            try
            {
                hardware.Update();
            }
            catch (Exception ex)
            {
                SensorLog.Write($"[STORAGE] {hardware.Name}：读取失败（{ex.GetType().Name}）");
                continue;
            }

            var adopted = SelectDriveTemperature(hardware.Sensors);
            var raw = new List<string>();
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature)
                    continue;

                var note = IsThresholdSensorName(sensor.Name) ? "（阈值，不计）" : string.Empty;
                raw.Add($"{sensor.Name}={sensor.Value?.ToString("0.##") ?? "null"}{note}");
            }

            var rawText = raw.Count > 0 ? string.Join("，", raw) : "无温度传感器";
            var adoptedText = adopted?.ToString("0.##") ?? "无可用读数";
            SensorLog.Write($"[STORAGE] {hardware.Name}：采用 {adoptedText} ℃ | 原始：{rawText}");
        }
    }

    /// <summary>
    /// 探测内核驱动（PawnIO）可用性（v1.16.1）。CPU 温度/功耗、SuperIO 风扇转速与硬盘 SMART
    /// 温度都走 LHM 的内核驱动，而 PawnIO 设备**只对提升后的进程开放**；未以管理员身份运行时
    /// 这些字段必然读不到，界面必须说清是哪一种原因（缺驱动 / 缺权限），而不是只留一个"-"。
    /// 与 v1.10.5 的教训一致：降级路径要带出真实原因，不许编。
    /// </summary>
    public static SensorAccessInfo ProbeKernelAccess()
    {
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        var installed = false;
        try
        {
            // IsInstalled / Version 是 PawnIo 上仅有的两个静态成员（IsLoaded 是实例属性，
            // 拿它做静态门禁会抛 TargetException —— v1.10.5 就是这么坏的）。
            var pawnIo = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.PawnIo.PawnIo", throwOnError: false);
            installed = pawnIo?.GetProperty("IsInstalled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;
        }
        catch
        {
            // 反射失败按"未检测到"处理，文案里已用"未检测到"而不是断言"没装"。
        }

        if (!installed)
        {
            return new SensorAccessInfo(
                Badge: "⚠ 缺驱动",
                Detail: "未检测到 PawnIO 内核驱动：CPU 温度/功耗、风扇转速、硬盘温度都依赖它。"
                        + "装好 PawnIO（LibreHardwareMonitor 的内核驱动）并以管理员身份运行即可读取。");
        }

        if (!elevated)
        {
            return new SensorAccessInfo(
                Badge: "⚠ 需提权",
                Detail: "未以管理员身份运行：PawnIO 内核驱动只对提升后的进程开放，"
                        + "本机 CPU 温度/功耗、风扇转速、硬盘温度读不到（显卡温度走 NVML，不受影响）。"
                        + "点此以管理员身份重新启动，温度/功耗即刻可读。");
        }

        return SensorAccessInfo.Ok;
    }

    /// <inheritdoc />
    public IntelBaseClockProbe ProbeIntelBaseClock()
    {
        // LHM 0.9.x 起 MSR 访问改经 PawnIO（IntelMsr 模块），替代旧的 Ring0/WinRing0；
        // 模块类型只随 runtimes 程序集发布，ref 元数据里没有公开定义，编译期无法直引 → 反射。
        //
        // v1.10.5 修错：旧实现先读 PawnIo.IsLoaded 当门禁，但该属性在 LHM 0.9.6 里是**实例**属性
        // （静态的只有 PawnIo.IsInstalled / PawnIo.Version），GetValue(null) 抛 TargetException 被
        // catch 吞掉，于是"MSR 基频修正"这一层从来没真正执行过，日志还统一写成
        // "not elevated or unsupported"。现在不做静态门禁：直接实例化模块类型（ctor 内部自行打开
        // PawnIO 设备并载入模块），读失败就返回可审计的原因。
        try
        {
            var asm = typeof(Computer).Assembly;
            var msrType = asm.GetType("LibreHardwareMonitor.PawnIo.IntelMsr", throwOnError: false)
                          ?? FindIntelMsrType(asm);
            if (msrType is null)
                return Fail("IntelMsr 类型不存在（该 LHM 版本不含 PawnIo MSR 模块）");

            var readMsr = msrType.GetMethod("ReadMsr", new[] { typeof(uint), typeof(ulong).MakeByRefType() });
            if (readMsr is null)
                return Fail($"IntelMsr.ReadMsr(uint, out ulong) 签名不匹配（{msrType.FullName}）");

            object module;
            try
            {
                module = Activator.CreateInstance(msrType)!;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return Fail($"PawnIO 模块载入失败（{inner.GetType().Name}: {inner.Message}）");
            }

            try
            {
                if (!TryReadMsr(module, readMsr, 0xCE, out var platformInfo))
                    return Fail("MSR 0xCE 读取被拒（未提权 / PawnIO 驱动未就绪 / 被安全策略拦截）");

                // MSR_PLATFORM_INFO(0xCE)：bits 15:8 = 最大非睿频比（熔丝值；2006 年后 BCLK 恒 100 MHz）。
                var ratio = (int)((platformInfo >> 8) & 0xFF);
                if (ratio is < 4 or > 200)
                    return Fail($"MSR 0xCE 非睿频比超出合理域：{ratio}");

                // MSR_PKG_POWER_LIMIT(0x610)：PL1 = bits 14:0，PL2 = bits 47:32，单位 1/8 W。
                double pl1 = double.NaN, pl2 = double.NaN;
                if (TryReadMsr(module, readMsr, 0x610, out var powerLimit))
                {
                    var w1 = (powerLimit & 0x7FFF) / 8.0;
                    var w2 = ((powerLimit >> 32) & 0x7FFF) / 8.0;
                    if (w1 is > 0 and < 1000)
                        pl1 = w1;
                    if (w2 is > 0 and < 1000)
                        pl2 = w2;
                }

                return new IntelBaseClockProbe
                {
                    Available = true,
                    BaseFrequencyMhz = ratio * 100.0,
                    Pl1W = pl1,
                    Pl2W = pl2,
                    Note = $"msr.0xCE ratio={ratio}",
                };
            }
            finally
            {
                try { msrType.GetMethod("Close")?.Invoke(module, null); } catch { }
            }
        }
        catch (Exception ex)
        {
            return Fail($"反射读取 MSR 异常（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    private static IntelBaseClockProbe Fail(string note) => new() { Available = false, Note = note };

    /// <summary>按类型名（大小写不敏感）兜底查找 IntelMsr：LHM 改命名/命名空间时不必改这里。</summary>
    private static Type? FindIntelMsrType(System.Reflection.Assembly asm)
    {
        try
        {
            return asm.GetTypes()
                .FirstOrDefault(t => t.Name.Equals("IntelMsr", StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            return ex.Types.FirstOrDefault(t => t is not null
                && t.Name.Equals("IntelMsr", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool TryReadMsr(object module, System.Reflection.MethodInfo readMsr, uint index, out ulong value)
    {
        value = 0;
        var args = new object[] { index, 0UL };
        try
        {
            if (readMsr.Invoke(module, args) is not true)
                return false;
        }
        catch
        {
            return false;
        }

        value = (ulong)args[1];
        return true;
    }

    public HardwareSensorSnapshot ReadSnapshot()
    {
        double? packageTemp = null;
        double? maxCoreTemp = null;
        double? firstTemp = null;
        double? packagePower = null;
        double? fanRpm = null;

        // 被有效性守卫丢弃的原始读数（v1.16.1）：不留证据的话，
        // "读不到"与"读到了 0"在事后无法区分。
        double? rawTemp = null;
        double? rawPower = null;
        var clocks = new Dictionary<int, double>();
        var effectiveClocks = new Dictionary<int, double>();
        var voltages = new Dictionary<int, double>();

        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    ReadCpuSensors(hardware, ref packageTemp, ref maxCoreTemp, ref firstTemp,
                                   ref packagePower, ref rawTemp, ref rawPower,
                                   clocks, effectiveClocks, voltages);
                }
                else if (hardware.HardwareType == HardwareType.Motherboard)
                {
                    ReadFanSensor(hardware, ref fanRpm);
                }
            }
        }
        catch (Exception)
        {
            // 单次采样失败按空快照处理，避免监控线程崩溃；UI 沿用上次值/显示缺省。
        }

        // GPU / 硬盘温度（v1.16.0 状态栏扩展）：GPU 每帧读（NVML/nvapi 开销小），
        // 硬盘 SMART 查询较重，按 StorageRefreshSeconds 节流取缓存。
        var gpu = ReadGpuSensors();
        var drives = ReadDriveTemperaturesThrottled();

        var adoptedTemp = packageTemp ?? firstTemp;
        ReportSensorEvidence(adoptedTemp, packagePower, rawTemp, rawPower);

        return new HardwareSensorSnapshot
        {
            PackageTemperatureC = adoptedTemp,
            MaxCoreTemperatureC = maxCoreTemp,
            PackagePowerW = packagePower,
            FanRpm = fanRpm,
            CoreClockMhz = clocks,
            CoreEffectiveClockMhz = effectiveClocks,
            CoreVoltageV = voltages,
            GpuTemperatureC = gpu.TemperatureC,
            GpuUtilizationPercent = gpu.UtilizationPercent,
            DriveTemperatureC = drives.Count > 0 ? drives.Max(d => d.TemperatureC) : null,
            DriveTemperatures = drives,
        };
    }

    // ---------------------------------------------------------------- GPU / 存储

    /// <summary>硬盘温度读取节流间隔（秒）。SMART 查询较重，不值得每秒做。</summary>
    private const double StorageRefreshSeconds = 15.0;

    private readonly Stopwatch _storageClock = Stopwatch.StartNew();
    private IReadOnlyList<DriveTemperatureReading> _cachedDrives = [];
    private bool _storagePrimed;

    /// <summary>(核心温度℃, 核心占用率%)。</summary>
    private readonly record struct GpuReading(double? TemperatureC, double? UtilizationPercent);

    /// <summary>
    /// 读主显卡核心温度与占用。多 GPU（核显 + 独显）时按 NVIDIA → AMD → Intel 优先级
    /// 选主卡，与系统硬件层「独显优先」的状态栏口径对齐；单 GPU 平台不受影响。
    /// 温度取名次序：GPU Core → GPU Hot Spot → 首个温度传感器（部分卡只报 Hot Spot）。
    /// </summary>
    private GpuReading ReadGpuSensors()
    {
        try
        {
            IHardware? primary = null;
            var primaryRank = 0;

            foreach (var hardware in _computer.Hardware)
            {
                var rank = hardware.HardwareType switch
                {
                    HardwareType.GpuNvidia => 3,
                    HardwareType.GpuAmd => 2,
                    HardwareType.GpuIntel => 1,
                    _ => 0,
                };

                if (rank <= 0)
                    continue;

                // 高等级卡覆盖低等级卡；同分取先出现者（枚举顺序稳定）。
                if (rank > primaryRank || primary is null)
                {
                    primary = hardware;
                    primaryRank = rank;
                }
            }

            if (primary is null)
                return new GpuReading(null, null);

            primary.Update();

            double? coreTemp = null;
            double? hotSpotTemp = null;
            double? fallbackTemp = null;
            double? coreUsage = null;

            foreach (var sensor in primary.Sensors)
            {
                if (sensor.Value is not { } value)
                    continue;

                if (sensor.SensorType == SensorType.Temperature)
                {
                    // 阈值/极值传感器不是读数（同盘温，v1.16.2）：绝不能让它们充当兜底值。
                    if (!IsThresholdSensorName(sensor.Name) && IsPlausibleTemperature(value))
                        fallbackTemp ??= value;

                    if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                        coreTemp = value;
                    else if (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
                        hotSpotTemp = value;
                }
                else if (sensor.SensorType == SensorType.Load
                         && sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                {
                    coreUsage ??= value;
                }
            }

            return new GpuReading(coreTemp ?? hotSpotTemp ?? fallbackTemp, coreUsage);
        }
        catch
        {
            return new GpuReading(null, null);
        }
    }

    /// <summary>逐块盘的盘温（读不到温度的盘不进列表），按节流间隔刷新缓存。</summary>
    private IReadOnlyList<DriveTemperatureReading> ReadDriveTemperaturesThrottled()
    {
        if (!_storagePrimed || _storageClock.Elapsed.TotalSeconds >= StorageRefreshSeconds)
        {
            _storagePrimed = true;
            _storageClock.Restart();
            _cachedDrives = ReadDriveTemperatures();
        }

        return _cachedDrives;
    }

    private IReadOnlyList<DriveTemperatureReading> ReadDriveTemperatures()
    {
        var result = new List<DriveTemperatureReading>();

        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Storage)
                    continue;

                try
                {
                    hardware.Update();
                }
                catch
                {
                    continue;
                }

                if (SelectDriveTemperature(hardware.Sensors) is { } value)
                    result.Add(new DriveTemperatureReading { Name = hardware.Name, TemperatureC = value });
            }
        }
        catch
        {
            // 整轮枚举失败：返回已拿到的部分，不吞掉已有读数。
        }

        return result;
    }

    /// <summary>
    /// 单块盘的盘温（℃）。优先级：<c>Composite Temperature</c> → <c>Temperature</c> →
    /// 其余温度传感器的最大值。
    /// </summary>
    /// <remarks>
    /// v1.16.2 修错。<para>
    /// 旧实现取的是「该盘所有 <c>SensorType.Temperature</c> 的最大值（仅守卫 0~120）」，而 LHM
    /// 会把 NVMe 的**阈值**也作为同类型传感器一并暴露，实测 WD PC SN560：
    /// <c>Composite Temperature=46</c>、<c>Temperature #1=66.85</c>、<c>Temperature #2=45.85</c>、
    /// <c>Warning Temperature=83</c>、<c>Critical Temperature=87</c> —— 取 max 得到 87 ℃，
    /// 于是状态栏长期显示该盘的**临界告警线**而不是盘温；阈值更高的盘直接顶到 100 ℃ 以上
    /// （用户报告 114 ℃）。</para>
    /// <para>
    /// 根因是两个错误叠加：一是没分辨"现在多少度"与"到多少度报警"，二是"取最热"这个动作
    /// 对阈值毫无意义（阈值只会 >= 真实温度）。故按名字排掉阈值/极值传感器，并优先采用
    /// NVMe 标准的 Composite 读数（与 CrystalDiskInfo、任务管理器同口径）。
    /// </para>
    /// </remarks>
    private static double? SelectDriveTemperature(IReadOnlyList<ISensor> sensors)
    {
        double? composite = null;
        double? plain = null;
        double? other = null;

        foreach (var sensor in sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not { } value)
                continue;

            if (!IsPlausibleDriveTemperature(value) || IsThresholdSensorName(sensor.Name))
                continue;

            if (sensor.Name.Equals("Composite Temperature", StringComparison.OrdinalIgnoreCase))
                composite ??= value;
            else if (sensor.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                plain ??= value;
            else
                other = Math.Max(other ?? double.MinValue, value);
        }

        // 缺 Composite / Temperature 时才退到"其余传感器取最大值"（Temperature #N、厂商自定义名），
        // 这样 SATA / HDD 的 "Temperature" 与厂商命名变体都还能取到数。
        return composite ?? plain ?? other;
    }

    /// <summary>
    /// 阈值 / 极值类传感器名（不是实时读数）。名字是唯一可用的区分手段：
    /// LHM 对这几类传感器用的都是 <c>SensorType.Temperature</c>。
    /// </summary>
    private static bool IsThresholdSensorName(string name)
        => name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Limit", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Max", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Min", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Highest", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Lowest", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Lifetime", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 硬盘温度有效域（℃）：低于 100 才算读数。
    /// </summary>
    /// <remarks>
    /// 上界取 100 的依据：消费级 SSD 的 NVMe 临界阈值普遍在 85~95 ℃，到点即降速/关机，
    /// 盘体不可能稳定工作在 100 ℃ 以上（HDD 更低）。超过这个范围的值只可能来自
    /// 阈值传感器或 SMART 原始字节被误解析，两者都不是"当前的温度"。
    /// 这是第二道网：第一道是 <see cref="IsThresholdSensorName"/>（按名字）。
    /// </remarks>
    private static bool IsPlausibleDriveTemperature(double value) => value is > 0 and < 100;

    /// <summary>温度有效域（℃）：0 与超范围值不是读数，是"拿不到读数"的占位。</summary>
    private static bool IsPlausibleTemperature(double value) => value is > 0 and < 150;

    /// <summary>
    /// 温度/功耗可用性取证（v1.16.1）。**只在状态翻转时写一行**：
    /// 读数本身每秒都变，逐帧写会把 debug.log 淹掉；而"能不能读"在会话内只在很有限的
    /// 几个时刻变化（驱动被拒 / 提权 / 恢复），恰是需要留痕的那几次。
    /// </summary>
    private void ReportSensorEvidence(double? temp, double? power, double? rawTemp, double? rawPower)
    {
        var state = (Temp: temp is not null, Power: power is not null);
        if (_lastSensorState == state)
            return;

        _lastSensorState = state;

        if (state.Temp && state.Power)
        {
            SensorLog.Write($"[SENSOR] 温度/功耗已可读：{temp:0.#} ℃ / {power:0.#} W");
            return;
        }

        // 原始读数必须一并带出：AMD 在没有内核驱动时把 SMU 读数填 0 而非 null，
        // 只看"读到 0"极易误判成"这台机器真的只耗 0 W"。
        var raw = $"原始读数 温度={Format(rawTemp)} 功耗={Format(rawPower)}";
        var why = ProbeKernelAccess().Detail;
        SensorLog.Write(
            $"[SENSOR] 温度{(temp is null ? "不可用" : "可读")}、功耗{(power is null ? "不可用" : "可读")}"
            + $"（{raw}）；原因：{why}");
    }

    private static string Format(double? value) => value?.ToString("0.##") ?? "无该传感器";

    /// <summary>上次的温度/功耗可用性（null = 尚未采样过）。</summary>
    private (bool Temp, bool Power)? _lastSensorState;

    private static void ReadCpuSensors(
        IHardware hardware,
        ref double? packageTemp,
        ref double? maxCoreTemp,
        ref double? firstTemp,
        ref double? packagePower,
        ref double? rawTemp,
        ref double? rawPower,
        Dictionary<int, double> clocks,
        Dictionary<int, double> effectiveClocks,
        Dictionary<int, double> voltages)
    {
        hardware.Update();

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is null)
                continue;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    // 非提权时 AMD 的 "Core (Tctl/Tdie)" 读数是 0（不是 null），
                    // 放它过去就会变成"这颗 CPU 0 ℃"。守卫在此，不在下游。
                    if (!IsPlausibleTemperature(sensor.Value.Value))
                    {
                        rawTemp ??= sensor.Value;
                        break;
                    }

                    firstTemp ??= sensor.Value;
                    if (sensor.Name is "CPU Package" or "Core (Tctl/Tdie)" or "Core (Tctl)" or "CPU (Tctl/Socket)")
                        packageTemp = sensor.Value;
                    else if (sensor.Name.StartsWith("Core #"))
                        maxCoreTemp = Math.Max(maxCoreTemp ?? double.MinValue, sensor.Value.Value);
                    break;

                case SensorType.Power:
                    if (sensor.Name is not ("CPU Package" or "Package" or "CPU PPT"))
                        break;

                    // 功耗 0 W 不是读数：CPU 通电就必然有功耗。同温度，守卫在源头。
                    if (sensor.Value.Value > 0)
                        packagePower = sensor.Value;
                    else
                        rawPower ??= sensor.Value;
                    break;

                case SensorType.Clock:
                    // Intel 命名 "CPU Core #N"，AMD 命名 "Core #N"（均为物理核心 1 基序号）；
                    // 另有 "Core #N (Effective)"（驻留加权的有效频率，v1.18.0 新增采集）。
                    // 排除 "(Average)"/"(Average Effective)" 这类封装级聚合 —— 它们不是逐核读数，
                    // 混进逐核字典会把封装值冒充成某一颗核的读数。
                    ReadCoreClock(sensor, clocks, effectiveClocks);
                    break;

                case SensorType.Voltage:
                    // v1.18.0：逐核电压。AMD 上 LHM 提供 "Core #N VID"（逐核，实测 0.99–1.34 V）
                    // 与 "Core (SVI2 TFN)"/"SoC (SVI2 TFN)"（封装级）。
                    // **只收逐核 VID**：封装级那个在本机实测恒为 1.55 V（= SVI2 解码上限，
                    // 即 VID=0 的退化读数），混进逐核列表会造出一个"8 核都是 1.55 V"的假象。
                    ReadCoreVoltage(sensor, voltages);
                    break;
            }
        }
    }

    /// <summary>
    /// 从 <see cref="SensorType.Clock"/> 传感器取逐核频率（v1.18.0）。
    /// <para>
    /// 两个字典指向两个**截然不同**的口径，混起来会让判定失去意义：
    /// </para>
    /// <list type="bullet">
    ///   <item><paramref name="clocks"/>（<c>Core #N</c>）：P-state 倍频 × 总线频率，离散档位，
    ///         与负载无关 —— 本机所有非停放核都读到 4567 MHz。界面 Tile 显示它。</item>
    ///   <item><paramref name="effectiveClocks"/>（<c>Core #N (Effective)</c>）：驻留加权的
    ///         时间平均频率 —— 空闲核 39–855 MHz、满载核 2102+ MHz。**健康判定采用它。**</item>
    /// </list>
    /// </summary>
    private static void ReadCoreClock(
        ISensor sensor,
        Dictionary<int, double> clocks,
        Dictionary<int, double> effectiveClocks)
    {
        var name = sensor.Name;
        if (name.StartsWith("CPU ", StringComparison.Ordinal))
            name = name["CPU ".Length..];

        var isEffective = name.Contains("(Effective)", StringComparison.OrdinalIgnoreCase);
        if (isEffective)
            name = name.Replace("(Effective)", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd();

        if (!name.StartsWith("Core #", StringComparison.Ordinal) || name.Contains('('))
            return;

        if (!int.TryParse(name["Core #".Length..], out var coreNo) || coreNo < 1)
            return;

        if (isEffective)
            effectiveClocks[coreNo - 1] = sensor.Value!.Value;
        else
            clocks[coreNo - 1] = sensor.Value!.Value;
    }

    /// <summary>从 <see cref="SensorType.Voltage"/> 传感器取**逐核**电压（v1.18.0）。</summary>
    private static void ReadCoreVoltage(ISensor sensor, Dictionary<int, double> voltages)
    {
        var name = sensor.Name;
        if (name.StartsWith("CPU ", StringComparison.Ordinal))
            name = name["CPU ".Length..];

        var isVid = name.EndsWith(" VID", StringComparison.OrdinalIgnoreCase);
        if (isVid)
            name = name[..^" VID".Length];

        if (!name.StartsWith("Core #", StringComparison.Ordinal) || name.Contains('('))
            return;

        if (!int.TryParse(name["Core #".Length..], out var coreNo) || coreNo < 1)
            return;

        // 有效域守卫放在源头（同温度/功耗的约定）：0.3–2.0 V 之外的读数不是"这颗核的电压"，
        // 宁可缺席让界面显示 "-"，也不用一个越界值去填充。
        if (IsPlausibleCoreVoltage(sensor.Value!.Value))
            voltages[coreNo - 1] = sensor.Value.Value;
    }

    /// <summary>逐核电压有效域（V）：低于 0.3 或高于 2.0 都不是可用的核心电压读数。</summary>
    private static bool IsPlausibleCoreVoltage(double value) => value is > 0.3 and < 2.0;

    private static void ReadFanSensor(IHardware hardware, ref double? fanRpm)
    {
        hardware.Update();

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Fan || sensor.Value is not { } rpm || rpm <= 0)
                continue;

            // 优先取 CPU 风扇；否则记录第一个风扇兜底。
            if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            {
                fanRpm = rpm;
                return;
            }

            fanRpm ??= rpm;
        }

        // SuperIO 传感器挂在主板子硬件上，逐层查找。
        foreach (var sub in hardware.SubHardware)
        {
            sub.Update();
            foreach (var sensor in sub.Sensors)
            {
                if (sensor.SensorType != SensorType.Fan || sensor.Value is not { } rpm || rpm <= 0)
                    continue;

                if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                {
                    fanRpm = rpm;
                    return;
                }

                fanRpm ??= rpm;
            }
        }
    }

    public void Dispose() => _computer.Close();
}
