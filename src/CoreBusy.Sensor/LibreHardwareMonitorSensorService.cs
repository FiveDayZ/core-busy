namespace CoreBusy.Sensor;

using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using CoreBusy.Core.Energy;
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

    public void Start() => _computer.Open();

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
        var activeFanCount = 0;

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
                                   ref packagePower,
                                   clocks, effectiveClocks, voltages);
                }
                else if (hardware.HardwareType == HardwareType.Motherboard)
                {
                    activeFanCount = ReadFanSensor(hardware, ref fanRpm);
                }
            }
        }
        catch (Exception)
        {
            // 单次采样失败按空快照处理，避免监控线程崩溃；UI 沿用上次值/显示缺省。
        }

        // GPU / 存储（v1.16.0 状态栏扩展；v1.20.1 起存储另带活动度供功耗模型使用）：
        // GPU 每帧读（NVML/nvapi 开销小），硬盘 SMART 查询较重，按 StorageRefreshSeconds
        // 节流取缓存 —— 活动度与盘温共用同一次刷新，避免两处各自 Update 造成时间基准错位。
        var gpu = ReadGpuSensors();
        var storage = ReadStorageThrottled();

        var adoptedTemp = packageTemp ?? firstTemp;

        return new HardwareSensorSnapshot
        {
            PackageTemperatureC = adoptedTemp,
            MaxCoreTemperatureC = maxCoreTemp,
            PackagePowerW = packagePower,
            FanRpm = fanRpm,
            ActiveFanCount = activeFanCount,
            CoreClockMhz = clocks,
            CoreEffectiveClockMhz = effectiveClocks,
            CoreVoltageV = voltages,
            GpuTemperatureC = gpu.TemperatureC,
            GpuUtilizationPercent = gpu.UtilizationPercent,
            DiscreteGpuPowerW = gpu.DiscretePowerW,
            DiscreteGpuNames = gpu.DiscreteNames,
            DriveTemperatureC = storage.Temperatures.Count > 0 ? storage.Temperatures.Max(d => d.TemperatureC) : null,
            DriveTemperatures = storage.Temperatures,
            StorageThroughputMbps = storage.ThroughputMbps,
            StorageBusyPercent = storage.BusyPercent,
        };
    }

    // ---------------------------------------------------------------- GPU / 存储

    /// <summary>硬盘温度读取节流间隔（秒）。SMART 查询较重，不值得每秒做。</summary>
    private const double StorageRefreshSeconds = 15.0;

    private readonly Stopwatch _storageClock = Stopwatch.StartNew();
    private bool _storagePrimed;

    /// <summary>
    /// 读显卡读数（v1.20.1）：主卡的温度/占用率 + **全部独显**的实测功率之和与节点名。
    /// <para>
    /// <b>独显优先且逐卡判别：</b>多 GPU（核显 + 独显）时主卡取独显 —— 判据与状态栏的显卡排序
    /// 统一为 <see cref="GpuRatedPower.LooksDiscrete"/>（旧版按厂商加权，遇到"AMD 核显 + AMD 独显"
    /// 这种同厂商组合会取到核显）。独显名单单独带出，因为核显的功耗已含在 CPU 封装读数里，
    /// 只能对独显单独计功耗，判错的后果是凭空多算或漏算一二百瓦。
    /// </para>
    /// <para>
    /// 温度取名次序：GPU Core → GPU Hot Spot → 首个温度传感器（部分卡只报 Hot Spot）。
    /// </para>
    /// </summary>
    /// <summary>读一次显卡时的返回：主卡温度/占用率 + 全部独显功率之和 + 独显节点名。</summary>
    private readonly record struct GpuReading(
        double? TemperatureC,
        double? UtilizationPercent,
        double? DiscretePowerW,
        IReadOnlyList<string> DiscreteNames);

    private GpuReading ReadGpuSensors()
    {
        try
        {
            GpuNodeReading? mainCard = null;
            double? discretePower = null;
            var discreteNames = new List<string>();

            foreach (var hardware in _computer.Hardware)
            {
                var vendorRank = hardware.HardwareType switch
                {
                    HardwareType.GpuNvidia => 3,
                    HardwareType.GpuAmd => 2,
                    HardwareType.GpuIntel => 1,
                    _ => 0,
                };

                if (vendorRank <= 0)
                    continue;

                // 独显权重（+4）压过任何厂商差：AMD 核显(2) vs AMD 独显(6) 取独显；
                // 核显之间再按厂商排序。同分取先出现者（枚举顺序稳定）。
                var name = hardware.Name;
                var discrete = GpuRatedPower.LooksDiscrete(name);
                var rank = vendorRank + (discrete ? 4 : 0);

                hardware.Update();
                var (temperature, usage, power) = ReadGpuNodeSensors(hardware);

                if (mainCard is null || rank > mainCard.Value.Rank)
                    mainCard = new GpuNodeReading(rank, temperature, usage);

                if (!discrete)
                    continue;

                discreteNames.Add(name);

                // 只做收集（有限、非负、上界已由 IsPlausibleGpuPower 守卫），
                // "0 W 是真读数还是失效读数"的判定交给功耗模型统一做 —— 那需要占用率，
                // 而模型层才是"什么算可用读数"的唯一定义处。
                if (power is { } watt)
                    discretePower = (discretePower ?? 0) + Math.Max(0, watt);
            }

            var main = mainCard;
            return new GpuReading(main?.TemperatureC, main?.UtilizationPercent, discretePower, discreteNames);
        }
        catch
        {
            return new GpuReading(null, null, null, []);
        }
    }

    /// <summary>主卡节点（只保留排名、温度、占用率三样，功耗单独按独显名单汇总）。</summary>
    private readonly record struct GpuNodeReading(int Rank, double? TemperatureC, double? UtilizationPercent);

    /// <summary>读单个 GPU 节点的温度 / 占用率 / 功率。</summary>
    private static (double? TemperatureC, double? UtilizationPercent, double? PowerW) ReadGpuNodeSensors(
        IHardware hardware)
    {
        double? coreTemp = null;
        double? hotSpotTemp = null;
        double? fallbackTemp = null;
        double? coreUsage = null;
        double? preferredPower = null;
        double? anyPower = null;

        foreach (var sensor in hardware.Sensors)
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
            else if (sensor.SensorType == SensorType.Power && IsPlausibleGpuPower(value))
            {
                // 同一块卡可能有多个功率传感器（整卡 / 核心 / 显存）。整卡口径优先，
                // 其余取最大值兜底 —— 显存功率是整卡的一部分，取大不会小于真实整卡。
                if (IsWholeCardPowerSensor(sensor.Name))
                    preferredPower = Math.Max(preferredPower ?? 0, value);
                else
                    anyPower = Math.Max(anyPower ?? 0, value);
            }
        }

        return (coreTemp ?? hotSpotTemp ?? fallbackTemp, coreUsage, preferredPower ?? anyPower);
    }

    /// <summary>是否为"整卡"口径的功率传感器（NVIDIA 用 GPU Package，AMD 用 GPU Core/Board）。</summary>
    private static bool IsWholeCardPowerSensor(string name)
        => name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Core", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Board", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Total", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 显卡功率读数的有效域（W）。上界与功耗模型的 <c>MaxGpuWatt</c> 取同一量级：
    /// 越界的值不是"这块卡很费电"，而是单位错误或异常读数，宁可缺席让模型去估。
    /// <para>
    /// <b>下界是 0（与 CPU 相反）：</b>CPU 通电必有功耗，所以它的 0 是"读不到"的占位；
    /// 而独显在 Optimus/运行时 D3 状态下**确实会被下电**，0 W 是真实读数。
    /// 挡住 0 会让笔记本上那块休眠的独显被型号估算填回一二十瓦 —— 一个只在笔记本上出现、
    /// 且看起来完全合理的错数（v1.20.1 实测修正）。
    /// </para>
    /// </summary>
    private static bool IsPlausibleGpuPower(double value) => value is >= 0 and <= 1000;

    /// <summary>一次存储刷新得到的全部结果：盘温 + 活动度（吞吐 / 忙率）。</summary>
    private readonly record struct StorageReading(
        IReadOnlyList<DriveTemperatureReading> Temperatures,
        double? ThroughputMbps,
        double? BusyPercent);

    /// <summary>单块盘本拍的活动度读数。</summary>
    private readonly record struct DriveActivity(
        double? ReadRateMbps,
        double? WriteRateMbps,
        double? BusyPercent,
        double DeltaGb,
        bool CountersChanged);

    private StorageReading _cachedStorage = new([], null, null);

    /// <summary>各盘上一拍的累计读写量（GB），按硬件标识索引（同一型号的两块盘 Name 会相同，标识不会）。</summary>
    private readonly Dictionary<string, (double ReadGb, double WrittenGb)> _lastDriveCounters = new(StringComparer.Ordinal);

    /// <summary>上一次**观测到累计量变化**的时刻，用于把增量折算成平均带宽。</summary>
    private DateTime _lastCounterObservedUtc;

    private bool _counterPrimed;
    private double? _lastThroughputMbps;

    /// <summary>按节流间隔刷新存储读数（盘温与活动度共用一次 Update，保证两者时间基准一致）。</summary>
    private StorageReading ReadStorageThrottled()
    {
        if (!_storagePrimed || _storageClock.Elapsed.TotalSeconds >= StorageRefreshSeconds)
        {
            _storagePrimed = true;
            _storageClock.Restart();
            _cachedStorage = ReadStorage();
        }

        return _cachedStorage;
    }

    /// <summary>
    /// 读逐块盘的温度与活动度。
    /// <para>
    /// <b>活动度的两个口径（v1.20.1）：</b>优先用 LHM 的 <c>Throughput</c>（Read/Write Rate，MB/s）；
    /// 本机 NVMe 实测这两个传感器为 null，故退到「累计读写量 <c>Data Read</c>/<c>Data Written</c>
    /// 差商」—— 累计量是**实测**的，只是需要在两个时刻上相减。
    /// </para>
    /// <para>
    /// <b>差商必须按真实观测间隔算：</b>存储节点 15 s 才 <c>Update()</c> 一次，
    /// 若每帧都拿"距上次观测 1 秒"去除，会把 15 秒里攒下的写入量算成 15 倍带宽
    /// （本机实测可达 GB/s 级），于是整机功耗里凭空多出一个满载硬盘。
    /// 故只在累计量**真的变化**时结算一次，并记下该时刻。
    /// </para>
    /// </summary>
    private StorageReading ReadStorage()
    {
        var temperatures = new List<DriveTemperatureReading>();
        double? busyMax = null;
        double? rateMax = null;
        var deltas = new List<double>();
        var countersChanged = false;
        var now = DateTime.UtcNow;

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
                    temperatures.Add(new DriveTemperatureReading { Name = hardware.Name, TemperatureC = value });

                var activity = ReadDriveActivity(hardware.Sensors, hardware.Identifier.ToString());

                if (activity.BusyPercent is { } busy)
                    busyMax = Math.Max(busyMax ?? double.MinValue, busy);

                // 吞吐口径：只要该盘给了 Read/Write Rate 就用它（缺失的一侧按 0 计），
                // 取全机最忙那块盘作为整机存储项的驱动量。
                if (activity.ReadRateMbps is not null || activity.WriteRateMbps is not null)
                {
                    var perDriveRate = (activity.ReadRateMbps ?? 0) + (activity.WriteRateMbps ?? 0);
                    rateMax = Math.Max(rateMax ?? double.MinValue, perDriveRate);
                }

                if (activity.CountersChanged)
                {
                    countersChanged = true;
                    deltas.Add(activity.DeltaGb);
                }
            }
        }
        catch
        {
            // 整轮枚举失败：返回已拿到的部分，不吞掉已有读数。
        }

        if (rateMax is not null)
        {
            _lastThroughputMbps = rateMax;
        }
        else if (countersChanged)
        {
            var seconds = _counterPrimed ? (now - _lastCounterObservedUtc).TotalSeconds : 0;
            _lastCounterObservedUtc = now;
            _counterPrimed = true;

            if (seconds > 0.5 && deltas.Count > 0)
            {
                // 取"最忙那块盘"的增量：无法把总吞吐归因到单块盘时，
                // 用最忙的那块驱动功耗摆幅，比按全部盘求和更保守。
                _lastThroughputMbps = deltas.Max() * 1024.0 / seconds;
            }
        }

        return new StorageReading(temperatures, _lastThroughputMbps, busyMax);
    }

    /// <summary>
    /// 单块盘的活动度。
    /// <para>
    /// <b>刻意不采信 <c>Total Activity</c>：</b>本机实测该传感器在 <c>Read Activity</c>、
    /// <c>Write Activity</c> 都 ≈ 0（盘空闲）时仍报告 99.999985（见 .workbuddy/lhm_dump_v1200.txt），
    /// 用它会把一块常年空闲的盘算成永久满载 +8 W。忙率只取读写两个分项的大者。
    /// </para>
    /// </summary>
    private DriveActivity ReadDriveActivity(IReadOnlyList<ISensor> sensors, string driveId)
    {
        double? readRate = null;
        double? writeRate = null;
        double? readBusy = null;
        double? writeBusy = null;
        var readGb = 0.0;
        var writtenGb = 0.0;
        var hasRead = false;
        var hasWritten = false;

        foreach (var sensor in sensors)
        {
            if (sensor.Value is not { } value)
                continue;

            switch (sensor.SensorType)
            {
                // LHM 的存储 Throughput 传感器单位是 **bytes/s**，不是它名义上的 MB/s。
                // v1.21.0 用可控写入负载标定（.workbuddy/storageprobe：640 MB × Flush(true)）：
                // 真实平均写入 211.6 MB/s 时，同一时刻传感器报 222,387,872 —— 相差 10^6。
                // 直接当 MB/s 采信的后果不是"数字大一点"：一条空闲盘的 0.26 MB/s 会变成
                // 269,535 MB/s，落进模型层 [0, 100000] 的合理域**之内**（或恰好越界），
                // 于是 `StorageActivityRatio` 被 clamp 到 1.0、硬盘项按满载 +6.8 W 计 ——
                // 一个随字节速率随机出现、只看界面完全看不出来的偏差。
                case SensorType.Throughput when sensor.Name.StartsWith("Read", StringComparison.OrdinalIgnoreCase):
                    readRate = ToMegabytesPerSecond(value);
                    break;

                case SensorType.Throughput when sensor.Name.StartsWith("Write", StringComparison.OrdinalIgnoreCase):
                    writeRate = ToMegabytesPerSecond(value);
                    break;

                case SensorType.Load when sensor.Name.StartsWith("Read", StringComparison.OrdinalIgnoreCase):
                    readBusy = value;
                    break;

                case SensorType.Load when sensor.Name.StartsWith("Write", StringComparison.OrdinalIgnoreCase):
                    writeBusy = value;
                    break;

                // 累计量口径：单位是 **GB 且为整数粒度**（v1.21.0 实测：连续写入 640 MB 只让
                // Data Written 从 539 跳到 540）。因此差商路径只有在"刚好跨过一个 GB 边界"时
                // 才拿得到值，其余时刻恒为 0 —— 它只是 Throughput 传感器不可用时的兜底，
                // 不是主口径。差值仍按 ×1024 折算成 MB（GB → MiB 是 1024，与 GB 的定义一致）。
                case SensorType.Data when sensor.Name.Equals("Data Read", StringComparison.OrdinalIgnoreCase):
                    readGb = value;
                    hasRead = true;
                    break;

                case SensorType.Data when sensor.Name.Equals("Data Written", StringComparison.OrdinalIgnoreCase):
                    writtenGb = value;
                    hasWritten = true;
                    break;
            }
        }

        var changed = false;
        var deltaGb = 0.0;

        if (hasRead || hasWritten)
        {
            if (_lastDriveCounters.TryGetValue(driveId, out var previous))
            {
                var deltaRead = Math.Max(0, readGb - previous.ReadGb);
                var deltaWritten = Math.Max(0, writtenGb - previous.WrittenGb);
                deltaGb = deltaRead + deltaWritten;
                changed = deltaGb > 0;
            }

            _lastDriveCounters[driveId] = (readGb, writtenGb);
        }

        double? busyPercent = null;
        if (readBusy is not null || writeBusy is not null)
            busyPercent = Math.Clamp(Math.Max(readBusy ?? 0, writeBusy ?? 0), 0, 100);

        return new DriveActivity(readRate, writeRate, busyPercent, deltaGb, changed);
    }

    /// <summary>
    /// LHM 存储 Throughput 传感器的原始值 → MB/s。
    /// <para>
    /// 该传感器名义上是 MB/s，实测是 <b>bytes/s</b>：v1.21.0 用可控写入负载标定
    /// （<c>.workbuddy/storageprobe</c>，640 MB 分片 <c>Flush(true)</c> 落盘），
    /// 真实平均 211.6 MB/s 时传感器读到 222,387,872，比值 ≈ 1.05×10^6。
    /// </para>
    /// <para>
    /// 单位错必须在这里修掉，不能留给模型层的"合理域"闸：闸门对 10^6 的量级错误只是
    /// 偶发有效（<c>269535</c> 越界被丢，<c>86968</c> 却落在闸内被采信）。
    /// </para>
    /// </summary>
    private static double ToMegabytesPerSecond(double bytesPerSecond) => bytesPerSecond / (1024.0 * 1024.0);

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

    private static void ReadCpuSensors(
        IHardware hardware,
        ref double? packageTemp,
        ref double? maxCoreTemp,
        ref double? firstTemp,
        ref double? packagePower,
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
                        break;

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

    /// <summary>
    /// 读风扇转速（优先取 CPU 风扇）并返回**转动中**的风扇数量（v1.20.1）。
    /// <para>
    /// 计数与取值用同一个合理域守卫：空风扇接口常报 0（本机 5 个接口只有 1 个在转），
    /// 少数主板对空接口会报一个恒定的噪声值，故 <see cref="IsPlausibleFanRpm"/> 上下都设界。
    /// 整机功耗模型用这个**个数**（每风扇按常数计），不用转速反推功率 ——
    /// 风扇的 P–Q 曲线随型号差异太大，用转速换算比取常数更不准。
    /// </para>
    /// </summary>
    private static int ReadFanSensor(IHardware hardware, ref double? fanRpm)
    {
        hardware.Update();

        var count = CountActiveFans(hardware, ref fanRpm);

        // SuperIO 传感器挂在主板子硬件上，逐层查找。
        foreach (var sub in hardware.SubHardware)
        {
            sub.Update();
            count += CountActiveFans(sub, ref fanRpm);
        }

        return count;
    }

    /// <summary>数一层硬件上"转速可信"的风扇，并顺带确定要显示的那一个（CPU 风扇优先）。</summary>
    private static int CountActiveFans(IHardware hardware, ref double? fanRpm)
    {
        var count = 0;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Fan || sensor.Value is not { } rpm)
                continue;

            if (!IsPlausibleFanRpm(rpm))
                continue;

            count++;

            // 优先 CPU 风扇；否则保留第一个可信读数（不覆盖，避免后出现的机箱风扇顶掉 CPU 风扇）。
            if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || fanRpm is null)
                fanRpm = rpm;
        }

        return count;
    }

    /// <summary>
    /// 风扇转速的有效域（RPM）。下界 200：更低的值不是"风扇在慢转"，而是空接口的噪声读数；
    /// 上界 5000：机箱/散热器风扇的实际上限，超过它只能是传感器标定错误。
    /// </summary>
    private static bool IsPlausibleFanRpm(double rpm) => rpm is >= 200 and <= 5000;

    public void Dispose() => _computer.Close();
}
