namespace CoreBusy.App.ViewModels.Dashboard;

using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Progression;

/// <summary>
/// 单个物理核的等级状态（v1.22.0）：累积 EXP → 等级 / 进度 / 已解锁内容。
/// </summary>
/// <remarks>
/// <para>
/// <b>与健康度严格分开。</b>健康度角标显示的是 0–100 的<b>硬件状态</b>，
/// 等级显示的是<b>陪你跑了多久</b>。两者语义完全无关，混在一起会让人以为
/// 「等级低 = 机器坏了」。因此等级不进角标，走 Tile 底部一行与悬浮说明。
/// </para>
/// <para>
/// <b>显示口径。</b>
/// <list type="bullet">
///   <item>EXP 为 0 → 只显示「Lv 1」，不给进度条（0 是不含信息量的读数）。</item>
///   <item>达标记得满级的核 → 显示「满级」，不再画进度条。</item>
///   <item>否则显示「Lv N·进度%」+ 距下一级的缺口（核·时）。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class CoreLevelVm : ObservableObject
{
    private int _level = 1;
    private double _progress;
    private double _expCoreMinutes;
    private bool _isMaxLevel;

    /// <summary>
    /// 下一个待解锁的内容。**必须在构造时就给一个值**（Lv 2 那一条），
    /// 否则新建的 VM 会停在「无待解锁项」，直到第一次 <see cref="Refresh"/> 才对 ——
    /// 而首帧之前 Tile 上会挂着 inconsistent 的状态。
    /// </summary>
    private CoreUnlock? _nextUnlock = CoreProgressionRules.NextUnlock(1);

    /// <summary>等级文本（如 "Lv 7"；满级为 "满级"）。</summary>
    public string LevelText => _isMaxLevel ? "满级" : $"Lv {_level}";

    /// <summary>
    /// 成长阶段（v1.25.0 养成游戏形态）：等级映射为进化阶段，
    /// 让"练到多少级"有一个一眼可读的形象感。满级独占「传说」。
    /// </summary>
    public string StageText => _isMaxLevel
        ? "传说"
        : _level switch
        {
            < 10 => "幼体",
            < 30 => "成长期",
            < 60 => "成熟期",
            _ => "究极期",
        };

    /// <summary>本级进度（0-100，用于进度条）。</summary>
    public double ProgressPercent => Math.Clamp(_progress * 100.0, 0.0, 100.0);

    /// <summary>是否已满级（满级后不再画进度条，改为显示累计经验）。</summary>
    public bool IsMaxLevel
    {
        get => _isMaxLevel;
        private set => SetProperty(ref _isMaxLevel, value);
    }

    /// <summary>累计 EXP（核·分）。</summary>
    public double ExpCoreMinutes
    {
        get => _expCoreMinutes;
        private set => SetProperty(ref _expCoreMinutes, value);
    }

    /// <summary>距下一级的 EXP 缺口（核·分）；满级为 0。</summary>
    public double ExpToNext
    {
        get => CoreLevelCurve.ExpToNextLevel(_expCoreMinutes);
    }

    /// <summary>等级（1-99）。</summary>
    public int Level
    {
        get => _level;
        private set
        {
            if (SetProperty(ref _level, value))
                OnPropertyChanged(nameof(LevelText));
        }
    }

    /// <summary>本级进度比例 [0,1]。</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
                OnPropertyChanged(nameof(ProgressPercent));
        }
    }

    /// <summary>下一个待解锁的内容（满级为 null）。</summary>
    public CoreUnlock? NextUnlock
    {
        get => _nextUnlock;
        private set
        {
            if (SetProperty(ref _nextUnlock, value))
            {
                OnPropertyChanged(nameof(HasNextUnlock));
                OnPropertyChanged(nameof(NextUnlockText));
            }
        }
    }

    /// <summary>是否有待解锁内容。</summary>
    public bool HasNextUnlock => _nextUnlock is not null;

    /// <summary>下一个待解锁内容的文本（无则空串）。</summary>
    public string NextUnlockText => _nextUnlock is null ? string.Empty : $"{_nextUnlock.Title} · Lv {_nextUnlock.Level}";

    /// <summary>
    /// 一次刷新所需的全部派生文本。集中在一个方法里算完再逐个通知，
    /// 避免每帧多次 <see cref="INotifyPropertyChanged"/> 引发 Tile 反复重绘。
    /// </summary>
    public void Refresh(double expCoreMinutes, bool floorDominated)
    {
        var level = CoreLevelCurve.LevelOf(expCoreMinutes);
        var progress = CoreLevelCurve.LevelProgress(expCoreMinutes);
        var max = level >= CoreLevelCurve.MaxLevel;
        var next = CoreProgressionRules.NextUnlock(level);

        var expChanged = Math.Abs(expCoreMinutes - _expCoreMinutes) > 1e-9;
        _expCoreMinutes = expCoreMinutes;

        // 逐项比对再通知：EXP 每帧都在涨，若无条件发 PropertyChanged，
        // 8 张 Tile 每帧各触发 6 次重绘 —— 而界面上的文本大多数帧并不真的变了。
        var levelChanged = level != _level;
        var progressChanged = Math.Abs(progress - _progress) > 1e-9;
        var maxChanged = max != _isMaxLevel;
        var nextChanged = !ReferenceEquals(next, _nextUnlock);

        // 走 setter 而不是直赋后备字段：setter 内部按值比较并派发 PropertyChanged，
        // 直赋会跳过通知，界面就停在初始值上且**没有任何报错**（只有截图能发现）。
        Level = level;
        Progress = progress;
        IsMaxLevel = max;
        NextUnlock = next;

        if (levelChanged)
            OnPropertyChanged(nameof(Level));
        if (levelChanged || maxChanged)
        {
            OnPropertyChanged(nameof(LevelText));
            OnPropertyChanged(nameof(StageText));
        }
        if (progressChanged)
            OnPropertyChanged(nameof(Progress));
        if (progressChanged)
            OnPropertyChanged(nameof(ProgressPercent));
        if (maxChanged)
            OnPropertyChanged(nameof(IsMaxLevel));
        if (nextChanged)
        {
            OnPropertyChanged(nameof(NextUnlock));
            OnPropertyChanged(nameof(HasNextUnlock));
            OnPropertyChanged(nameof(NextUnlockText));
        }
        if (expChanged || levelChanged || maxChanged)
        {
            OnPropertyChanged(nameof(ExpCoreMinutes));
            OnPropertyChanged(nameof(ExpToNext));
        }

        OnPropertyChanged(nameof(TipText));
        _floorDominated = floorDominated;
    }

    private bool _floorDominated;

    private string _etaText = "正在积累经验…";

    /// <summary>
    /// "距升级还要多久"（v1.27.0）：由本次运行的 EXP 均值速率外推。
    /// <para>
    /// 这是弹卡里替代文字墙的直观信息 —— 用户不需要理解"核·分"口径，
    /// 只需要知道"在涨、涨多快、还差多久"。满级 / 速率未知 / 速率极低各有直白文案。
    /// </para>
    /// </summary>
    public string EtaText { get => _etaText; private set => SetProperty(ref _etaText, value); }

    /// <summary>
    /// 依据当前 EXP 速率刷新预估文案（每 tick 调用；文本不变时 setter 不发通知）。
    /// </summary>
    /// <param name="rateCoreMinutesPerSecond">本次运行的 EXP 均值速率（核·分/秒）；观测不足时上层传 0。</param>
    public void UpdateEta(double rateCoreMinutesPerSecond)
    {
        string text;
        if (_isMaxLevel)
            text = "已达满级";
        else if (!double.IsFinite(rateCoreMinutesPerSecond) || rateCoreMinutesPerSecond <= 0)
            text = "正在积累经验…";
        else
        {
            var etaSeconds = ExpToNext / rateCoreMinutesPerSecond;
            text = etaSeconds switch
            {
                < 90 => "即将升级",
                < 3600 => $"距升级约 {etaSeconds / 60:0} 分钟",
                < 3600 * 48 => $"距升级约 {etaSeconds / 3600.0:0.#} 小时",
                _ => "按当前速率还很遥远",
            };
        }

        EtaText = text;
    }

    /// <summary>
    /// 悬浮说明：完整口径声明 + 已解锁清单 + 缺口。
    /// </summary>
    /// <remarks>
    /// 刻意把「进度轴**不是**健康度」这句话写进说明里 ——
    /// 这是最容易被误解的地方（用户会自然把等级当机器好坏的指标）。
    /// </remarks>
    public string TipText => BuildTip();

    private string BuildTip()
    {
        var lines = new List<string>(8)
        {
            $"{LevelText} · 累计 {CoreLevelCurve.ToCoreHours(_expCoreMinutes):0.0} 核·时",
        };

        if (_isMaxLevel)
        {
            lines.Add("已满级。经验仍在累积，可继续累积导出纪念。");
        }
        else
        {
            var toNext = ExpToNext;
            if (toNext > 0)
            {
                lines.Add(
                    $"距 Lv {Level + 1} 还差 {CoreLevelCurve.ToCoreHours(toNext):0.0} 核·时" +
                    $"（{toNext:0} 核·分）");
            }
        }

        lines.Add(string.Empty);
        lines.Add("经验口径：满负荷核·时（实测占用率的时间积分）");
        lines.Add($"· 占用 ≥ {CoreExpCaliber.MinLoadPct:0}% 按负荷计");
        lines.Add($"· 占用 {CoreExpCaliber.PresentFloorPct:0}%~{CoreExpCaliber.MinLoadPct:0}% 由陪伴兜底（×{CoreExpCaliber.FloorFactor:0.##}）");
        lines.Add($"· 占用 < {CoreExpCaliber.PresentFloorPct:0}% 完全摸鱼，不给经验");

        if (_floorDominated)
        {
            lines.Add(string.Empty);
            lines.Add(CoreProgressionRules.FloorDominanceHint(_expCoreMinutes));
            lines.Add("（该核长期轻载但持续在列，非异常）");
        }

        var unlocked = CoreProgressionRules.Unlocked(Level);
        if (unlocked.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"已解锁 {unlocked.Count} 项（Lv2 起逐级解锁历史记录能力）：");
            foreach (var item in unlocked)
                lines.Add($"· Lv{item.Level} {item.Title}");
        }

        if (_nextUnlock is not null)
        {
            lines.Add(string.Empty);
            lines.Add($"下一项：Lv {_nextUnlock.Level} 解锁「{_nextUnlock.Title}」");
            lines.Add(_nextUnlock.Description);
        }

        lines.Add(string.Empty);
        lines.Add("等级只表示「这颗核陪你跑了多久」，不表示硬件好坏 ——");
        lines.Add("硬件状态看健康度角标（那是合成代理指标，不是损耗百分比）。");

        return string.Join('\n', lines);
    }
}