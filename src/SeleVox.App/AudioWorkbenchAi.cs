using System.Windows;
using System.Windows.Controls;
using SeleVox.Core;

namespace SeleVox.App;

public partial class AudioWorkbenchWindow
{
    private ChatCompletionClient creativeClient = new();
    private Func<string, string?> creativeKey = url => AzureCredentials.Llm(url).Read();
    private CancellationTokenSource? aiRequest;
    private long aiEpoch;
    private (TextBox Target, string Text)? aiUndo;
    internal void ConfigureCreativeClient(ChatCompletionClient client, Func<string, string?> key) { creativeClient = client; creativeKey = key; }
    private void InitializeAi()
    {
        foreach (var target in new[] { InputText, VoiceDescription, MiMoStyle })
            target.TextChanged += (_, _) => { if (aiRequest is not null) CancelAi("输入已编辑；AI 结果不会覆盖当前草稿。"); };
        Provider.SelectionChanged += (_, _) => CancelAi("Provider 已切换；AI 请求已取消。");
        CreativeMode.SelectionChanged += (_, _) => CancelAi("模型模式已切换；AI 请求已取消。");
        CreativeControl.SelectionChanged += (_, _) => CancelAi("控制方式已切换；AI 请求已取消。");
    }
    private async void AiGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<CreativePurpose>(tag, out var purpose)) await GenerateCreativeAsync(purpose);
    }
    internal async Task GenerateCreativeAsync(CreativePurpose purpose)
    {
        if (closed) return;
        CancelAi("正在准备 AI 请求…");
        var target = purpose switch { CreativePurpose.VoiceDescription => VoiceDescription, CreativePurpose.Style => MiMoStyle, _ => InputText };
        var original = target.Text;
        var provider = Provider.SelectedItem; var mode = Mode; var control = Control;
        if (purpose == CreativePurpose.VoiceDescription && (provider is not TtsProvider.MiMo || mode != MiMoCreativeMode.VoiceDesign) ||
            purpose == CreativePurpose.Style && (provider is not TtsProvider.MiMo || control != MiMoControl.NaturalLanguage) ||
            purpose == CreativePurpose.AudioTags && (provider is not TtsProvider.MiMo || control != MiMoControl.AudioTags))
        { AiStatus.Text = "当前模式不支持此 AI 操作。"; return; }
        if (string.IsNullOrWhiteSpace(original)) { AiStatus.Text = "请先输入创作需求或完整标签脚本。"; return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        aiRequest = request; var id = aiEpoch;
        var actionButton = purpose switch { CreativePurpose.VoiceDescription => AiVoice, CreativePurpose.Style => AiStyle, CreativePurpose.AudioTags => AiTags, _ => AiScript };
        actionButton.Content = purpose == CreativePurpose.AudioTags ? "AI 正在添加…" : "AI 正在生成…";
        CancelAiButton.IsEnabled = true; AiStatus.Text = "AI 正在生成…可编辑草稿或取消请求。";
        try
        {
            var settings = getSettings() with { LlmSystemPrompt = CreativeText.Prompt(purpose, provider is TtsProvider.MiMo && mode == MiMoCreativeMode.PresetVoice && control == MiMoControl.Singing) };
            var key = await Task.Run(() => creativeKey(settings.LlmBaseUrl), request.Token);
            ChatCompletionClient.Validate(original, settings, key);
            var output = await creativeClient.GenerateAsync(original, settings, key!, _ => { }, request.Token);
            await Task.Run(() => CreativeText.Validate(purpose, original, output, request.Token), request.Token);
            if (closed || request.IsCancellationRequested || id != aiEpoch || target.Text != original ||
                !Equals(Provider.SelectedItem, provider) || Mode != mode || Control != control) return;
            aiRequest = null;
            aiUndo = (target, original); RestoreAiButton.IsEnabled = true;
            target.Text = output; AiStatus.Text = "AI 文本已替换，可继续编辑或恢复替换前文本；未生成语音。";
        }
        catch (OperationCanceledException) { if (!closed && id == aiEpoch) AiStatus.Text = "AI 请求已取消；原文保留。"; }
        catch (Exception ex) { if (!closed && id == aiEpoch) AiStatus.Text = ex is UserFeedbackException ? ex.Message : "AI 创作失败；原文保留，请检查主设置或网络。"; }
        finally { if (ReferenceEquals(aiRequest, request)) aiRequest = null; if (!closed && id == aiEpoch) { CancelAiButton.IsEnabled = false; actionButton.Content = purpose == CreativePurpose.AudioTags ? "AI 添加标签" : "AI 生成"; } }
    }
    internal void CancelAi(string message = "AI 请求已取消；原文保留。")
    {
        ++aiEpoch; aiRequest?.Cancel(); aiRequest = null;
        if (!closed) { CancelAiButton.IsEnabled = false; AiVoice.Content = AiStyle.Content = AiScript.Content = "AI 生成"; AiTags.Content = "AI 添加标签"; AiStatus.Text = message; }
    }
    private void CancelAi_Click(object sender, RoutedEventArgs e) => CancelAi();
    private void RestoreAi_Click(object sender, RoutedEventArgs e)
    {
        if (aiUndo is not { } previous) return;
        CancelAi(); previous.Target.Text = previous.Text; aiUndo = null; RestoreAiButton.IsEnabled = false;
        AiStatus.Text = "已恢复上一次 AI 替换前文本。";
    }
}
