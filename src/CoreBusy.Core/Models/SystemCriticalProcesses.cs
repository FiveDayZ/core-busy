namespace CoreBusy.Core.Models;

/// <summary>
/// 系统关键进程名单（v1.13.0 从 Windows 实现层上移到 Core）。
/// <para>
/// 上移的理由：名单原来只被 <c>WindowsCpuOptimizationService</c> 用，因此是 private；
/// 但 v1.13.0 的「后台负载降级」预设也要按同一份名单过滤自己生成的规则
/// （否则会给 msmpeng / searchindexer 这类进程生成永远失败的规则，日志被刷屏）。
/// 两处各存一份必然漂移，故收敛到这里。
/// </para>
/// <para>
/// 名单的语义是<b>硬拒绝</b>：把 csrss / winlogon / lsass 限制到单核后，
/// 轻则登录界面卡死，重则整机无法操作且只能强制断电。而用户往往是在
/// "给浏览器瘦身"的语境下顺手加规则，根本不会预期系统进程也在候选列表里。
/// </para>
/// </summary>
public static class SystemCriticalProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "secure system",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "audiodg", "dwm", "fontdrvhost", "sihost", "taskhostw",
        "ctfmon", "wudfhost", "spoolsv", "searchindexer", "nissrv",
        "msmpeng", "securityhealthservice", "wscsvc", "wmiprvse", "dllhost",
        "runtimebroker", "applicationframehost", "startmenuexperiencehost",
        "shellexperiencehost", "textinputhost", "searchhost", "corebusy.app",
    };

    /// <summary>该进程名是否属于系统关键进程（自动剥掉 <c>.exe</c>，大小写不敏感）。</summary>
    public static bool IsProtected(string processName) => Names.Contains(Normalize(processName));

    /// <summary>
    /// 剥掉 <c>.exe</c> 后缀。规则里存的是 <c>chrome.exe</c>，而
    /// <c>Process.ProcessName</c> 给的是 <c>chrome</c> —— 两者必须能互相比较，
    /// 否则同一个进程在"列表枚举"与"规则匹配"两条路径上会被判成不同的东西。
    /// </summary>
    public static string Normalize(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
}
