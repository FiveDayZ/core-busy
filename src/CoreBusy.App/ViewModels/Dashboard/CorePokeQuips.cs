namespace CoreBusy.App.ViewModels.Dashboard;

/// <summary>
/// 戳一戳台词库（v1.28.0）：弹卡里小核灵的随机台词。
/// </summary>
/// <remarks>
/// <para>
/// <b>纯函数池。</b>输入（状态标签 / 占用率 / 频率文本 / 等级 / 本级进度 / 戳击次数），
/// 输出一句台词；不持有状态、不碰 UI 类型 —— 可被探针脚本反射验证每个分支都非空。
/// </para>
/// <para>
/// <b>状态键与 <see cref="CoreBusy.Core.CoreStatusClassifier"/> 的中文标签逐字对应。</b>
/// 累积视图下状态槽位显示的是名次（"#1 / 8"），查不到池子时自然落入默认池 —— 这是预期行为，
/// 不是缺陷：名次状态下说点通用的闲话刚好。
/// </para>
/// </remarks>
internal static class CorePokeQuips
{
    private static readonly Random Rng = Random.Shared;

    /// <summary>按状态标签分池的台词。键必须与 CoreStatusClassifier.ToLabel 的输出逐字一致。</summary>
    private static readonly Dictionary<string, string[]> Pool = new(StringComparer.Ordinal)
    {
        ["摸鱼"] =
        [
            "带薪休息，专业选手 ✌️",
            "zzz…别戳，我在攒经验",
            "摸鱼界的扛把子就是我",
            "这叫战略性低负载",
            "嘘——正在假装思考",
        ],
        ["空闲"] =
        [
            "正在待机，梦想是 0 瓦",
            "闲着也是一门手艺",
            "我的占用率比你的周末还空",
            "待命中…有活儿随时喊我",
            "闲到开始数时钟周期了",
        ],
        ["工作"] =
        [
            "在搬砖，别闹",
            "这单算完就歇",
            "单线程的忧伤你不懂",
            "正在勤勤恳恳搬指令",
            "专业打工核，戳了也白戳",
        ],
        ["忙碌"] =
        [
            "忙！没看到队列都排满了？",
            "别戳！缓存都是热的",
            "流水线全速运转中 🏭",
            "每个时钟周期都很贵",
            "忙到飞起，预约请排队",
        ],
        ["高负载"] =
        [
            "烫烫烫烫烫！",
            "风扇在为我尖叫",
            "再戳就要降频了！",
            "火力全开，勿扰模式",
        ],
        ["爆肝"] =
        [
            "爆肝中，散会再说",
            "这是燃烧的时刻",
            "功耗曲线都在为我爬山",
            "散热器：我尽力了",
        ],
    };

    /// <summary>状态查不到池子时的兜底（累积视图名次槽位 / "未知"）。</summary>
    private static readonly string[] DefaultPool =
    [
        "……我状态存疑，别为难我",
        "数据没跟上，让我缓缓",
        "今天也是认真运行的一天",
    ];

    /// <summary>满级核的专属台词（偶尔摆架子）。</summary>
    private static readonly string[] MaxLevelPool =
    [
        "传说在此，凡人退散",
        "满级之后，只余从容",
        "经验还在涨，但我已经不需要了",
    ];

    /// <summary>每次打开弹卡的招呼语（不计入戳击）。</summary>
    private static readonly string[] GreetPool =
    [
        "哟，又来看我啦",
        "来了？戳我一下试试",
        "今日份成长，正在营业",
        "看我，是不是又长了一点",
        "点我没奖，但很快乐",
    ];

    /// <summary>弹卡打开：随机一句招呼。</summary>
    public static string Greet() => GreetPool[Rng.Next(GreetPool.Length)];

    /// <summary>
    /// 被戳了一下：按优先级挑一句台词。
    /// 连击彩蛋 &gt; 满级摆架子 &gt; 高占用吃爆肝池 &gt; 临升级倒计时 &gt; 频率炫耀 &gt; 状态池。
    /// </summary>
    /// <param name="statusLabel">状态标签（累积视图下是名次文本，查不到池子走默认池）。</param>
    /// <param name="usage">当前占用率（0-100；累积视图为累积均值）。</param>
    /// <param name="freqText">频率文本；无效时为 "-"（沿用全线"不拿 0 冒充"约定）。</param>
    /// <param name="level">当前等级。</param>
    /// <param name="isMaxLevel">是否满级。</param>
    /// <param name="levelProgress">本级进度 [0,1]。</param>
    /// <param name="pokeCount">本会话累计被戳次数（1 起）。</param>
    public static string Pick(
        string statusLabel, double usage, string freqText,
        int level, bool isMaxLevel, double levelProgress, int pokeCount)
    {
        // 连击彩蛋：每逢 10 的倍数必给一句（确定性触发，文案二选一）。
        if (pokeCount >= 10 && pokeCount % 10 == 0)
        {
            return pokeCount % 20 == 0
                ? $"{pokeCount} 连击！这颗核已经彻底记住你了"
                : $"连戳 {pokeCount} 下！它已经记住你的手感了";
        }

        // 满级核偶尔摆架子。
        if (isMaxLevel && Rng.Next(3) == 0)
            return MaxLevelPool[Rng.Next(MaxLevelPool.Length)];

        // 高占用优先吃爆肝池 —— 标签与占用可能短暂不一致，以占用为准。
        if (usage >= 80 && Rng.Next(3) != 0)
            return Pool["爆肝"][Rng.Next(Pool["爆肝"].Length)];

        // 临升级倒计时。
        if (!isMaxLevel && levelProgress >= 0.9 && Rng.Next(3) == 0)
            return $"就差一点点就 Lv {level + 1} 了，紧张！";

        // 频率炫耀彩蛋：频率有效时偶发吹一句。
        if (!string.Equals(freqText, "-", StringComparison.Ordinal) && Rng.Next(5) == 0)
            return $"刚才冲到 {freqText}，帅吧";

        // 状态池（查不到走默认池）。
        var lines = Pool.TryGetValue(statusLabel, out var pool) ? pool : DefaultPool;
        return lines[Rng.Next(lines.Length)];
    }
}
