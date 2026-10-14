using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed class DiagnosticsWindow : Window
{
    private sealed class Row(DiagnosticService service)
    {
        internal readonly DiagnosticService Service = service;
        internal readonly TextBlock Target = new() { TextWrapping = TextWrapping.Wrap };
        internal readonly TextBlock Precheck = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) };
        internal readonly TextBlock Result = new() { Text = "未检测", TextWrapping = TextWrapping.Wrap };
        internal readonly Button Run = new() { Content = "检测此服务（在线可能收费）", Padding = new(8, 4, 8, 4) };
        internal readonly Button Cancel = new() { Content = "取消", IsEnabled = false, Padding = new(8, 4, 8, 4), Margin = new(8, 0, 0, 0) };
        internal CancellationTokenSource? Cancellation;
        internal string? Identity;
        internal bool Expired;
        internal DiagnosticState State;
    }
    private readonly Func<Settings> saved;
    private readonly Func<DiagnosticService, bool> dirty;
    private readonly ProviderDiagnostics diagnostics;
    private readonly Func<DiagnosticService, Settings, string?> readKey;
    private readonly List<Row> rows = [];
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool closed, busy;
    internal event Action<DiagnosticService, DiagnosticState, string, bool>? DiagnosticChanged;
    internal Task RunServiceAsync(DiagnosticService service)
    {
        var row = rows.Single(r => r.Service == service);
        if (row.Cancellation is not null) { row.Cancellation.Cancel(); return Task.CompletedTask; }
        Refresh();
        if (!busy && !row.Run.IsEnabled)
        {
            row.State = DiagnosticState.Failed; row.Result.Text = "未发起调用：" + row.Precheck.Text; Refresh();
            return Task.CompletedTask;
        }
        return RunAsync(row);
    }
    internal DiagnosticsWindow(Func<Settings> saved, Func<DiagnosticService, bool> dirty, ProviderDiagnostics? diagnostics = null,
        Func<DiagnosticService, Settings, string?>? readKey = null)
    {
        this.saved = saved; this.dirty = dirty; this.diagnostics = diagnostics ?? new();
        this.readKey = readKey ?? ProviderDiagnostics.ReadKey;
        Title = "SeleVox · 服务检测"; Width = 790; Height = 850; MinWidth = 620; MinHeight = 500;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "服务检测", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "仅使用已保存配置。点击每项检测即同意发送固定短句 / 问题，在线调用可能收费；不读取选区、OCR 或草稿，不播放音频。一次只检测一项，不重试。结果只代表本次调用。LLM 使用固定 System Prompt，不验证自定义 Prompt 质量。VoiceClone 此处不上传样本。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 12) });
        foreach (var service in Enum.GetValues<DiagnosticService>())
        {
            var row = new Row(service); rows.Add(row);
            if (service == DiagnosticService.Windows) row.Run.Content = "检测本地语音（离线）";
            var body = new StackPanel(); body.Children.Add(row.Target); body.Children.Add(row.Precheck);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 6) };
            actions.Children.Add(row.Run); actions.Children.Add(row.Cancel); body.Children.Add(actions); body.Children.Add(row.Result);
            panel.Children.Add(new Border { Child = body, Background = Brushes.White, Padding = new(12), Margin = new(0, 6, 0, 6) });
            row.Run.Click += async (_, _) => await RunAsync(row);
            row.Cancel.Click += (_, _) => row.Cancellation?.Cancel();
        }
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        monitor.Tick += Monitor; Refresh(); monitor.Start();
        Closed += (_, _) => { closed = true; monitor.Stop(); monitor.Tick -= Monitor; foreach (var row in rows) row.Cancellation?.Cancel(); };
    }
    private DiagnosticSnapshot Capture(DiagnosticService service) { var s = saved(); return DiagnosticSnapshot.Create(service, s, readKey(service, s)); }
    private void Monitor(object? sender, EventArgs e) => Refresh();
    internal void Refresh()
    {
        if (closed) return;
        foreach (var row in rows)
        {
            var ready = false;
            try
            {
                var current = Capture(row.Service); row.Target.Text = current.Summary;
                var unsaved = dirty(row.Service);
                if (row.Identity is not null && (current.Identity != row.Identity || unsaved)) Expire(row);
                if (unsaved) row.Precheck.Text = "存在尚未保存的参数 / Key：请先保存，再检测。";
                else { diagnostics.Precheck(current); row.Precheck.Text = "配置已就绪（尚不代表服务可用）。"; ready = true; }
            }
            catch (Exception ex)
            {
                if (row.Identity is not null) Expire(row);
                row.Precheck.Text = ex is UserFeedbackException feedback ? feedback.Message : "配置或凭据读取失败。";
            }
            row.Run.IsEnabled = ready && !busy;
            DiagnosticChanged?.Invoke(row.Service, row.State, row.Target.Text + "\n" + row.Precheck.Text + "\n" + row.Result.Text,
                !busy || row.Cancellation is not null);
        }
    }
    private static void Expire(Row row)
    {
        if (row.Expired) return;
        row.Expired = true; row.Cancellation?.Cancel();
        row.State = DiagnosticState.Expired;
        row.Result.Text = "结果过期：配置 / 凭据已改变或有未保存修改。\n" + row.Result.Text;
    }
    internal void CredentialsChanged()
    {
        foreach (var row in rows.Where(r => r.Identity is not null)) Expire(row);
        Refresh();
    }
    private async Task RunAsync(Row row)
    {
        if (closed || busy) return;
        Refresh(); if (!row.Run.IsEnabled) return;
        DiagnosticSnapshot snapshot;
        try { snapshot = Capture(row.Service); }
        catch { row.Precheck.Text = "无法读取当前配置或凭据，请重新保存后检测。"; return; }
        busy = true; row.Expired = false; row.Identity = snapshot.Identity;
        using var cancellation = new CancellationTokenSource(); row.Cancellation = cancellation; row.Cancel.IsEnabled = true;
        row.State = DiagnosticState.Running; row.Result.Text = "检测中：真实功能调用（最长 90 秒）…"; Refresh();
        try
        {
            var result = await diagnostics.RunAsync(snapshot, cancellation.Token);
            if (closed) return;
            Refresh(); if (row.Expired) return;
            row.State = result.State;
            var label = result.State switch { DiagnosticState.Succeeded => "成功", DiagnosticState.Canceled => "取消", _ => "失败" };
            row.Result.Text = $"{label} · {result.Elapsed.TotalSeconds:0.00} 秒 · 最近检测 {result.TestedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n" +
                (result.Failure is { } failure ? $"分类：{FailureLabel(failure)}" + (result.HttpStatus is { } status ? $" · HTTP {status}" : "") + "\n" : "") + result.Message;
        }
        finally { row.Cancellation = null; busy = false; if (!closed) { row.Cancel.IsEnabled = false; Refresh(); } }
    }
    private static string FailureLabel(ServiceFailure failure) => failure switch {
        ServiceFailure.Configuration => "配置无效 / 条件未满足", ServiceFailure.MissingCredential => "Key 缺失或无效",
        ServiceFailure.Authentication => "鉴权失败", ServiceFailure.RateLimited => "请求受限（频率或配额）",
        ServiceFailure.Parameters => "地址 / 模型 / 音色 / 参数不支持", ServiceFailure.Server => "服务端错误",
        ServiceFailure.Connection => "连接失败或中断", ServiceFailure.Timeout => "超时",
        ServiceFailure.ResponseTooLarge => "响应超过上限", _ => "响应 / 音频为空、无效或不完整"
    };
}
