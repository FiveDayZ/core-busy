namespace CoreBusy.App.Views;

using System.Windows;
using CoreBusy.App.Services;

/// <summary>
/// 更新下载进度对话框（v1.31.0）：进度条 + 字节计数；右上角关闭即取消下载
/// （关闭事件只上报 <see cref="CancelRequested"/>，取消动作由持有
/// CancellationToken 的调用方执行，避免对话框自己管理下载生命周期）。
/// </summary>
public partial class UpdateProgressDialog : Window
{
    /// <summary>用户请求取消（点了标题栏关闭）。外层接到 CancellationTokenSource 上。</summary>
    public event EventHandler? CancelRequested;

    public UpdateProgressDialog(Window? owner)
    {
        InitializeComponent();

        // owner 可为 null（如主窗口尚未赋给 Application.MainWindow 的极端时序）。
        if (owner is not null)
            Owner = owner;
    }

    /// <summary>上报进度（UI 线程调用；数值直填控件，不走绑定）。</summary>
    public void Report(UpdateDownloadProgress progress)
    {
        Bar.Value = progress.Percent;
        DetailText.Text = progress.Detail;
    }

    private void OnCancel(object sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);
}
