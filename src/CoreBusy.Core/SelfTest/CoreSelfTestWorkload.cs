namespace CoreBusy.Core.SelfTest;

using System.Diagnostics;

/// <summary>
/// 逐核确定性自检的**工作负载**（v1.18.0）。纯计算、无 IO、无平台依赖，
/// 线程绑定与提权由宿主（Windows 层）负责 —— 这样它可以在无硬件的探针里被直接验证。
/// <para>
/// ──────────────────────────── 为什么需要它 ────────────────────────────
/// </para>
/// <para>
/// 频率比值、电压、热裕度全都是**间接推断**：它们能告诉你"这颗核看起来不对劲"，
/// 但无法回答"它算错了没有"。真正能**确定性**判定"这颗核不稳"的只有一种办法 ——
/// 让它算一段结果已知的计算，再比对结果。这是 Prime95 / mprime 的 torture test 的
/// 全部方法论（其 <c>FATAL ERROR: Rounding was 0.5, expected less than 0.4</c>
/// 是硬件故障的直接证据，不是推断）。
/// </para>
/// <para>
/// <b>只借鉴方法，不引入其代码或二进制</b>：Prime95 的 EULA 明确非 FOSS（涉及 EFF 悬赏归属）。
/// 本实现的三条校验路径全部自行构造，见下。
/// </para>
/// <para>
/// ──────────────────────────── 三条独立校验 ────────────────────────────
/// </para>
/// <list type="number">
///   <item><b>整数精确性（交叉算法互证）</b>：同一次卷积用两条算法各算一遍 ——
///         NTT（数论变换，模 998244353）与 O(n²) 直卷积。两者在数学上必然逐点相等，
///         且全程只用整数运算，因此**任何一处 ALU / 访存 / 缓存错误都会让它们不一致**。
///         这条不需要外部"标准答案"，是自己证明自己，可靠性来自两条路径的独立性。</item>
///   <item><b>浮点比特稳定性</b>：对同一输入重复算同一个浮点卷积，要求每一次的输出
///         **比特级完全一致**。健康的 CPU 上同一条指令序列必然给出同一个结果；
///         只要出现一次不同的位模式，就说明这次计算与上一次不是同一回事（时序/供电/热异常导致
///         的位翻转或非确定性舍入）。比对的是位模式而不是数值容差，所以不存在"阈值定得松紧"
///         这种可争辩的空间。</item>
///   <item><b>浮点误差幅值</b>：把浮点结果与整数**精确**结果对比，测出最大相对误差。
///         它单独不作通过判据（不同平台的 FMA/指令选择会让它与首次运行不同），
///         但它的**趋势**是信息：同一台机器上误差突然变大，是硬件开始出问题的早期迹象。</item>
/// </list>
/// <para>
/// ──────────────────────────── 使用边界（必须遵守） ────────────────────────────
/// </para>
/// <list type="bullet">
///   <item><b>必须由用户显式触发</b>：它真的把核跑满，属状态变更。</item>
///   <item><b>结果不进评分</b>：只作事实记录。一次 2 秒的自检通过，不能证明一台机器"健康"。</item>
///   <item><b>可中断</b>：<see cref="Run"/> 接受 <see cref="CancellationToken"/>，
///         且只在每轮之间检查，不会把一次计算撕裂成不确定状态。</item>
/// </list>
/// </summary>
public static class CoreSelfTestWorkload
{
    /// <summary>NTT 用的素数模数（998244353 = 119×2^23+1，支持长度 2^23 的变换）。</summary>
    public const int NttModulus = 998244353;

    /// <summary>该模数的原根。</summary>
    private const int NttPrimitiveRoot = 3;

    /// <summary>默认卷积长度（线性卷积长度 2n-1，用 2n 的循环卷积承载）。</summary>
    public const int DefaultLength = 512;

    /// <summary>
    /// 单轮的工作量参数：每个测试向量由长度为 <see cref="DefaultLength"/> 的 LCG 序列给出，
    /// 取值上限刻意压到 1000 —— 这样长度为 n 的卷积结果上界是 n×1000×1000 = 512×10⁶ ≈ 5.1e8，
    /// **严格小于模数** 9.98e8，于是"模 p 的循环卷积"与"真实线性卷积"逐点相等，
    /// 两条整数路径的比对才是一个严格等式而不是一次近似。
    /// <para>
    /// 余量只有约 2 倍，因此**加大 <see cref="DefaultLength"/> 或放开取值上限时必须重算这条不等式**
    /// （例如 n 提到 1024 就会顶到 1.02e9 &gt; 模数，两条路径立刻系统性不一致，
    /// 自检会把健康的机器报成算错）。</para>
    /// </summary>
    private const int VectorValueBound = 1000;

    /// <summary>浮点误差的参考容差（相对最大幅值）。仅用于标注"是否超出常规"，不作为通过判据。</summary>
    public const double FpErrorTolerance = 1e-9;

    /// <summary>一轮计算的完整结果。</summary>
    /// <param name="Passes">完成的轮数（被取消时可能少于此前的计划）。</param>
    /// <param name="IntegerMismatches">整数两条路径不一致的轮数。**任何非 0 都是硬故障证据**。</param>
    /// <param name="FpBitInstabilities">浮点结果比特与首轮不同的轮数。**任何非 0 都是硬故障证据**。</param>
    /// <param name="FpMaxRelativeError">浮点结果相对整数精确结果的最大相对误差。</param>
    /// <param name="IntegerChecksum">整数路径的校验和（跨运行可比，用于识别"结论被整体篡改"）。</param>
    /// <param name="FpChecksumBits">浮点输出的位模式异或（跨运行可比）。</param>
    /// <param name="Elapsed">实际计算耗时。</param>
    public readonly record struct RoundResult(
        int Passes,
        int IntegerMismatches,
        int FpBitInstabilities,
        double FpMaxRelativeError,
        long IntegerChecksum,
        long FpChecksumBits,
        TimeSpan Elapsed);

    /// <summary>
    /// 跑一段**尽可能满**的确定性计算，时长由 <paramref name="duration"/> 控制。
    /// <para>
    /// 内部循环外层按时间收敛、内层按固定轮次推进，因此：① 时长可控（不靠猜轮数）；
    /// ② 每轮的结果都可以参与比特稳定性比对；③ 取消时最多丢掉一轮，不会留下半个状态。
    /// </para>
    /// </summary>
    /// <param name="duration">目标计算时长。建议 ≤ 3 秒 —— 它只是采样，不是压力测试。</param>
    /// <param name="cancellationToken">取消即可中断（下一次轮间检查时退出）。</param>
    public static RoundResult Run(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var n = DefaultLength;
        var size = 1;
        while (size < n * 2)
            size <<= 1;

        // 预分配：计算循环里**不允许有分配**，否则测到的会是 GC 抖动而不是算力。
        var a = BuildVector(n, seed: 0x5EED_0001);
        var b = BuildVector(n, seed: 0x5EED_0002);

        var exact = new long[n * 2];
        var ntt = new long[n * 2];
        var fp = new double[n * 2];

        var fa = new double[size];
        var fb = new double[size];
        PrepareFpInputs(a, b, fa, fb, size);

        // NTT 的临时缓冲：同样在循环外分配（循环内一次分配都不做）。
        var nttA = new int[size];
        var nttB = new int[size];

        var passes = 0;
        var integerMismatches = 0;
        var fpInstabilities = 0;
        var maxRelativeError = 0.0;
        var integerChecksum = 0L;
        var fpChecksumBits = 0L;
        long firstPassBits = 0;

        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < duration && !cancellationToken.IsCancellationRequested)
        {
            // 1) 整数精确路径：O(n²) 直卷积（long 累加，全整数，比特级可复现）。
            ConvolveDirect(a, b, exact, n);

            // 2) 整数快速路径：NTT。与上一条数学上必然逐点相等。
            ConvolveNtt(a, b, ntt, n, size, nttA, nttB);

            var mismatch = false;
            for (var i = 0; i < n * 2; i++)
            {
                if (exact[i] != ntt[i])
                {
                    mismatch = true;
                    break;
                }
            }

            if (mismatch)
                integerMismatches++;

            // 3) 浮点路径：与整数精确结果比对幅值，并记录位模式用于稳定性判定。
            var passBits = ConvolveFp(fa, fb, fp, size);
            if (passes == 0)
                firstPassBits = passBits;
            else if (passBits != firstPassBits)
                fpInstabilities++;

            var error = MaxRelativeError(fp, exact, n);
            if (error > maxRelativeError)
                maxRelativeError = error;

            // 校验和取整数路径 —— 它比特级确定，跨运行可比；浮点只存位模式异或。
            integerChecksum = 0;
            for (var i = 0; i < n * 2; i++)
                integerChecksum = unchecked((integerChecksum * 31) + exact[i]);

            fpChecksumBits = passBits;
            passes++;
        }

        clock.Stop();

        return new RoundResult(
            passes,
            integerMismatches,
            fpInstabilities,
            maxRelativeError,
            integerChecksum,
            fpChecksumBits,
            clock.Elapsed);
    }

    /// <summary>确定性测试向量（自实现的 LCG，跨平台一致 —— 不用 <c>Random</c>，它不保证跨版本可复现）。</summary>
    private static int[] BuildVector(int n, uint seed)
    {
        var result = new int[n];
        var state = seed;
        for (var i = 0; i < n; i++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            result[i] = (int)(state % VectorValueBound);
        }

        return result;
    }

    /// <summary>O(n²) 直卷积：整数精确参考实现（也是 NTT 与浮点两条路径的比对基准）。</summary>
    private static void ConvolveDirect(int[] a, int[] b, long[] output, int n)
    {
        Array.Clear(output, 0, output.Length);

        for (var i = 0; i < n; i++)
        {
            var ai = a[i];
            if (ai == 0)
                continue;

            for (var j = 0; j < n; j++)
                output[i + j] += (long)ai * b[j];
        }
    }

    /// <summary>
    /// NTT 循环卷积（模 <see cref="NttModulus"/>），结果落在 [0, 模数) 内。
    /// 临时缓冲由调用方提供，避免在计时循环里触发分配（那会把 GC 抖动混进判定）。
    /// </summary>
    private static void ConvolveNtt(int[] a, int[] b, long[] output, int n, int size, int[] scratchA, int[] scratchB)
    {
        Array.Clear(scratchA, 0, size);
        Array.Clear(scratchB, 0, size);
        for (var i = 0; i < n; i++)
        {
            scratchA[i] = a[i] % NttModulus;
            scratchB[i] = b[i] % NttModulus;
        }

        Transform(scratchA, invert: false);
        Transform(scratchB, invert: false);

        for (var i = 0; i < size; i++)
            scratchA[i] = (int)((long)scratchA[i] * scratchB[i] % NttModulus);

        Transform(scratchA, invert: true);

        Array.Clear(output, 0, output.Length);
        for (var i = 0; i < n * 2; i++)
            output[i] = scratchA[i];
    }

    /// <summary>原地 NTT / 逆变换（模 998244353）。</summary>
    private static void Transform(int[] values, bool invert)
    {
        var n = values.Length;

        // 位反转置换。
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
                (values[i], values[j]) = (values[j], values[i]);
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var w = PowMod(NttPrimitiveRoot, (NttModulus - 1) / len);
            if (invert)
                w = PowMod(w, NttModulus - 2);

            for (var i = 0; i < n; i += len)
            {
                var wn = 1L;
                for (var k = 0; k < len / 2; k++)
                {
                    var u = values[i + k];
                    var v = (int)(values[i + k + (len / 2)] * wn % NttModulus);

                    var sum = u + v;
                    if (sum >= NttModulus)
                        sum -= NttModulus;

                    var diff = u - v;
                    if (diff < 0)
                        diff += NttModulus;

                    values[i + k] = sum;
                    values[i + k + (len / 2)] = diff;
                    wn = wn * w % NttModulus;
                }
            }
        }

        if (!invert)
            return;

        var inverseN = PowMod(n, NttModulus - 2);
        for (var i = 0; i < n; i++)
            values[i] = (int)((long)values[i] * inverseN % NttModulus);
    }

    private static long PowMod(long baseValue, long exponent)
    {
        var result = 1L;
        var b = baseValue % NttModulus;
        var e = exponent;
        while (e > 0)
        {
            if ((e & 1) == 1)
                result = result * b % NttModulus;
            b = b * b % NttModulus;
            e >>= 1;
        }

        return result;
    }

    private static void PrepareFpInputs(int[] a, int[] b, double[] fa, double[] fb, int size)
    {
        Array.Clear(fa, 0, fa.Length);
        Array.Clear(fb, 0, fb.Length);
        for (var i = 0; i < a.Length; i++)
        {
            fa[i] = a[i];
            fb[i] = b[i];
        }
    }

    /// <summary>
    /// 浮点卷积（朴素 O(n²) 双精度），返回输出位模式的异或。
    /// <para>
    /// 刻意用最朴素的写法、按固定顺序累加：这样**指令序列完全确定**，
    /// 同一二进制在健康硬件上每次都给出同一个位模式，比特比对才有意义。
    /// </para>
    /// </summary>
    private static long ConvolveFp(double[] fa, double[] fb, double[] output, int size)
    {
        // 只用前 n 个点（输入有效部分）算线性卷积，结果长度 2n。
        var n = DefaultLength;
        for (var i = 0; i < n * 2; i++)
        {
            double acc = 0;
            var lo = Math.Max(0, i - n + 1);
            var hi = Math.Min(n - 1, i);
            for (var j = lo; j <= hi; j++)
                acc += fa[j] * fb[i - j];

            output[i] = acc;
        }

        // 其余位置置 0，让位模式异或覆盖整个输出（也顺带检验写入路径）。
        for (var i = n * 2; i < size; i++)
            output[i] = 0;

        var bits = 0L;
        for (var i = 0; i < size; i++)
            bits = unchecked((bits * 31) + BitConverter.DoubleToInt64Bits(output[i]));

        return bits;
    }

    /// <summary>浮点结果相对整数精确结果的最大相对误差（以输出最大幅值为分母）。</summary>
    private static double MaxRelativeError(double[] fp, long[] exact, int n)
    {
        var scale = 0.0;
        for (var i = 0; i < n * 2; i++)
        {
            var magnitude = Math.Abs((double)exact[i]);
            if (magnitude > scale)
                scale = magnitude;
        }

        if (scale <= 0)
            return 0;

        var worst = 0.0;
        for (var i = 0; i < n * 2; i++)
        {
            var error = Math.Abs(fp[i] - exact[i]) / scale;
            if (error > worst)
                worst = error;
        }

        return worst;
    }
}
