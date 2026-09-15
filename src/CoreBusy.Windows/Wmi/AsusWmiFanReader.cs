namespace CoreBusy.Windows.Wmi;

using System.Management;

/// <summary>
/// ASUS 笔记本风扇转速读取（v1.16.3）。
/// <para>
/// 为什么不能靠 LibreHardwareMonitor：笔记本风扇由 EC 控制，而 LHM 0.9.6 在 ASUS GA402XV 上
/// 整棵硬件树里连一个 <c>SensorType.Fan</c> 都没有（主板节点下没有 SuperIO/EC 子硬件），
/// 且 <c>Computer</c> 也没有 EmbeddedController 采集开关。所以"风扇读不到"不是取值口径问题，
/// 而是那一层压根没有这条数据 —— 无论提权与否。
/// </para>
/// <para>
/// 可用通道是 ASUS 自家的只读 ACPI 接口：WMI <c>root\wmi</c> 类 <c>AsusAtkWmi_WMNB</c> 的
/// <c>DSTS</c> 方法。实测（GA402XV / Ryzen 9 7940HS，提权）：
/// CPU <c>0x00110013</c> → <c>0x00010029</c> = 4100 RPM，
/// GPU <c>0x00110014</c> → <c>0x00010026</c> = 3800 RPM；
/// 中置风扇 <c>0x00110031</c> → <c>0xFFFFFFFE</c>（本机型无此端点，哨兵值）。
/// 返回值低 16 位 = 转速 / 100。
/// </para>
/// <para>
/// 该接口**只对提升后的进程开放**：非提权时连类枚举都返回"拒绝访问"。
/// 本类只读，不写 EC，不改风扇策略。
/// </para>
/// </summary>
public sealed class AsusWmiFanReader : IDisposable
{
    private const string WmiNamespace = @"root\wmi";
    private const string WmiClassName = "AsusAtkWmi_WMNB";
    private const string DstsMethod = "DSTS";
    private const string DeviceIdArgument = "Device_ID";
    private const string StatusProperty = "device_status";

    /// <summary>CPU 风扇端点（ASUS ACPI 约定，与 G-Helper 一致）。</summary>
    public const uint CpuFanDeviceId = 0x00110013;

    /// <summary>GPU 风扇端点。</summary>
    public const uint GpuFanDeviceId = 0x00110014;

    /// <summary>刷新节流（秒）：风扇转速变化慢，不必每个采样周期都走一次 WMI。</summary>
    private const double RefreshSeconds = 2.0;

    /// <summary>低 16 位的合理上限（200 → 20000 RPM）；再高只能是哨兵或错读。</summary>
    private const uint MaxPlausibleTenths = 200;

    private readonly object _gate = new();
    private ManagementObject? _device;
    private bool _probeDone;
    private string _unavailableReason = string.Empty;
    private DateTime _lastReadUtc = DateTime.MinValue;
    private AsusFanReading _last = AsusFanReading.Unavailable("尚未读取");
    private bool? _lastLoggedAvailability;

    /// <summary>
    /// 读取 CPU / GPU 风扇转速（带 2 秒缓存）。非 ASUS 机型、未提权或接口缺失时返回原因文本，
    /// 而不是假装读到 0。
    /// </summary>
    public AsusFanReading Read()
    {
        lock (_gate)
        {
            if ((DateTime.UtcNow - _lastReadUtc).TotalSeconds < RefreshSeconds)
                return _last;

            _lastReadUtc = DateTime.UtcNow;
            _last = ReadCore();
            LogOnAvailabilityChange(_last);
            return _last;
        }
    }

    private AsusFanReading ReadCore()
    {
        if (!EnsureDevice())
            return AsusFanReading.Unavailable(_unavailableReason);

        uint? cpuRaw = null;
        uint? gpuRaw = null;

        try
        {
            cpuRaw = QueryRaw(CpuFanDeviceId);
            gpuRaw = QueryRaw(GpuFanDeviceId);
        }
        catch (Exception ex)
        {
            return AsusFanReading.Unavailable($"ASUS WMI 风扇查询失败：{ex.GetType().Name}: {ex.Message}");
        }

        var cpu = cpuRaw is { } cr ? DecodeRpm(cr) : null;
        var gpu = gpuRaw is { } gr ? DecodeRpm(gr) : null;

        _rawEvidence = $"CPU id=0x{CpuFanDeviceId:X8} raw={Format(cpuRaw)} → {Format(cpu)}"
                       + $"；GPU id=0x{GpuFanDeviceId:X8} raw={Format(gpuRaw)} → {Format(gpu)}";

        if (cpu is null && gpu is null)
        {
            return AsusFanReading.Unavailable(
                $"ASUS WMI 未提供可用风扇端点（{_rawEvidence}）");
        }

        return new AsusFanReading(cpu, gpu, $"来源：ASUS WMI（{WmiClassName}.DSTS）");
    }

    private string _rawEvidence = string.Empty;

    /// <summary>
    /// 解析 DSTS 返回值 → RPM。哨兵值与越界值一律返回 <c>null</c>（读不到就是读不到）。
    /// </summary>
    /// <remarks>
    /// 位布局（GA402XV 实测）：低 16 位 = 转速 / 100，高 16 位为标志位（实测 0x0001）。
    /// 本机型不存在的端点返回 <c>0xFFFFFFFE</c>，<c>0xFFFFFFFF</c> / <c>0x0000FFFF</c> 同为哨兵。
    /// 注意"真实 0 转速"（风扇停转）会正常返回 <c>0.0</c>，与"读不到"（<c>null</c>）严格区分。
    /// </remarks>
    public static double? DecodeRpm(uint raw)
    {
        if (raw is 0xFFFFFFFF or 0xFFFFFFFE or 0x0000FFFF)
            return null;

        var tenths = raw & 0xFFFF;
        var flags = raw >> 16;
        if (flags > 1 || tenths > MaxPlausibleTenths)
            return null;

        return tenths * 100.0;
    }

    private uint? QueryRaw(uint deviceId)
    {
        if (_device is null)
            return null;

        using var inParams = _device.GetMethodParameters(DstsMethod);
        inParams[DeviceIdArgument] = deviceId;

        using var outParams = _device.InvokeMethod(DstsMethod, inParams, null);
        var value = outParams?[StatusProperty];
        return value is null ? null : Convert.ToUInt32(value);
    }

    private bool EnsureDevice()
    {
        if (_probeDone)
            return _device is not null;

        _probeDone = true;

        try
        {
            using var managementClass = new ManagementClass(WmiNamespace, WmiClassName, null);
            foreach (ManagementObject instance in managementClass.GetInstances())
            {
                _device = instance;
                break;
            }

            if (_device is null)
                _unavailableReason = $"本机没有 {WmiClassName} 实例（非 ASUS 机型或未安装 ASUS 系统控制接口）";
        }
        catch (ManagementException ex) when (IsAccessDenied(ex))
        {
            // ASUS 新版 System Control Interface 收紧了权限：非提权连类枚举都被拒。
            _unavailableReason = "读取 ASUS 风扇需要以管理员身份运行（WMI 拒绝访问）";
        }
        catch (Exception ex)
        {
            _unavailableReason = $"ASUS WMI 不可用：{ex.GetType().Name}: {ex.Message}";
        }

        return _device is not null;
    }

    private static bool IsAccessDenied(ManagementException ex)
        => ex.ErrorCode == ManagementStatus.AccessDenied
           || ex.Message.Contains("拒绝访问", StringComparison.Ordinal)
           || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);

    /// <summary>只在"可用性翻转"时写一行日志，避免每秒刷屏。</summary>
    private void LogOnAvailabilityChange(AsusFanReading reading)
    {
        if (_lastLoggedAvailability == reading.HasValue)
            return;

        _lastLoggedAvailability = reading.HasValue;

        WmiLog.Write(reading.HasValue
            ? $"[FAN] ASUS WMI 风扇已可读：{_rawEvidence}"
            : $"[FAN] ASUS WMI 风扇不可读：{reading.Detail}");
    }

    private static string Format(double? rpm) => rpm is { } v ? $"{v:0} RPM" : "-";

    private static string Format(uint? raw) => raw is { } v ? $"0x{v:X8}" : "(查询失败)";

    public void Dispose()
    {
        lock (_gate)
        {
            try { _device?.Dispose(); } catch { }
            _device = null;
        }
    }
}
