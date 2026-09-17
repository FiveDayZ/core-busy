using CoreBusy.Core.Energy;

namespace CoreBusy.Core.Models;

/// <summary>
/// 主题模式（规范 §7）：默认"自动"按 CPU 厂商切换；也可手动锁定 Intel Blue / AMD Red。
/// </summary>
public enum ThemeMode
{
    /// <summary>自动：按 CPU 厂商（Intel / AMD / 未知→Neutral）。</summary>
    Auto = 0,

    /// <summary>强制 Intel Blue 主题。</summary>
    Intel = 1,

    /// <summary>强制 AMD Red 主题。</summary>
    Amd = 2,
}

/// <summary>
/// 核心分区口径（规范 §20/§32/§33 与 v1.5.0 设计稿两种口径二选一）。
/// </summary>
public enum CoreLayoutMode
{
    /// <summary>
    /// 规范口径（默认）：Intel 混合架构 → P-Core / E-Core；
    /// Intel 非混合与 AMD → 单段 CPU Core（多 CCD Ryzen → CCD 0..N）。
    /// AMD 严禁显示 P-Core / E-Core。
    /// </summary>
    Spec = 0,

    /// <summary>
    /// 设计稿口径（v1.5.0 一比一还原）：按逻辑处理器逐线程成 Tile，
    /// 前后对半切成 P-Core 段与 E-Core 段（AMD 亦如此）。
    /// </summary>
    Draft = 1,
}

/// <summary>
/// 应用设置（Phase 5）。持久化到 %APPDATA%\CORE-BUSY\settings.json，
/// 采样周期对应方案 7.2 节可调整档位（500/1000/2000ms）。
/// </summary>
public sealed class AppSettings
{
    /// <summary>采样周期（毫秒），合法值 500/1000/2000。</summary>
    public int IntervalMilliseconds { get; set; } = 1000;

    /// <summary>启用硬件传感器（温度/功耗/频率）。关闭可降低内存与 CPU 占用。</summary>
    public bool SensorsEnabled { get; set; } = true;

    /// <summary>启用游戏模式（全屏/无边框全屏检测）。</summary>
    public bool GameDetectionEnabled { get; set; } = true;

    /// <summary>主题模式（自动 / Intel Blue / AMD Red，规范 §7）。默认自动。</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.Auto;

    /// <summary>核心分区口径（规范 / 设计稿）。默认规范。</summary>
    public CoreLayoutMode CoreLayout { get; set; } = CoreLayoutMode.Spec;

    /// <summary>点击关闭（标题栏 X / Alt+F4）时最小化到托盘而不是退出。默认启用；托盘图标右键可退出。</summary>
    public bool CloseToTray { get; set; } = true;

    // ── 整机功耗估算的逐部件可校准系数（v1.20.1）────────────────────────
    //    为什么从 v1.20.0 的「一个斜率 k + 一个底数 b」改成分项：那一版整机口径是
    //    「整机 ≈ k × CPU封装功耗 + b」，只有一个输入，于是"CPU 之外的一切"全被压进 k 与 b；
    //    它在带独显的机器上会整体漏掉独显那一两百瓦（独显功耗不随 CPU 封装功耗变化），
    //    且用户即使发现偏差也无法判断该修哪一项。现在改为逐部件（显卡 / 内存 / 硬盘 / 风扇 /
    //    主板），每一项都有自己的系数，见 SystemPowerEstimator。
    //    全部字段的越界处理与全线约定一致：**回落默认值，而不是截断**——截断会把"填错了"
    //    伪装成"填了个极端值"。旧版 settings.json 里的 SystemPowerFactor /
    //    SystemPowerBaseWatts 反序列化时被忽略（不报错），需按下面五项重新校准。

    /// <summary>整机总量校准乘数（默认 1.0，合法 0.3–3.0）。有功率计的用户只调这一个。</summary>
    public double SystemPowerCalibration { get; set; } = SystemPowerEstimator.DefaultCalibration;

    /// <summary>主板/芯片组/网卡/USB 供电等固定开销（W，默认 10，合法 0–100）。</summary>
    public double SystemPowerBoardWatts { get; set; } = SystemPowerEstimator.DefaultBoardWatts;

    /// <summary>每个转动风扇的功耗（W，默认 2，合法 0–20）。</summary>
    public double SystemPowerFanWatts { get; set; } = SystemPowerEstimator.DefaultFanWatts;

    /// <summary>独显无功率传感器且型号不在表内时的整卡额定功率兜底（W，默认 150，合法 0–600）。</summary>
    public double SystemPowerGpuFallbackWatts { get; set; } = SystemPowerEstimator.DefaultGpuFallbackWatts;

    /// <summary>内存每 GB 的满载功耗系数（W/GB，默认 0.392 = Cloud Carbon Footprint 口径，合法 0–2）。</summary>
    public double SystemPowerDramWattsPerGb { get; set; } = SystemPowerEstimator.DefaultDramWattsPerGb;

    /// <summary>把扁平字段组装成功耗模型的设置记录（唯一的转换点，避免各处各拼一次）。</summary>
    public SystemPowerSettings ToSystemPowerSettings() => new()
    {
        Calibration = SystemPowerCalibration,
        BoardWatts = SystemPowerBoardWatts,
        FanWatts = SystemPowerFanWatts,
        GpuFallbackWatts = SystemPowerGpuFallbackWatts,
        DramWattsPerGb = SystemPowerDramWattsPerGb,
    };

    /// <summary>
    /// CPU 核心优化设置（v1.12.0）：进程亲和性/优先级规则、游戏模式联动、电源策略。
    /// 单独成节而非平铺到顶层，避免与界面偏好混在一起 —— 这组设置会真实改动系统状态。
    /// 属性带默认值，旧版 settings.json 反序列化后自动得到空规则表，不会因缺字段报错。
    /// </summary>
    public OptimizationSettings Optimization { get; set; } = new();

    /// <summary>主题与核心分区需在窗口构建前生效，变更后需重启应用（UI 用于提示）。</summary>
    public bool RequiresRestartComparedTo(AppSettings other) =>
        Theme != other.Theme || CoreLayout != other.CoreLayout;
}
