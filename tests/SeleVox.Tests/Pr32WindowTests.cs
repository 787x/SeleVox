using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
[Trait("Category", "Offline")]
public sealed class Pr32WindowTests
{
    private static void Click(CheckBox box, bool value) { box.IsChecked = value; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    [Fact] public Task QuickTogglePersistsSynchronizesPreservesDraftAndRollsBackRealSaveFailure() => OcrDesktopTests.RunSta(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeleVox-pr32-" + Guid.NewGuid()); var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path); var saved = new Settings(Provider: TtsProvider.MiMo, LlmModel: "saved-model"); store.Save(saved);
        var speech = 0; var stop = 0; var requests = 0;
        var completion = new TaskCompletionSource<string>();
        using var session = new QuestionSession((_, _, _, _, _) => { ++requests; return completion.Task; }, (_, _) => { ++speech; return Task.CompletedTask; }, () => ++stop);
        MainWindow? main = null; AiAnswerWindow? answer = null;
        string? Save(Settings updated)
        {
            try
            {
                var shortcut = SeleVox.App.App.OnlyLlmAutoReadChanged(saved, updated);
                store.Save(updated); saved = updated;
                if (main is not null)
                {
                    main.SetLlmAutoRead(saved.LlmAutoRead);
                    if (!shortcut) main.SetAutomaticSettings(saved);
                }
                answer?.Refresh(); return null;
            }
            catch (Exception) { main?.SetLlmAutoRead(saved.LlmAutoRead); return "设置保存失败"; }
        }
        main = new(saved, Save, () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { });
        answer = new(session, () => saved, () => "fixture", session.Cancel, () => { }, saveAutoRead: enabled => Save(saved with { LlmAutoRead = enabled }));
        try
        {
            var running = session.AskAsync("fixture", ChatCompletionTests.Options, "fixture-key"); var before = stop;
            var model = (TextBox)main.FindName("LlmModel"); model.Text = "unsaved-model";
            var prompt = (TextBox)main.FindName("LlmSystemPrompt"); prompt.Text = "unsaved prompt";
            var ocr = (ComboBox)main.FindName("OcrActionChoice"); ocr.SelectedIndex = (int)OcrCompletionAction.AskAi;
            var streaming = (CheckBox)main.FindName("LlmStreaming"); streaming.IsChecked = false;
            var quick = AiWindowTests.Descendants<CheckBox>(answer).Single(c => Equals(c.Content, "自动朗读 AI 回答"));
            var startup = (CheckBox)main.FindName("AutoStart"); startup.IsChecked = true;
            Click(quick, false);
            Assert.True(startup.IsChecked);
            Assert.False(saved.LlmAutoRead); Assert.False(store.Load().Settings.LlmAutoRead); Assert.False(((CheckBox)main.FindName("LlmAutoRead")).IsChecked);
            Assert.Equal("saved-model", saved.LlmModel); Assert.Equal("unsaved-model", model.Text); Assert.Equal("unsaved prompt", prompt.Text);
            Assert.False(streaming.IsChecked); Assert.Equal(2, ocr.SelectedIndex); Assert.Equal(before, stop); Assert.Equal(1, requests);
            // The active request retains its launch snapshot despite the shortcut change.
            completion.SetResult("answer"); await running; Assert.Equal(1, speech);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(quick, true);
                Click((CheckBox)main.FindName("LlmAutoRead"), true);
            }
            Assert.True(startup.IsChecked); startup.IsChecked = false;
            Assert.False(quick.IsChecked); Assert.False(saved.LlmAutoRead); Assert.False(store.Load().Settings.LlmAutoRead);
            Assert.Contains(AiWindowTests.Descendants<TextBlock>(answer), t => t.Text.Contains("设置保存失败"));
            Click((CheckBox)main.FindName("LlmAutoRead"), true); answer.Refresh(); Assert.True(quick.IsChecked); Assert.True(store.Load().Settings.LlmAutoRead);
            Click(quick, false);
            for (var i = 0; i < 100 && ((ListBox)main.FindName("Outputs")).Items.Count == 0; ++i) await Task.Delay(10);
            AiWindowTests.Descendants<Button>(main).Single(b => Equals(b.Content, "保存设置")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(saved.LlmAutoRead); Assert.Equal("unsaved-model", saved.LlmModel); Assert.Equal("unsaved prompt", saved.LlmSystemPrompt);
            Assert.False(saved.LlmStreaming); Assert.Equal(OcrCompletionAction.AskAi, saved.OcrAction);
            answer.ShowAnswer(); OcrDesktopTests.RenderIfRequested(answer, "pr32-answer");
            main.Show(); OcrDesktopTests.RenderIfRequested(main, "pr32-settings");
        }
        finally { answer.Close(); main.Close(); Directory.Delete(directory, true); }
    });
    [Fact] public Task OcrConsentRequiresSavedAddressAndAddressChangeClearsBothAutomaticGrants() => OcrDesktopTests.RunSta(async () =>
    {
        var saved = Pr32Tests.OcrOptions with { Provider = TtsProvider.MiMo, AllowAutomaticLlm = true, LlmAuthorizedBaseUrl = Pr32Tests.OcrOptions.LlmBaseUrl };
        MainWindow? main = null;
        main = new(saved, value => { saved = value; main!.SetAutomaticSettings(value); return null; }, () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { });
        try
        {
            var consent = (CheckBox)main.FindName("OcrLlmConsent"); var action = (ComboBox)main.FindName("OcrActionChoice");
            Assert.Equal(3, action.Items.Count); Assert.Equal((int)OcrCompletionAction.AskAi, action.SelectedIndex); Assert.True(consent.IsChecked);
            ((TextBox)main.FindName("LlmBaseUrl")).Text = "https://different.invalid/v1";
            Click(consent, true); Assert.True(saved.AllowsOcrLlm); Assert.Equal(Pr32Tests.OcrOptions.LlmBaseUrl, saved.LlmBaseUrl);
            for (var i = 0; i < 100 && ((ListBox)main.FindName("Outputs")).Items.Count == 0; ++i) await Task.Delay(10);
            AiWindowTests.Descendants<Button>(main).Single(b => Equals(b.Content, "保存设置")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(saved.AllowOcrLlm); Assert.Null(saved.OcrLlmAuthorizedBaseUrl); Assert.False(saved.AllowsAutomaticLlm); Assert.False(consent.IsChecked); Assert.False(saved.AutoAsk);
            Click(consent, true); Assert.True(saved.AllowsOcrLlm); Assert.False(saved.AutoAsk); Assert.False(saved.AllowsAutomaticLlm);
            Click(consent, false); Assert.False(saved.AllowsOcrLlm); Assert.True(QuestionSession.AllowsSource(saved, QuestionSource.Manual));
        }
        finally { main.Close(); }
    });

}
