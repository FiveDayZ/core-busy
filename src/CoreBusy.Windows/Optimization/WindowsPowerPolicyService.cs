namespace CoreBusy.Windows.Optimization;

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;
using Microsoft.Win32;

/// <summary>
/// 电源策略服务（v1.12.0）：读写当前电源方案中的处理器设置。
/// <para>
/// 参考实现 <c>vadyaravadim/cpu-parking-disabler</c>。三项设置同属处理器电源管理子组
/// （<c>54533251-82be-4824-96c1-47b60b740d00</c>）：
/// 核心停放最小核数 <c>CPMINCORES</c>、能源性能偏好 <c>PERFEPP</c>、
/// 处理器最大状态 <c>PROCTHROTTLEMAX</c>。一律用 **GUID** 寻址——别名在不同 Windows
/// 版本上存在差异，GUID 才是稳定契约。
/// </para>
/// <para>
/// <b>走 powrprof.dll 而不是 powercfg.exe</b>（v1.12.1 修正）。首版用
/// <c>powercfg /query SCHEME_CURRENT &lt;sub&gt; &lt;setting&gt;</c> 再正则抓
/// <c>Power Setting Index</c>，在中文系统上两条链路同时失效：
/// ① <c>/query</c> 的三参数形式**只打印方案头**，不输出设置块（实测）；
/// ② 输出标签是本地化的（<c>当前交流电源设置索引: 0x00000050</c>），
///    英文正则永远匹配不到 —— 界面于是恒显"读取电源方案失败"。
/// 改走 powrprof 的 <c>PowerReadACValue</c> / <c>PowerWriteACValueIndex</c> 后，
/// 读值不再依赖任何可被本地化的文本，也顺带消掉了"以管理员身份启动一个路径
/// 不完全确定的子进程"这个攻击面。
/// </para>
/// <para>
/// <b>活动方案 GUID 必须显式传，不能传 NULL</b>：SDK 文档把 <c>SchemeGuid</c> 标为
/// optional 并声称 NULL 表示"当前方案"，但实测本机（Windows 11 / 23H2）对三项设置
/// 一律返回 <c>ERROR_INVALID_PARAMETER(87)</c>，换成真实活动方案 GUID 才返回 0。
/// 见 <c>.workbuddy/power_probe2.py</c> 的参数矩阵。故此处一律先
/// <c>PowerGetActiveScheme</c> 取 GUID，再显式传入。
/// </para>
/// <para>
/// <b>为什么必须先备份</b>：这三项是系统级状态，改动会写进当前电源方案并跨重启保留。
/// 没有回滚路径就给用户暴露这个开关，等于让人凭感觉改一台自己看不见状态的机器。
/// 备份只在**首次**应用时写入，之后不覆盖——否则第二次应用会把"已被本工具改过"的值
/// 当成原始值存下来，原始状态就此永久丢失。
/// </para>
/// </summary>
public sealed class WindowsPowerPolicyService : IPowerPolicyService
{
    private const string SubProcessorGroup = "54533251-82be-4824-96c1-47b60b740d00";
    private const string CoreParkingMinGuid = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string EppGuid = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    private const string ProcessorMaxGuid = "bc5038f7-23e0-4960-96da-33abaf5935ec";

    /// <summary>数据目录重定向（与能量历史同一契约，供取证隔离）。</summary>
    public const string DataDirEnvVar = "COREBUSY_DATA_DIR";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string DirectoryPath
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirEnvVar);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CORE-BUSY")
                : overridden;
        }
    }

    private static string BackupPath => Path.Combine(DirectoryPath, "power-backup.json");

    public bool HasBackup => File.Exists(BackupPath);

    public PowerPolicyState Read()
    {
        if (!TryGetActiveScheme(out var scheme))
        {
            return new PowerPolicyState(false, null, null, null, HasBackup, "读取当前电源方案失败");
        }

        var parking = ReadValue(CoreParkingMinGuid, dc: false, scheme);
        var epp = ReadValue(EppGuid, dc: false, scheme);
        var maxState = ReadValue(ProcessorMaxGuid, dc: false, scheme);

        var available = parking is not null || epp is not null || maxState is not null;
        var detail = available
            ? $"核心停放 {(parking?.ToString(CultureInfo.InvariantCulture) ?? "-")}% · " +
              $"EPP {(epp?.ToString(CultureInfo.InvariantCulture) ?? "-")} · " +
              $"最大状态 {(maxState?.ToString(CultureInfo.InvariantCulture) ?? "-")}%"
            : "电源方案中未找到可读的处理器设置";

        return new PowerPolicyState(available, parking, epp, maxState, HasBackup, detail);
    }

    public bool Apply(PowerPreset preset, out string error)
    {
        error = string.Empty;

        if (preset == PowerPreset.None)
        {
            error = "未指定预设";
            return false;
        }

        if (!TryGetActiveScheme(out var scheme))
        {
            error = "无法解析当前电源方案";
            return false;
        }

        // 备份必须在任何写操作之前落盘，且只在首次写入。
        if (!SaveBackupIfAbsent(scheme, out var backupError))
        {
            error = $"备份当前电源方案失败，已中止改动：{backupError}";
            return false;
        }

        // 性能优先：解除核心停放 + EPP 拉满性能 + 处理器状态不设上限。
        // 节能优先：恢复动态停放 + EPP 偏向节能 + 处理器状态封顶 80%。
        var (parking, epp, maxState) = preset == PowerPreset.Performance
            ? (100, 0, 100)
            : (10, 100, 80);

        var failures = new List<string>();

        // PERFEPP / CPMINCORES 在多数系统上处于"隐藏"状态；隐藏只影响控制面板可见性，
        // 不阻止 API 写入，但少数 OEM 镜像会连带拒绝。失败时先解除隐藏（Attributes=0）再试一次。
        if (!TryWriteWithUnhide(CoreParkingMinGuid, parking, scheme, out var parkingError))
            failures.Add($"核心停放({parkingError})");

        if (!TryWriteWithUnhide(EppGuid, epp, scheme, out var eppError))
            failures.Add($"EPP({eppError})");

        if (!TryWriteWithUnhide(ProcessorMaxGuid, maxState, scheme, out var maxError))
            failures.Add($"处理器最大状态({maxError})");

        Activate(scheme);

        var state = Read();

        if (failures.Count > 0)
        {
            error = $"部分设置未能应用：{string.Join("、", failures)}（备份已保留，可还原）";
            return false;
        }

        return true;
    }

    public bool Restore(out string error)
    {
        error = string.Empty;

        if (!TryGetActiveScheme(out var scheme))
        {
            error = "无法解析当前电源方案";
            return false;
        }

        if (!TryLoadBackup(out var backup, out var loadError))
        {
            error = loadError;
            return false;
        }

        var failures = new List<string>();

        foreach (var (guid, label) in new[]
                 {
                     (CoreParkingMinGuid, "核心停放"),
                     (EppGuid, "EPP"),
                     (ProcessorMaxGuid, "处理器最大状态"),
                 })
        {
            if (backup.Ac.TryGetValue(guid, out var ac) && !TryWrite(guid, ac, dc: false, scheme))
                failures.Add($"{label}(AC)");

            if (backup.Dc.TryGetValue(guid, out var dc) && !TryWrite(guid, dc, dc: true, scheme))
                failures.Add($"{label}(DC)");
        }

        Activate(scheme);

        if (failures.Count > 0)
        {
            error = $"还原时部分设置失败：{string.Join("、", failures)}";
            return false;
        }

        return true;
    }

    // ===== 读 =====

    /// <summary>
    /// 读取某项设置的当前索引。读不到返回 null —— 绝不拿 0 冒充"未知"，
    /// 否则界面上 EPP=0（最高性能）会与"读失败"混为一谈。DPAPI 的
    /// <c>POWER_ACTION</c> 类设置返回的也不是 DWORD，故必须校验 size。
    /// </summary>
    private static int? ReadValue(string settingGuid, bool dc, Guid scheme)
    {
        var subGroup = ParseGuid(SubProcessorGroup);
        var setting = ParseGuid(settingGuid);
        var schemeGuid = scheme;

        var buffer = new byte[16];
        uint size = (uint)buffer.Length;

        var status = dc
            ? PowerReadDCValue(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, out _, buffer, ref size)
            : PowerReadACValue(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, out _, buffer, ref size);

        if (status != 0 || size < sizeof(uint))
        {
            return null;
        }

        // 三项均为 REG_DWORD，原生小端 4 字节。
        return (int)BitConverter.ToUInt32(buffer, 0);
    }

    // ===== 写 =====

    /// <summary>同时写入 AC 与 DC 值（笔记本上两者环境不同，必须都覆盖）。</summary>
    private static bool TryWrite(string settingGuid, int value, bool dc, Guid scheme) =>
        TryWrite(settingGuid, value, dc, scheme, out _);

    private static bool TryWrite(string settingGuid, int value, bool dc, Guid scheme, out uint status)
    {
        var subGroup = ParseGuid(SubProcessorGroup);
        var setting = ParseGuid(settingGuid);
        var schemeGuid = scheme;

        status = dc
            ? PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, (uint)value)
            : PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, (uint)value);

        return status == 0;
    }

    /// <summary>同时写 AC/DC；任一失败则解除隐藏标记后整组重试一次。</summary>
    private static bool TryWriteWithUnhide(string settingGuid, int value, Guid scheme, out string error)
    {
        error = string.Empty;

        var acOk = TryWrite(settingGuid, value, dc: false, scheme, out var acStatus);
        var dcOk = TryWrite(settingGuid, value, dc: true, scheme, out var dcStatus);

        if (acOk && dcOk)
            return true;

        if (TryUnhide(settingGuid))
        {
            acOk |= TryWrite(settingGuid, value, dc: false, scheme, out acStatus);
            dcOk |= TryWrite(settingGuid, value, dc: true, scheme, out dcStatus);
        }

        if (acOk && dcOk)
            return true;

        error = $"AC={StatusText(acStatus)} DC={StatusText(dcStatus)}";
        return false;
    }

    /// <summary>
    /// 让电源方案重新生效。PowerWrite*ValueIndex 只改方案存储，
    /// 不再 setactive 一次的话新值要等用户手动切方案才会生效。
    /// </summary>
    private static void Activate(Guid scheme)
    {
        var schemeGuid = scheme;
        _ = PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
    }

    /// <summary>解除设置项的隐藏标记（Attributes=0）。</summary>
    private static bool TryUnhide(string settingGuid)
    {
        try
        {
            var keyPath = $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{SubProcessorGroup}\{settingGuid}";
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            if (key is null)
                return false;

            key.SetValue("Attributes", 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string StatusText(uint status) => status switch
    {
        0 => "ok",
        5 => "拒绝访问(需管理员)",
        87 => "参数无效",
        _ => $"0x{status:X}",
    };

    // ===== 备份 =====

    /// <summary>
    /// 首次应用时记录原始值。**已存在则不改写** —— 见类注释中关于原始状态丢失的说明。
    /// </summary>
    private static bool SaveBackupIfAbsent(Guid scheme, out string error)
    {
        error = string.Empty;

        if (File.Exists(BackupPath))
            return true;

        try
        {
            var backup = new PowerBackup
            {
                SavedAt = DateTime.Now,
                Scheme = scheme.ToString(),
                Ac = CaptureAll(dc: false, scheme),
                Dc = CaptureAll(dc: true, scheme),
            };

            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Dictionary<string, int> CaptureAll(bool dc, Guid scheme)
    {
        var result = new Dictionary<string, int>();
        foreach (var guid in new[] { CoreParkingMinGuid, EppGuid, ProcessorMaxGuid })
        {
            var value = ReadValue(guid, dc, scheme);
            if (value is not null)
                result[guid] = value.Value;
        }

        return result;
    }

    private static bool TryLoadBackup(out PowerBackup backup, out string error)
    {
        backup = new PowerBackup();
        error = string.Empty;

        if (!File.Exists(BackupPath))
        {
            error = "尚未备份过电源方案，无可还原的内容";
            return false;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<PowerBackup>(File.ReadAllText(BackupPath));
            if (loaded is null)
            {
                error = "备份文件内容为空";
                return false;
            }

            backup = loaded;
            return true;
        }
        catch (Exception ex)
        {
            error = $"备份文件解析失败：{ex.Message}";
            return false;
        }
    }

    private static Guid ParseGuid(string value) => new(value);

    /// <summary>取当前活动电源方案 GUID（返回的内存由 LocalFree 释放）。</summary>
    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = default;

        var status = PowerGetActiveScheme(IntPtr.Zero, out var activePtr);
        if (status != 0 || activePtr == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            scheme = Marshal.PtrToStructure<Guid>(activePtr);
            return true;
        }
        finally
        {
            LocalFree(activePtr);
        }
    }

    // ===== powrprof 互操作 =====
    // 注意：SchemeGuid 必须传**真实活动方案 GUID**。SDK 文档把该参数标为
    // optional 并声称 NULL 表示"当前方案"，但实测传 NULL 时三项设置一律返回
    // ERROR_INVALID_PARAMETER(87)。故一律经 TryGetActiveScheme 取 GUID 后传入。

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadACValue(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint type,
        [Out] byte[] buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadDCValue(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint type,
        [Out] byte[] buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint value);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint value);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>电源方案原始值备份（AC / DC 分开存，键为设置 GUID）。</summary>
    private sealed class PowerBackup
    {
        public DateTime SavedAt { get; set; }

        /// <summary>备份时的活动方案 GUID（仅作留痕，还原时不依赖它）。</summary>
        public string Scheme { get; set; } = string.Empty;

        public Dictionary<string, int> Ac { get; set; } = [];

        public Dictionary<string, int> Dc { get; set; } = [];
    }
}
