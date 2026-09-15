namespace CoreBusy.App.Controls;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// 轻量指数趋近动画器：随渲染帧把显示值平滑逼近期望值，
/// 消除采样周期带来的数值跳变/生硬闪烁。接近目标后自动摘除渲染钩子，零空闲开销。
/// <para>
/// 限帧：默认软件渲染（RenderOptions.ProcessRenderMode = SoftwareOnly）下，每帧都要整卡重绘，
/// 60fps 的动画会让本工具自身 CPU 占用显著升高（实测 500ms 刷新时可达 15% 单核）。
/// 这里用 <see cref="RenderingEventArgs.RenderingTime"/> 做**全实例共享**的帧闸门降到 30fps：
/// 同一帧内所有动画器取到一致的判定结果，不会出现部分控件被饿死。
/// </para>
/// </summary>
internal sealed class ValueAnimator
{
    private const double Step = 0.30;       // 每帧逼近比例
    private const double SnapEpsilon = 0.6; // 小于该差值直接贴合

    /// <summary>限帧步长：每 2 个合成帧处理 1 次（约 30fps）。</summary>
    private const int FrameStride = 2;

    // 共享帧闸门（所有实例同一帧得到一致结果）。
    private static TimeSpan _frameTime = TimeSpan.MinValue;
    private static int _framePhase;
    private static bool _frameRender;

    private readonly FrameworkElement _owner;
    private double _current;
    private double _target;
    private bool _hooked;

    public ValueAnimator(FrameworkElement owner, double initial = 0)
    {
        _owner = owner;
        _current = initial;
        _target = initial;
        _owner.Unloaded += (_, _) => Detach();
    }

    /// <summary>当前显示值（OnRender 使用）。</summary>
    public double Current => _current;

    /// <summary>跳过动画直接贴合（首帧/复位）。</summary>
    public void Snap(double value)
    {
        _current = Sanitize(value);
        _target = _current;
        Detach();
    }

    /// <summary>平滑逼近目标值。</summary>
    public void MoveTo(double value)
    {
        _target = Sanitize(value);
        var diff = Math.Abs(_target - _current);
        if (diff < SnapEpsilon)
        {
            _current = _target;
            Detach();
            return;
        }

        if (!_hooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // 同一帧内所有动画器共享判定；被限掉的帧只跳过绘制（不推进动画）。
        if (e is RenderingEventArgs args && !ShouldRenderFrame(args.RenderingTime))
            return;

        var diff = _target - _current;
        if (Math.Abs(diff) < SnapEpsilon)
        {
            _current = _target;
            Detach();
        }
        else
        {
            _current += diff * Step;
        }

        _owner.InvalidateVisual();
    }

    /// <summary>共享帧闸门：同一合成帧内所有实例返回同一结果。</summary>
    private static bool ShouldRenderFrame(TimeSpan renderingTime)
    {
        if (renderingTime != _frameTime)
        {
            _frameTime = renderingTime;
            _framePhase = (_framePhase + 1) % FrameStride;
            _frameRender = _framePhase == 0;
        }

        return _frameRender;
    }

    private void Detach()
    {
        if (!_hooked)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
    }

    private static double Sanitize(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Clamp(value, 0, 100);
}
