namespace CoreBusy.App.ViewModels.Dashboard;

using CoreBusy.App.Infrastructure;

/// <summary>
/// 弹卡「戳一戳」小核灵（v1.28.0）：按成长阶段换形象、按当前状态说台词。
/// </summary>
/// <remarks>
/// <para>
/// <b>会话级趣味，刻意不持久化。</b>戳击次数与台词都是当次会话的即兴演出；
/// 若进 EXP 账本，就把一个玩笑变成了一条要维护口径、要做往返取证的落盘数据 ——
/// 趣味性不值得这个复杂度。
/// </para>
/// <para>
/// 形象阈值与 <see cref="CoreLevelVm.StageText"/> 逐档对应（v1.29.0 机器进化线，
/// 用户反馈小动物系不合语义）：
/// 🔩 螺丝钉(幼体) / ⚙️ 齿轮(成长期) / 🤖 机器人(成熟期) / 🦾 强化机体(究极期) / 🛸 终极机体(传说)。
/// </para>
/// </remarks>
public sealed class CorePokeVm : ObservableObject
{
    private string _face = FaceFor(1, isMaxLevel: false);
    private string _quip = "……";
    private string _hintText = "👆 戳戳它";
    private int _pokeCount;

    /// <summary>当前形象（随等级阶段进化）。</summary>
    public string Face
    {
        get => _face;
        private set => SetProperty(ref _face, value);
    }

    /// <summary>气泡台词。</summary>
    public string Quip
    {
        get => _quip;
        private set => SetProperty(ref _quip, value);
    }

    /// <summary>首戳提示（戳过后消失；空串复用同一 TextBlock 槽位，不加可见性绑定）。</summary>
    public string HintText
    {
        get => _hintText;
        private set => SetProperty(ref _hintText, value);
    }

    /// <summary>本会话累计被戳次数（每颗核独立；弹卡开关不清零，重启归零）。</summary>
    public int PokeCount => _pokeCount;

    /// <summary>等级变化时同步形象（弹卡开着时升级也能当场进化）。</summary>
    public void OnLevelChanged(int level, bool isMaxLevel)
        => Face = FaceFor(level, isMaxLevel);

    /// <summary>弹卡每次打开都换一句招呼（不计入戳击）。</summary>
    public void Greet() => Quip = CorePokeQuips.Greet();

    /// <summary>被戳了一下：连击计数 +1，换一句状态台词，收起首戳提示。</summary>
    public void Poke(
        string statusLabel, double usage, string freqText,
        int level, bool isMaxLevel, double levelProgress)
    {
        _pokeCount++;
        OnPropertyChanged(nameof(PokeCount));
        Quip = CorePokeQuips.Pick(
            statusLabel, usage, freqText, level, isMaxLevel, levelProgress, _pokeCount);
        HintText = string.Empty;
    }

    /// <summary>等级 → 形象（v1.29.0 机器进化线，用户反馈小动物系不合语义）。
    /// 阈值与 CoreLevelVm.StageText 逐档对应（&lt;10 幼体 / &lt;30 成长期 / &lt;60 成熟期 / 其余究极期）：
    /// 🔩 螺丝钉 → ⚙️ 齿轮 → 🤖 机器人 → 🦾 强化机体 → 🛸 终极机体。</summary>
    internal static string FaceFor(int level, bool isMaxLevel) => isMaxLevel
        ? "🛸"
        : level switch
        {
            < 10 => "🔩",
            < 30 => "⚙️",
            < 60 => "🤖",
            _ => "🦾",
        };
}
