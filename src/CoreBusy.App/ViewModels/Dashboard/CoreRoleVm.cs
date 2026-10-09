namespace CoreBusy.App.ViewModels.Dashboard;

using System.Windows.Media;
using CoreBusy.App.Infrastructure;
using CoreBusy.App.Themes;
using CoreBusy.Core.Progression;

/// <summary>
/// 单个物理核的**行为角色**状态（v1.22.0）：由长期统计派生，跨天相对稳定。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="CoreLevelVm"/> 的分工：等级回答「陪你跑了多久」（单调递增），
/// 角色回答「它是怎么跑的」（形态描述）。两者互不替代 ——
/// 一颗核可以是「Lv.48 的苦劳」，也可以是「Lv.48 的尖峰侠」。
/// </para>
/// <para>
/// <b>角色不代表硬件好坏。</b>摸鱼王不是坏核，尖峰侠也不是不稳定 ——
/// 它只描述这颗核在<b>本机当前使用习惯</b>下的行为。这一点写进悬浮说明，
/// 因为「尖峰」这个词很容易被读成硬件问题。
/// </para>
/// </remarks>
public sealed class CoreRoleVm : ObservableObject
{
    private CoreRole _role = CoreRole.Unknown;
    private CoreRoleStats? _stats;

    /// <summary>角色标签（摸鱼 / 闲时热手 / 稳态 / 苦劳 / 尖峰 / 夜猫；证据不足为 "-"）。</summary>
    public string Label => CoreRoleClassifier.Label(_role);

    /// <summary>是否有可判定的角色（证据不足时为 false，界面用 <see cref="Label"/> 的 "-"）。</summary>
    public bool HasRole => _role != CoreRole.Unknown;

    /// <summary>角色对应的强调色（用于 Tile 上的小字）。</summary>
    public Brush AccentBrush => _role switch
    {
        CoreRole.Slacker => Frozen("#888780"),
        CoreRole.Burster => Frozen("#BA7517"),
        CoreRole.Steady => Frozen("#378ADD"),
        CoreRole.Workhorse => Frozen("#E24B4A"),
        CoreRole.Spiker => Frozen("#A32D2D"),
        CoreRole.Nocturnal => Frozen("#7F77DD"),
        _ => UiTheme.TileBorderBrush,
    };

    /// <summary>
    /// 悬浮说明：角色定义 + 实测依据 + "不代表硬件好坏"的声明。
    /// </summary>
    /// <remarks>
    /// 末尾那句声明是刻意加的：角色名里的「苦劳」「尖峰」很容易被读成硬件评价，
    /// 而它其实只描述使用习惯。不写清楚，用户会以为工具在暗示他的 CPU 有问题。
    /// </remarks>
    public string TipText
    {
        get
        {
            var lines = new List<string>(8)
            {
                CoreRoleClassifier.Describe(_role, _stats),
                string.Empty,
                "角色由长期统计派生（不是瞬时状态）——",
                "· 平均占用决定「忙闲」：< 8% 摸鱼 · ≥ 60% 苦劳 · 5%~15% 由陪伴兜底",
                "· 波动系数（变异系数）决定「稳不稳」：≤ 0.35 算稳",
                "· 峰值 ÷ 均值 ≥ 5 且峰值 ≥ 90% 判尖峰",
                "· 夜间（20:00–08:00）负荷占比 ≥ 62% 判夜猫",
                string.Empty,
                "角色只描述这颗核在当前使用习惯下的行为，不代表硬件好坏。",
                "硬件状态看健康度角标。",
            };
            return string.Join('\n', lines);
        }
    }

    /// <summary>
    /// 一次刷新。逐项比对再通知，避免每帧触发 Tile 重绘。
    /// </summary>
    public void Refresh(CoreRole role, CoreRoleStats? stats)
    {
        var changed = role != _role;
        _role = role;
        _stats = stats;

        if (changed)
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(HasRole));
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(TipText));
        }
    }

    private static Brush Frozen(string hex) => new SolidColorBrush(
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);

    /// <summary>供 VM 层断言用的当前角色。</summary>
    public CoreRole Role => _role;
}