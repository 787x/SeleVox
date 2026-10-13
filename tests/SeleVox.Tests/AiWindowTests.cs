using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class AiWindowTests
{
    [Fact] public Task OcrAskUsesEditedTextWithoutCopyOrReadAndKeepsWindow() => OcrDesktopTests.RunSta(async () =>
    {
        var original = ClipboardBackup.Capture(); var reads = 0; string? asked = null;
        var window = new OcrResultWindow(new("original", TextSource.ScreenOcr), () => new(), _ => { ++reads; return Task.CompletedTask; }, value => asked = value);
        try
        {
            window.Show(); Clipboard.SetText("unchanged clipboard");
            var editor = Descendants<TextBox>(window).Single(); editor.Text = "edited question";
            Button(window, "问 AI").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("edited question", asked); Assert.True(window.IsVisible); Assert.Equal(0, reads); Assert.Equal("unchanged clipboard", Clipboard.GetText());
            asked = null; editor.Text = " "; Button(window, "问 AI").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.Null(asked);
            editor.Text = "edited question"; Button(window, "朗读文字").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.Equal(1, reads);
            Button(window, "复制文字").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.Equal("edited question", Clipboard.GetText());
            await Task.Yield();
        }
        finally { window.Close(); if (original is not null) Clipboard.SetDataObject(original, true); else Clipboard.Clear(); }
    });
    [Fact] public Task AnswerShowsIncrementalTextAndCloseCancelsWithoutLateSpeech() => OcrDesktopTests.RunSta(async () =>
    {
        Action<string>? delta = null; CancellationToken token = default;
        var complete = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        using var session = new QuestionSession((_, _, _, update, cancellation) => { delta = update; token = cancellation; return complete.Task; }, (_, _) => { ++reads; return Task.CompletedTask; }, () => { });
        var running = session.AskAsync("question", ChatCompletionTests.Options, "key");
        var window = new AiAnswerWindow(session, () => new(), () => "idle", session.Cancel, () => { });
        window.ShowAnswer(); delta!("partial"); window.Refresh();
        Assert.Contains(Descendants<TextBox>(window), b => b.Text == "partial");
        Assert.False(Button(window, "朗读 / 重新朗读").IsEnabled);
        OcrDesktopTests.RenderIfRequested(window, "pr8-answer-streaming");
        window.Close(); Assert.True(token.IsCancellationRequested); delta("late"); complete.SetResult("answer"); await running;
        Assert.Equal("partial", session.Snapshot.Answer); Assert.Equal(0, reads);
    });
    [Fact] public Task LlmSettingsRestoreSaveAndEmptyPromptRemainIndependentOfTts() => OcrDesktopTests.RunSta(async () =>
    {
        var saved = new Settings(Provider: TtsProvider.MiMo, LlmModel: "llm", LlmSystemPrompt: "original", OpenAiModel: "tts"); MainWindow? window = null;
        window = new MainWindow(saved, value => { saved = value; window!.SetAutomaticSettings(value); return null; }, () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { });
        try
        {
            ((TextBox)window.FindName("LlmSystemPrompt")).Clear(); ((TextBox)window.FindName("LlmModel")).Text = "updated";
            ((CheckBox)window.FindName("LlmAutoRead")).IsChecked = false; ((CheckBox)window.FindName("LlmStreaming")).IsChecked = false;
            for (var i = 0; i < 100 && ((ListBox)window.FindName("Outputs")).Items.Count == 0; ++i) await Task.Delay(10);
            Button(window, "保存设置").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("", saved.LlmSystemPrompt); Assert.Equal("updated", saved.LlmModel); Assert.False(saved.LlmAutoRead); Assert.False(saved.LlmStreaming);
            Assert.Equal("tts", saved.OpenAiModel); Assert.Equal(TtsProvider.MiMo, saved.Provider);
            window.Show(); window.UpdateLayout(); ((Expander)window.FindName("LlmPanel")).BringIntoView(); await Task.Delay(50); window.UpdateLayout();
            OcrDesktopTests.RenderIfRequested(window, "pr8-settings");
        }
        finally { window.Close(); }
    });
    [Fact] public void CredentialNamespacesSeparateNormalizedLlmAddressesAndTts()
    {
        var address = "https://example.invalid/" + Guid.NewGuid(); var llm = AzureCredentials.Llm(address);
        try
        {
            llm.Write("fake-qa-test-key"); Assert.Equal("fake-qa-test-key", AzureCredentials.Llm(address + "/").Read());
            Assert.Null(AzureCredentials.Llm(address + "-other").Read()); Assert.Null(AzureCredentials.OpenAi(address).Read());
        }
        finally { llm.Clear(); }
    }
    [Fact] public void OldConfigurationAndEmptyPromptPersistWithoutSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeleVox-qa-" + Guid.NewGuid()); var path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory); File.WriteAllText(path, "{\"MiMoVoice\":\"冰糖\"}"); var store = new SettingsStore(path);
            var old = store.Load(); Assert.Null(old.Warning); Assert.True(old.Settings.LlmStreaming); Assert.True(old.Settings.LlmAutoRead); Assert.Equal("", old.Settings.LlmModel);
            Assert.Equal(Settings.DefaultLlmPrompt, old.Settings.LlmSystemPrompt);
            var options = old.Settings with { LlmSystemPrompt = "", LlmModel = "model" }; store.Save(options);
            Assert.Equal(options, store.Load().Settings); Assert.DoesNotContain("Key", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static Button Button(DependencyObject window, string label) => Descendants<Button>(window).Single(b => b.Content as string == label);
}
