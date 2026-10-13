using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed class AiAnswerWindow : Window
{
    private readonly TextBox question = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox answer = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 16 };
    private readonly TextBlock destination = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock privacy = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button readButton;
    private readonly DispatcherTimer timer;
    private bool hiddenForCapture;
    private Rect captureBounds;
    private WindowState captureState;
    private readonly QuestionSession session;
    private readonly Func<Settings> settings;
    private readonly Func<string> readingStatus;
    internal AiAnswerWindow(QuestionSession session, Func<Settings> settings, Func<string> readingStatus, Action stop, Action read)
    {
        this.session = session; this.settings = settings; this.readingStatus = readingStatus;
        Title = "SeleVox · AI 回答"; Width = 720; Height = 600; MinWidth = 460; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(new TextBlock { Text = "当前问题", FontWeight = FontWeights.SemiBold }); header.Children.Add(question); header.Children.Add(destination); grid.Children.Add(header);
        Grid.SetRow(answer, 1); grid.Children.Add(answer);
        var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(privacy); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        AddButton(actions, "复制回答", () =>
        {
            if (string.IsNullOrWhiteSpace(answer.Text)) return;
            try { Clipboard.SetText(answer.Text); } catch { status.Text = "剪贴板不可用，请重试。"; }
        });
        readButton = AddButton(actions, "朗读 / 重新朗读", read);
        AddButton(actions, "取消生成 / 停止", stop); AddButton(actions, "关闭", Close);
        Content = grid;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Closed += (_, _) => { timer.Stop(); session.Cancel(); question.Clear(); answer.Clear(); Content = null; };
        Refresh();
    }
    internal void ShowAnswer(bool activate = true)
    {
        Refresh(); var saved = ShowActivated;
        try { ShowActivated = activate; Show(); if (activate && WindowState == WindowState.Minimized) WindowState = WindowState.Normal; if (activate) Activate(); }
        finally { ShowActivated = saved; }
    }
    internal void Refresh()
    {
        var snapshot = session.Snapshot;
        if (question.Text != snapshot.Question) question.Text = snapshot.Question;
        destination.Text = $"LLM 服务：{snapshot.Service} · 模型：{snapshot.Model}";
        if (answer.Text != snapshot.Answer)
        {
            var follow = answer.VerticalOffset >= answer.ExtentHeight - answer.ViewportHeight - 20;
            if (snapshot.Answer.StartsWith(answer.Text, StringComparison.Ordinal)) answer.AppendText(snapshot.Answer[answer.Text.Length..]);
            else { answer.Text = snapshot.Answer; follow = true; }
            if (follow) answer.ScrollToEnd();
        }
        status.Text = snapshot.Message + " · 音频：" + readingStatus();
        readButton.IsEnabled = snapshot.AnswerComplete && snapshot.State is not (QuestionState.Generating or QuestionState.Speaking) && snapshot.Answer.Length > 0;
        var current = settings();
        privacy.Text = "主动提问会将问题和 System Prompt 发送至所配置 LLM，可能收费。" +
            (current.Provider == TtsProvider.Windows ? "回答朗读使用 Windows 本地 TTS。" : "回答朗读使用在线 TTS，将再次发送回答到当前 TTS 服务，可能另行收费。") +
            (current.LlmAutoRead ? current.LlmReadingStrategy == AnswerReadingStrategy.Incremental && current.LlmStreaming ? "逐句朗读已启用；自动问答须满足独立 TTS 授权。" : "完整回答将自动朗读；自动问答须满足独立 TTS 授权。" : "自动朗读已关闭。");
    }
    internal void HideForCapture()
    {
        if (hiddenForCapture || !IsVisible) return;
        captureState = WindowState;
        captureBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        Hide(); hiddenForCapture = true;
    }
    internal void RestoreAfterCapture()
    {
        if (!hiddenForCapture) return;
        WindowStartupLocation = WindowStartupLocation.Manual; WindowState = WindowState.Normal;
        Left = captureBounds.Left; Top = captureBounds.Top; Width = captureBounds.Width; Height = captureBounds.Height;
        WindowState = captureState; hiddenForCapture = false;
        var activated = ShowActivated;
        try { ShowActivated = false; Show(); } finally { ShowActivated = activated; }
    }
    private static Button AddButton(Panel parent, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action(); parent.Children.Add(button); return button;
    }
}
