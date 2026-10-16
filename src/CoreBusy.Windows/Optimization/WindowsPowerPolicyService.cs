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

    private readonly bool _temporary;
    private string BackupPath => Path.Combine(DirectoryPath, _temporary ? "power-game-backup.json" : "power-backup.json");

    private readonly Func<Guid?> _currentScheme;
    private readonly Func<string, bool, Guid, int?> _read;
    private readonly Func<string, int, bool, Guid, bool> _write;
    private readonly Func<Guid, bool> _activate;
    private static readonly string[] SettingGuids = [CoreParkingMinGuid, EppGuid, ProcessorMaxGuid];

    public WindowsPowerPolicyService() : this(
        () => TryGetActiveScheme(out var scheme) ? scheme : null,
        ReadValue, WriteWithUnhide,
        scheme => { var id = scheme; return PowerSetActiveScheme(IntPtr.Zero, ref id) == 0; }) { }

    // 将原生调用边界注入测试，回归不改动实际系统电源方案。
    internal WindowsPowerPolicyService(Func<Guid?> currentScheme,
        Func<string, bool, Guid, int?> read,
        Func<string, int, bool, Guid, bool> write, Func<Guid, bool> activate, bool temporary = false)
    {
        _currentScheme = currentScheme; _read = read; _write = write; _activate = activate; _temporary = temporary;
    }

    public bool HasBackup
    {
        get { try { return LoadBackups().Schemes.Count > 0 || (!_temporary && TemporaryService().HasBackup); } catch { return false; } }
    }

    private WindowsPowerPolicyService TemporaryService() => new(_currentScheme, _read, _write, _activate, temporary: true);
    public bool ApplyTemporary(PowerPreset preset, out string error) => TemporaryService().Apply(preset, out error);
    public bool HasTemporaryBackup
    {
        get { try { return TemporaryService().LoadBackups().Schemes.Count > 0; } catch { return true; } }
    }
    public bool RestoreTemporary(out string error)
    {
        var temporary = TemporaryService();
        try
        {
            if (temporary.LoadBackups().Schemes.Count == 0) { error = ""; return true; }
            return temporary.Restore(out error);
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    public void DiscardTemporaryBackup()
    {
        try { File.Delete(TemporaryService().BackupPath); } catch { /* 不影响固定策略或程序退出。 */ }
    }

    public PowerPolicyState Read()
    {
        if (_currentScheme() is not { } scheme)
        {
            return new PowerPolicyState(false, null, null, null, HasBackup, "读取当前电源方案失败");
        }

        var parking = _read(CoreParkingMinGuid, false, scheme);
        var epp = _read(EppGuid, false, scheme);
        var maxState = _read(ProcessorMaxGuid, false, scheme);

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
        error = "";
        if (preset is not (PowerPreset.Performance or PowerPreset.Saver))
        {
            error = "未指定有效预设";
            return false;
        }
        if (_currentScheme() is not { } scheme)
        {
            error = "无法解析当前电源方案";
            return false;
        }
        PowerBackup? before = null;
        var writesStarted = false;
        PowerBackupFile? backups = null;
        string? previousTarget = null;
        var addedBackup = false;
        void RestoreBackupIndex()
        {
            if (backups is null) return;
            if (addedBackup) backups.Schemes.Remove(scheme.ToString());
            backups.LastAppliedScheme = previousTarget;
            SaveBackups(backups);
        }
        try
        {
            backups = LoadBackups();
            previousTarget = backups.LastAppliedScheme;
            before = Capture(scheme); // 六项完整读取成功后才允许写入。
            var key = scheme.ToString();
            // 游戏中首次应用固定策略，原始值来自进入游戏前的备份。
            var original = !_temporary && TemporaryService().LoadBackups().Schemes.TryGetValue(key, out var temporaryOriginal)
                ? temporaryOriginal : before;
            addedBackup = backups.Schemes.TryAdd(key, original);
            backups.LastAppliedScheme = key;
            SaveBackups(backups); // 原子保存，每个方案各自保留原值。
            writesStarted = true;
            var values = preset == PowerPreset.Performance ? new[] { 100, 0, 100 } : new[] { 10, 100, 80 };
            var ok = true;
            for (var i = 0; i < SettingGuids.Length; i++)
                foreach (var dc in new[] { false, true })
                    ok &= _write(SettingGuids[i], values[i], dc, scheme);
            ok &= ActivateIfCurrent(scheme);
            if (ok)
            {
                // 只有明确且成功应用固定策略才移交所有权；失败则保留临时恢复依据。
                if (!_temporary) File.Delete(TemporaryService().BackupPath);
                return true;
            }
            var rolledBack = WriteBackup(before);
            if (rolledBack) RestoreBackupIndex();
            error = rolledBack ? "预设未能完整应用，已撤销本次改动" : "预设应用和回滚未完整成功，请使用已保留的备份还原";
            return false;
        }
        catch (Exception ex)
        {
            var restored = false;
            if (writesStarted && before is not null)
                try { restored = WriteBackup(before); if (restored) RestoreBackupIndex(); } catch { }
            error = "电源策略未完成：" + ex.Message
                + (writesStarted ? restored ? "；已撤销本次改动" : "；请使用已保留的备份还原" : "；未写入系统参数");
            return false;
        }
    }

    public bool Restore(out string error)
    {
        if (!_temporary && TemporaryService().HasBackup) return RestoreTemporary(out error);
        error = "";
        try
        {
            var backups = LoadBackups();
            if (backups.LastAppliedScheme is not { } key || !backups.Schemes.TryGetValue(key, out var backup))
                throw new InvalidOperationException("尚无可还原的电源备份");
            Validate(backup);
            if (!WriteBackup(backup))
                throw new IOException("部分参数未能还原，备份已保留");
            backups.Schemes.Remove(key);
            backups.LastAppliedScheme = backups.Schemes.OrderByDescending(p => p.Value.SavedAt).Select(p => p.Key).FirstOrDefault();
            SaveBackups(backups);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private bool ActivateIfCurrent(Guid scheme) => _currentScheme() != scheme || _activate(scheme);

    private bool WriteBackup(PowerBackup backup)
    {
        var scheme = Guid.Parse(backup.Scheme);
        var ok = true;
        foreach (var setting in SettingGuids)
        {
            ok &= _write(setting, backup.Ac[setting], false, scheme);
            ok &= _write(setting, backup.Dc[setting], true, scheme);
        }
        return ActivateIfCurrent(scheme) && ok;
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

    private static bool WriteWithUnhide(string setting, int value, bool dc, Guid scheme)
        => TryWrite(setting, value, dc, scheme) || (TryUnhide(setting) && TryWrite(setting, value, dc, scheme));

    private static bool TryUnhide(string settingGuid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{SubProcessorGroup}\{settingGuid}", writable: true);
            if (key is null) return false;
            key.SetValue("Attributes", 0, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    // ===== 按方案保存备份；兼容原先单方案文件 =====
    private PowerBackup Capture(Guid scheme)
    {
        var backup = new PowerBackup { Scheme = scheme.ToString(), SavedAt = DateTime.Now };
        foreach (var setting in SettingGuids)
        {
            backup.Ac[setting] = _read(setting, false, scheme) ?? throw new IOException("交流电源参数不可读");
            backup.Dc[setting] = _read(setting, true, scheme) ?? throw new IOException("电池电源参数不可读");
        }
        Validate(backup);
        return backup;
    }

    private static void Validate(PowerBackup backup)
    {
        if (!Guid.TryParse(backup.Scheme, out _)) throw new IOException("电源备份方案 GUID 无效");
        foreach (var setting in SettingGuids)
            if (!backup.Ac.TryGetValue(setting, out var ac) || ac is < 0 or > 100
                || !backup.Dc.TryGetValue(setting, out var dc) || dc is < 0 or > 100)
                throw new IOException("电源备份不完整，已拒绝写入");
    }

    private PowerBackupFile LoadBackups()
    {
        if (!File.Exists(BackupPath)) return new();
        var text = File.ReadAllText(BackupPath);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.TryGetProperty("Scheme", out _))
        {
            var legacy = JsonSerializer.Deserialize<PowerBackup>(text) ?? throw new IOException("电源备份为空");
            Validate(legacy);
            return new() { LastAppliedScheme = legacy.Scheme, Schemes = new() { [legacy.Scheme] = legacy } };
        }
        var file = JsonSerializer.Deserialize<PowerBackupFile>(text) ?? throw new IOException("电源备份为空");
        foreach (var pair in file.Schemes)
        {
            Validate(pair.Value);
            if (pair.Key != pair.Value.Scheme) throw new IOException("电源备份方案索引不一致");
        }
        if (file.Schemes.Count > 0 && (file.LastAppliedScheme is null || !file.Schemes.ContainsKey(file.LastAppliedScheme)))
            throw new IOException("电源备份缺少还原目标");
        return file;
    }

    private void SaveBackups(PowerBackupFile file)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = BackupPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temporary, BackupPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class PowerBackupFile
    {
        public string? LastAppliedScheme { get; set; }
        public Dictionary<string, PowerBackup> Schemes { get; set; } = [];
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

        /// <summary>备份对应的方案 GUID；只写回此方案，不污染当前其他方案。</summary>
        public string Scheme { get; set; } = string.Empty;

        public Dictionary<string, int> Ac { get; set; } = [];

        public Dictionary<string, int> Dc { get; set; } = [];
    }
}
