using System.Windows;
using SeleVox.Core;

namespace SeleVox.App;

public partial class MainWindow
{
    private DiagnosticsWindow? diagnosticsWindow;
    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (diagnosticsWindow is not null) { diagnosticsWindow.Activate(); diagnosticsWindow.Refresh(); return; }
        diagnosticsWindow = new(() => savedSettings, HasUnsavedDiagnosticSettings) { Owner = this };
        diagnosticsWindow.Closed += (_, _) => diagnosticsWindow = null;
        diagnosticsWindow.Show();
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
