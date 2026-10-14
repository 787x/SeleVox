using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using SeleVox.Core;

namespace SeleVox.App;
public partial class MainWindow
{
    private VirtualMicrophoneRoute? virtualRoute;
    private IReadOnlyList<VirtualEndpoint> virtualEndpoints = [];
    private CancellationTokenSource? virtualTest;
    private bool virtualScanning, virtualRefreshing;
    private bool virtualDriverPresent;
    private DispatcherTimer? virtualTimer;
    internal void CancelVirtualMicrophoneTest() => virtualTest?.Cancel();
    private void InitializeVirtualMicrophone(Settings settings)
    {
        virtualRoute = settings.VirtualMicrophone; VirtualMicEnabled.IsChecked = settings.VirtualMicrophoneEnabled;
        virtualTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        virtualTimer.Tick += async (_, _) => { if (IsVisible && virtualTest is null) await ScanVirtualAsync(); };
        IsVisibleChanged += async (_, _) => { if (IsVisible) { virtualTimer.Start(); await ScanVirtualAsync(); } else { virtualTimer.Stop(); virtualTest?.Cancel(); } };
        Closed += (_, _) => { virtualTimer.Stop(); virtualTest?.Cancel(); };
        _ = ScanVirtualAsync(true);
    }
    private async void VirtualScan_Click(object sender, RoutedEventArgs e) => await ScanVirtualAsync(true);
    private async Task ScanVirtualAsync(bool detect = false)
    {
        if (virtualScanning || virtualTest is not null) return;
        virtualScanning = true;
        try
        {
            var discovered = await Task.Run(() => (Devices: VirtualMicrophones.Scan(), Installed: VirtualMicrophones.DriverPresent()));
            virtualEndpoints = discovered.Devices; virtualDriverPresent = discovered.Installed;
            if (detect) virtualRoute ??= VirtualMicrophones.Detect(virtualEndpoints);
            var selectedRender = virtualRoute?.RenderId ?? VirtualRender.SelectedValue as string;
            var selectedCapture = virtualRoute?.CaptureId ?? VirtualCapture.SelectedValue as string;
            virtualRefreshing = true;
            var render = virtualEndpoints.Where(d => d.Flow == DataFlow.Render).Select(d => new OutputChoice(d.Id, d.Label + (d.State == DeviceState.Active ? "" : "（不可用）"))).ToList();
            var capture = virtualEndpoints.Where(d => d.Flow == DataFlow.Capture).Select(d => new OutputChoice(d.Id, d.Label + (d.State == DeviceState.Active ? "" : "（不可用）"))).ToList();
            if (virtualRoute is { } route)
            {
                if (render.All(d => d.Id != route.RenderId)) render.Add(new(route.RenderId, "已保存播放端点不可用（保留 ID）"));
                if (capture.All(d => d.Id != route.CaptureId)) capture.Add(new(route.CaptureId, "已保存录音端点不可用（保留 ID）"));
            }
            VirtualRender.ItemsSource = render; VirtualCapture.ItemsSource = capture;
            VirtualRender.SelectedValue = selectedRender; VirtualCapture.SelectedValue = selectedCapture;
            UpdateVirtualStatus();
        }
        catch { VirtualMicStatus.Text = "发生故障：无法枚举虚拟端点，请稍后重新扫描。"; }
        finally { virtualRefreshing = virtualScanning = false; }
    }
    private void UpdateVirtualStatus()
    {
        var route = virtualRoute;
        var status = virtualEndpoints.Count == 0 ? virtualDriverPresent
            ? "设备不可用：驱动已存在但端点未就绪，请检查禁用状态或安装后的重启要求。"
            : "未找到兼容 VB-CABLE 驱动 / 端点，请通过首次安装引导检查安装及重启。" :
            route is null ? "配对不明确：请选择同一驱动的 Render / Capture，并执行录音验证。" :
            !VirtualMicrophones.IsPair(route, virtualEndpoints, true) ? "设备不可用：端点缺失、禁用、断开或需要重启。保留已选 ID，不改选其他设备。" :
            !route.RecordingVerified ? "端点可用，尚未通过录音验证。" : "就绪：已通过固定音频录音验证。";
        if (route is not null && VirtualMicrophones.Activity(route.RenderId) is { Length: > 0 } activity) status = activity + " · " + status;
        VirtualMicStatus.Text = (savedSettings.VirtualMicrophoneEnabled ? "已启用 · " : "已关闭 · ") + status +
            (VirtualMicEnabled.IsChecked == true != savedSettings.VirtualMicrophoneEnabled || virtualRoute != savedSettings.VirtualMicrophone ? " 修改尚未保存。" : "");
    }
    private void VirtualMicEnabled_Click(object sender, RoutedEventArgs e) => UpdateVirtualStatus();
    private void VirtualPair_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (virtualRefreshing || VirtualCapture is null) return;
        virtualTest?.Cancel();
        var render = virtualEndpoints.FirstOrDefault(d => d.Id == VirtualRender.SelectedValue as string);
        var capture = virtualEndpoints.FirstOrDefault(d => d.Id == VirtualCapture.SelectedValue as string);
        virtualRoute = render is not null && capture is not null && render.DriverInstanceId == capture.DriverInstanceId
            ? new(render.Id, capture.Id, render.DriverInstanceId) : null;
        UpdateVirtualStatus();
    }
    private async void VirtualTest_Click(object sender, RoutedEventArgs e)
    {
        if (virtualTest is not null) { virtualTest.Cancel(); return; }
        if (virtualRoute is not { } route || !VirtualMicrophones.IsPair(route, virtualEndpoints, true))
        { SetNotice("请选择同一受支持驱动的可用 Render 与 Capture。无需选择物理麦克风。"); return; }
        stop(); // A test replaces current reading; it does not run a second foreground session.
        using var source = new CancellationTokenSource(); virtualTest = source;
        VirtualTestButton.Content = "取消测试"; VirtualMicStatus.Text = "正在播放固定 PCM 并从所选虚拟 Capture 录音（仅内存）。";
        try
        {
            var score = await Task.Run(() => VirtualMicrophoneTest.RunAsync(route, source.Token));
            source.Token.ThrowIfCancellationRequested();
            if (virtualRoute != route) return;
            virtualRoute = route with { RecordingVerified = true };
            SetNotice($"录音验证通过（波形关联 {score:F2}）。选择启用后保存设置，在目标应用中选择此 Capture。录音未保存。");
            UpdateVirtualStatus();
        }
        catch (OperationCanceledException) { SetNotice("虚拟麦克风测试已取消。"); UpdateVirtualStatus(); }
        catch (Exception ex)
        {
            if (virtualRoute == route) virtualRoute = route with { RecordingVerified = false };
            VirtualMicStatus.Text = "发生故障：" + (ex is UserFeedbackException ? ex.Message : "播放或录音失败，请检查设备与 Windows 录音权限。");
        }
        finally { virtualTest = null; VirtualTestButton.Content = "播放测试音并录音验证"; }
    }
    private void VirtualInstall_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "VB-CABLE 来自 VB-Audio，采用 donationware 许可，欢迎捐赠 / 支付许可费用。SeleVox 不捆绑或自动安装驱动。\n\n从官方页面下载 Windows x64 包，检查安装器数字签名，按厂商说明以管理员权限安装并重启，再回到此处扫描并录音验证。已有正常驱动无需重新安装。无需启动 Voicemeeter。\n\n打开官方下载安装页面？", "VB-CABLE 首次安装引导", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/index.htm") { UseShellExecute = true }); }
        catch { SetNotice("无法打开浏览器，请访问 https://vb-audio.com/Cable/index.htm 。"); }
    }
}
