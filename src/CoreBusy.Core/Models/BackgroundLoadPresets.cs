namespace CoreBusy.Core.Models;

/// <summary>
/// 一个「后台负载」类别（v1.13.0）。
/// </summary>
/// <param name="Key">稳定标识，界面勾选与 <see cref="BackgroundLoadPresets.BuildRules"/> 之间按它传递。</param>
/// <param name="Title">类别名（界面显示）。</param>
/// <param name="Hint">类别说明（界面上作为悬停提示，同时解释"降级它有什么代价"）。</param>
/// <param name="ProcessNames">该类别包含的进程名（含 <c>.exe</c>）。</param>
public sealed record BackgroundLoadCategory(
    string Key,
    string Title,
    string Hint,
    IReadOnlyList<string> ProcessNames);

/// <summary>
/// 「后台负载降级」预设（v1.13.0）。
/// <para>
/// <b>为什么需要它</b>：Windows 是抢占式 + 动态优先级调度。前台应用在全屏、
/// 系统里没有别的重负载时，把它提到「高于标准」几乎不产生帧率变化 —— 没有竞争者时
/// 优先级本身没有意义。本页真正有效的用法是<b>反过来</b>：把后台重负载压到
/// 「低于标准」并降低磁盘 I/O 优先级。所以这里预置的是"压制谁"，不是"提拔谁"。
/// </para>
/// <para>
/// <b>为什么按类别勾选而不是全量应用</b>：这是开发机，Docker 与编译器被降级会拖慢
/// 日常构建。全量应用等于把一次"提升前台"变成一次"削弱后台"，边界必须交给用户划。
/// 每一条生成的规则都可以在列表里单独删改，预设只是省掉手工敲几十条规则的功夫。
/// </para>
/// <para>
/// <b>进程名写错不会出错</b>：规则按进程名匹配，本机不存在的进程永远匹配不到，
/// 只是扫描时多一次字典查询。代价可控，故不为了"精准"去逐个探测安装状态。
/// </para>
/// </summary>
public static class BackgroundLoadPresets
{
    /// <summary>
    /// 预设统一使用的优先级档位：低于标准。
    /// <para>
    /// 刻意不用「低」（Idle）：那会让被降级的进程在系统繁忙时几乎完全让路，
    /// 对一个正在跑构建或同步的任务来说等于暂停；「低于标准」既能明显让出 CPU，
    /// 又不会让后台任务饿死 —— 这是"压制"与"掐死"的分界。
    /// </para>
    /// </summary>
    public const ProcessPriorityLevel Priority = ProcessPriorityLevel.BelowNormal;

    /// <summary>预设类别（界面顺序即此顺序，最相关的前两类放最前）。</summary>
    public static IReadOnlyList<BackgroundLoadCategory> Categories { get; } =
    [
        new("container", "容器与虚拟机", "Docker / WSL / 虚拟机常驻高占用；降级会拖慢镜像构建与容器内编译。",
        [
            "docker.exe", "dockerd.exe", "com.docker.backend.exe", "com.docker.build.exe",
            "vmmem.exe", "vmmemWSL.exe", "wsl.exe", "wslhost.exe", "wslservice.exe",
            "vmware-vmx.exe", "VirtualBoxVM.exe", "VBoxHeadless.exe",
        ]),

        new("build", "构建与编译", "编译器 / 包管理器 / 脚本运行时；降级会拖慢构建，但能让前台保持流畅。",
        [
            "msbuild.exe", "devenv.exe", "cl.exe", "link.exe", "csc.exe", "vbcscompiler.exe",
            "dotnet.exe", "node.exe", "npm.exe", "java.exe", "javaw.exe",
            "python.exe", "pythonw.exe", "cargo.exe", "rustc.exe",
            "cmake.exe", "ninja.exe", "git.exe",
        ]),

        new("capture", "录屏与推流", "OBS / 录屏 / 会议客户端；它们不该抢走游戏的时间片。",
        [
            "obs64.exe", "obs32.exe", "obs.exe", "bdcam.exe",
            "GameBar.exe", "XboxGameOverlay.exe", "Zoom.exe",
        ]),

        new("sync", "云同步与备份", "网盘与同步客户端常在后台持续读写磁盘，最容易造成前台卡顿。",
        [
            "OneDrive.exe", "Dropbox.exe", "GoogleDriveFS.exe",
            "BaiduNetdisk.exe", "AliyunNetdisk.exe",
        ]),

        new("browser", "浏览器", "多标签页浏览器常驻后台吃 CPU 与内存。",
        [
            "chrome.exe", "msedge.exe", "firefox.exe", "msedgewebview2.exe",
            "Opera.exe", "brave.exe", "360se.exe", "QQBrowser.exe",
        ]),

        new("comm", "通讯与办公", "聊天与协作客户端（后台轮询 + 消息渲染）。",
        [
            "WeChat.exe", "Weixin.exe", "QQ.exe", "TIM.exe", "DingTalk.exe",
            "WXWork.exe", "ms-teams.exe", "Teams.exe", "Feishu.exe", "Lark.exe",
        ]),

        new("archive", "压缩与解压", "压缩包任务是典型的「短时高占用」，最适合被临时压制。",
        [
            "WinRAR.exe", "7zG.exe", "7z.exe", "Bandizip.exe", "HaoZip.exe", "360zip.exe",
        ]),
    ];

    /// <summary>
    /// 按类别 key 生成规则。同一条进程名跨类别只生成一次；
    /// 落入系统关键进程名单的一律丢弃（否则会生成永远失败的规则）。
    /// </summary>
    public static IReadOnlyList<ProcessOptimizationRule> BuildRules(IEnumerable<string> keys)
    {
        var wanted = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<ProcessOptimizationRule>();

        foreach (var category in Categories)
        {
            if (!wanted.Contains(category.Key))
                continue;

            foreach (var name in category.ProcessNames)
            {
                if (!emitted.Add(name) || SystemCriticalProcesses.IsProtected(name))
                    continue;

                rules.Add(new ProcessOptimizationRule
                {
                    ProcessName = name,
                    Enabled = true,
                    Affinity = CoreAffinityMode.None,
                    Priority = Priority,
                    KernelEnforced = false,
                    LowerIoPriority = true,
                });
            }
        }

        return rules;
    }
}
