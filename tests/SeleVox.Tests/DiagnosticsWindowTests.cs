using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class DiagnosticsWindowTests
{
    private static T[] Children<T>(DependencyObject w) where T : DependencyObject
    {
        var result = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(w); ++i)
        { var child = VisualTreeHelper.GetChild(w, i); if (child is T match) result.Add(match); result.AddRange(Children<T>(child)); }
        return result.ToArray();
    }
    private static Button Run(Window w, int index = 0) => Children<Button>(w).Where(b => b.Content.ToString()!.StartsWith("检测")).ElementAt(index);
    private static Button Cancel(Window w, int index = 0) => Children<Button>(w).Where(b => b.Content.ToString() == "取消").ElementAt(index);
    private static bool Text(Window w, string text) => Children<TextBlock>(w).Any(b => b.Text.Contains(text));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Wait(Func<bool> condition)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, budget.Token);
    }
    [Fact] public Task RealWindowEntrySuccessDirtySettingsCredentialsCancelAndClose() => OcrDesktopTests.RunSta(async () =>
    {
        var settings = ProviderDiagnosticsTests.Snapshot(DiagnosticService.Windows).Settings;
        var dirty = false; var key = "fixture"; var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diag = ProviderDiagnosticsTests.Fake(async (_, _, _) => { ++calls; if (calls > 1) { entered.TrySetResult(); await release.Task; } return new MemoryStream(AudioGenerationTests.Wave()); });
        var w = new DiagnosticsWindow(() => settings, _ => dirty, diag, (_, _) => key);
        try
        {
            w.Show(); Assert.Equal(0, calls); Assert.True(Text(w, "配置已就绪")); Assert.True(Text(w, "未检测"));
            Click(Run(w)); await Wait(() => Text(w, "成功 ·")); Assert.Equal(1, calls);
            dirty = true; w.Refresh(); Assert.False(Run(w).IsEnabled); Assert.True(Text(w, "结果过期"));
            dirty = false; w.Refresh(); Click(Run(w)); await entered.Task; Assert.True(Cancel(w).IsEnabled);
            Assert.All(Children<Button>(w).Where(b => b.Content.ToString()!.StartsWith("检测")), b => Assert.False(b.IsEnabled));
            settings = settings with { Rate = 1.1 }; w.Refresh(); release.TrySetResult(); await Wait(() => Run(w).IsEnabled);
            Assert.True(Text(w, "结果过期")); Assert.False(Text(w, "成功 ·"));
            Click(Run(w)); await Wait(() => Text(w, "成功 ·")); key = "new-key"; w.Refresh(); Assert.True(Text(w, "结果过期"));
            Click(Run(w)); await Wait(() => Text(w, "成功 ·")); w.CredentialsChanged(); Assert.True(Text(w, "结果过期"));
            Render(w);
        }
        finally { release.TrySetResult(); w.Close(); }
        // A closed window drops a provider completion even if the provider ignores cancellation.
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        diag = ProviderDiagnosticsTests.Fake(async (_, _, _) => { entered.TrySetResult(); await release.Task; return new MemoryStream(AudioGenerationTests.Wave()); });
        w = new(() => settings, _ => false, diag, (_, _) => key);
        w.Show(); Click(Run(w)); await entered.Task; Click(Cancel(w)); release.TrySetResult(); await Wait(() => Text(w, "取消 ·")); w.Close();
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        w = new(() => settings, _ => false, diag, (_, _) => key); w.Show(); Click(Run(w)); await entered.Task;
        w.Close(); release.TrySetResult(); await Task.Delay(100); Assert.False(Text(w, "成功 ·"));
    });
    [Fact] public Task MainEntryUsesSavedSnapshotAndDraftsDoNotChangeSavedProvider() => OcrDesktopTests.RunSta(() =>
    {
        var s = new Settings(); MainWindow? w = null;
        w = new(s, updated => { s = updated; w!.SetAutomaticSettings(updated); return null; }, () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { });
        try
        {
            w.Show(); Assert.False(w.HasUnsavedDiagnosticSettings(DiagnosticService.OpenAiTts));
            ((TextBox)w.FindName("OpenAiModel")).Text = "unsaved-model";
            Assert.True(w.HasUnsavedDiagnosticSettings(DiagnosticService.OpenAiTts));
            ((TextBox)w.FindName("LlmModel")).Text = "draft"; Assert.True(w.HasUnsavedDiagnosticSettings(DiagnosticService.Llm));
            var entry = Children<Button>(w).Single(b => b.Content.ToString() == "服务检测 · TTS / LLM"); Click(entry);
            Assert.Equal(TtsProvider.Windows, s.Provider); Assert.Single(w.OwnedWindows.Cast<Window>());
            Assert.True(Text(w.OwnedWindows.Cast<Window>().Single(), "尚未保存"));
            w.Close(); Assert.Empty(w.OwnedWindows.Cast<Window>());
        }
        finally { w.Close(); }
        return Task.CompletedTask;
    });
    [Fact] public async Task LocalDiagnosticDuringRealPlaybackDoesNotReplaceOrStopReading()
    {
        var voices = SpeechAudio.GetVoices(); Assert.NotEmpty(voices); var speech = new SpeechAudio();
        using var reading = new ReadingSession(speech, speech);
        var settings = new Settings(VoiceId: voices[0].Id, Volume: .1f);
        await reading.ReadCapturedTextAsync(new(string.Concat(Enumerable.Repeat("Hello, this is a playback session. ", 12)), TextSource.Selection), settings);
        Assert.Equal(ReadingState.Playing, reading.Status.State);
        var result = await new ProviderDiagnostics().RunAsync(DiagnosticSnapshot.Create(DiagnosticService.Windows, settings, null), default);
        Assert.Equal(DiagnosticState.Succeeded, result.State); Assert.Equal(ReadingState.Playing, reading.Status.State);
        reading.TogglePause(); Assert.Equal(ReadingState.Paused, reading.Status.State); reading.Stop();
    }
    private static void Render(Window w)
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_UI_RENDER_DIR") is not { Length: > 0 } directory) return;
        w.UpdateLayout(); Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(w);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, "pr15-diagnostics.png")); encoder.Save(file);
    }
}
