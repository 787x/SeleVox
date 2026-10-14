using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using SeleVox.Core;

namespace SeleVox.App;

public partial class MainWindow
{
    private DiagnosticsWindow? diagnosticsWindow;
    internal Func<DiagnosticsWindow>? CreateDiagnosticsWindow { get; set; }
    private DiagnosticsWindow EnsureDiagnosticsWindow()
    {
        if (diagnosticsWindow is not null) return diagnosticsWindow;
        var window = CreateDiagnosticsWindow?.Invoke() ?? new(() => savedSettings, HasUnsavedDiagnosticSettings);
        window.Owner = this; diagnosticsWindow = window;
        window.DiagnosticChanged += UpdateDiagnosticLink;
        window.Closed += (_, _) => {
            window.DiagnosticChanged -= UpdateDiagnosticLink; diagnosticsWindow = null;
            foreach (var service in Enum.GetValues<DiagnosticService>()) UpdateDiagnosticLink(service, DiagnosticState.Untested, "点击检测已保存配置；在线检测可能收费。", true);
        };
        window.Refresh(); return window;
    }
    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var window = EnsureDiagnosticsWindow(); window.Show(); window.Activate(); window.Refresh();
    }
    private async void InlineDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink link && Enum.TryParse<DiagnosticService>(link.Tag as string, out var service))
            await EnsureDiagnosticsWindow().RunServiceAsync(service);
    }
    private void UpdateDiagnosticLink(DiagnosticService service, DiagnosticState state, string details, bool enabled)
    {
        var link = service switch { DiagnosticService.Windows => WindowsDiagnosticLink, DiagnosticService.Azure => AzureDiagnosticLink,
            DiagnosticService.Edge => EdgeDiagnosticLink, DiagnosticService.MiMo => MiMoDiagnosticLink,
            DiagnosticService.OpenAiTts => OpenAiDiagnosticLink, _ => LlmDiagnosticLink };
        link.Inlines.Clear(); link.Inlines.Add(state switch { DiagnosticState.Running => "检测中 · 取消", DiagnosticState.Succeeded => "检测成功",
            DiagnosticState.Failed => "检测失败", DiagnosticState.Canceled => "已取消 · 重测", DiagnosticState.Expired => "已过期 · 重测", _ => "检测" });
        link.Foreground = state switch { DiagnosticState.Succeeded => Brushes.ForestGreen, DiagnosticState.Failed => Brushes.Firebrick,
            DiagnosticState.Expired or DiagnosticState.Canceled => Brushes.Gray, _ => new SolidColorBrush(Color.FromRgb(25, 59, 86)) };
        link.ToolTip = details + "\n点击可重新检测；在线检测可能收费。"; link.IsEnabled = enabled;
    }
    internal bool HasUnsavedDiagnosticSettings(DiagnosticService service)
    {
        var s = savedSettings;
        if (service != DiagnosticService.Llm && Rate.Value != s.Rate) return true;
        return service switch {
            DiagnosticService.Windows => Voice.SelectedValue as string != (s.VoiceId ?? Windows.Media.SpeechSynthesis.SpeechSynthesizer.DefaultVoice.Id),
            DiagnosticService.Azure => Region.Text.Trim() != s.AzureRegion || CloudVoice.SelectedValue as string != s.AzureVoice || AzureKey.Password.Length != 0,
            DiagnosticService.Edge => edgeSelection != s.EdgeVoice || (int)EdgePitch.Value != s.EdgePitchHz,
            DiagnosticService.MiMo => MiMoModel.Text.Trim() != s.MiMoModel || MiMoVoice.SelectedItem as string != s.MiMoVoice ||
                MiMoStyle.Text != s.MiMoStyle || MiMoStreaming.IsChecked == true != s.MiMoStreaming ||
                !miMoStyles.Selected.SequenceEqual(s.MiMoOverallStyles ?? []) || MiMoKey.Password.Length != 0,
            DiagnosticService.OpenAiTts => Settings.NormalizeServiceUrl(OpenAiBaseUrl.Text) != Settings.NormalizeServiceUrl(s.OpenAiBaseUrl) ||
                OpenAiModel.Text.Trim() != s.OpenAiModel || OpenAiVoice.Text.Trim() != s.OpenAiVoice ||
                OpenAiInstructions.Text != s.OpenAiInstructions || OpenAiKey.Password.Length != 0,
            _ => Settings.NormalizeServiceUrl(LlmBaseUrl.Text) != Settings.NormalizeServiceUrl(s.LlmBaseUrl) || LlmModel.Text.Trim() != s.LlmModel ||
                LlmStreaming.IsChecked == true != s.LlmStreaming || (ThinkingProtocol)ThinkingProtocolChoice.SelectedIndex != s.LlmThinkingProtocol ||
                (ThinkingOptionChoice.SelectedValue is ThinkingOption value ? value : ThinkingOption.Default) != s.LlmThinking ||
                LlmSystemPrompt.Text != s.LlmSystemPrompt || LlmKey.Password.Length != 0
        };
    }
}
