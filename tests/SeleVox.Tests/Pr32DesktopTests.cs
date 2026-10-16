using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;
using Drawing = System.Drawing;

namespace SeleVox.Tests;

public sealed class Pr32DesktopFactAttribute : FactAttribute
{
    public Pr32DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_OCR_DESKTOP_TESTS") != "1" || Environment.GetEnvironmentVariable("SELEVOX_AUDIO_TESTS") != "1")
            Skip = "Requires interactive desktop and explicit fixture audio opt-ins.";
    }
}
[Collection("WPF desktop")]
public sealed class Pr32DesktopTests(ITestOutputHelper output)
{
    [Pr32DesktopFact, Trait("Category", "WindowsOcr")]
    public Task ScreenOcrAutomaticWindowsSpeechControlledHttpAndQuickToggle() => OcrDesktopTests.RunSta(async () =>
    {
        var originalFocus = Native.GetForegroundWindow();
        using var fixture = OcrTests.Fixture("Automatic OCR reading fixture.\nThis fixed paragraph is used to check current speech.", "Arial");
        var source = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
            Content = new Image { Source = SelectionOverlay.ToSource(fixture), Stretch = System.Windows.Media.Stretch.Fill } };
        OcrResultWindow? resultWindow = null; AiAnswerWindow? answerWindow = null;
        var speech = new SpeechAudio(); using var reading = new ReadingSession(speech, speech);
        using var server = new ChatNetworkTests.Server();
        var voice = SpeechAudio.GetVoices().FirstOrDefault(v => v.Label.Contains("(en-")) ?? SpeechAudio.GetVoices().First();
        var saved = new Settings(VoiceId: voice.Id, Volume: .08f, OcrLanguage: OcrLanguage.English,
            LlmBaseUrl: server.Address, LlmModel: "controlled-fixture", AutoAsk: false, LlmAutoRead: true,
            LlmReadingStrategy: AnswerReadingStrategy.Incremental) { OcrAction = OcrCompletionAction.Read, AllowOcrLlm = true, OcrLlmAuthorizedBaseUrl = server.Address };
        using var questions = new QuestionSession(new ChatCompletionClient().GenerateAsync,
            (t, s) => reading.ReadSentenceAsync(t, s, default), reading.Stop, reading.ReadSentenceAsync);
        try
        {
            source.Show(); var handle = new WindowInteropHelper(source).Handle;
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            Assert.True(SetWindowPos(handle, new nint(-1), screen.X + 35, screen.Y + 35, fixture.Width, fixture.Height, 0x0040)); source.Activate();
            await Task.Delay(200); GetWindowRect(handle, out var rect);
            var snapshots = await ScreenSnapshot.CaptureAsync(default); CapturedText? recognized = null;
            try
            {
                var snapshot = snapshots.Single(s => s.Bounds.Contains(new Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
                using var crop = snapshot.Image.Clone(new Drawing.Rectangle(rect.Left - snapshot.Bounds.X, rect.Top - snapshot.Bounds.Y, rect.Right - rect.Left, rect.Bottom - rect.Top), Drawing.Imaging.PixelFormat.Format32bppRgb);
                using var capture = new TextCaptureOperation();
                await capture.RunAsync(token => new LocalOcr().RecognizeAsync(crop, OcrLanguage.English, token), text => recognized = text, message => Assert.Fail(message));
                Assert.False(capture.IsActive); Assert.NotNull(recognized); Assert.Contains("Automatic OCR", recognized.Text);
            }
            finally { foreach (var snapshot in snapshots) snapshot.Dispose(); source.Close(); }
            resultWindow = new(recognized!, () => saved, t => reading.ReadCapturedTextAsync(t, saved));
            resultWindow.ShowResult(recognized!);
            using var readRun = new OcrFollowUp(saved); Task? started = null;
            await readRun.RunAsync(recognized!, () => true,
                (t, s, token) => Task.Run(() => SpeechAudio.PrecheckAutomatic(t, s), token),
                (t, s, token) => started = reading.ReadAsync(_ => Task.FromResult(t), s, true, cancellation: token), _ => Assert.Fail());
            await started!.WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(ReadingState.Playing, reading.Status.State);
            answerWindow = new(questions, () => saved, () => reading.Status.Message, questions.Cancel, () => { },
                saveAutoRead: enabled => { saved = saved with { LlmAutoRead = enabled }; return null; });
            var beforeAnswer = Native.GetForegroundWindow();
            answerWindow.ShowAnswer(false); Assert.Equal(beforeAnswer, Native.GetForegroundWindow());
            var toggle = AiWindowTests.Descendants<CheckBox>(answerWindow).Single();
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(saved.LlmAutoRead); Assert.Equal(ReadingState.Playing, reading.Status.State);
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(saved.LlmAutoRead); Assert.Equal(ReadingState.Playing, reading.Status.State);
            output.WriteLine("Real desktop screenshot -> local OCR -> Windows TTS/WASAPI playing; answer shortcut left current playback running.");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
            var serving = Task.Run(async () =>
            {
                var context = await server.Next(); ++count;
                using var json = await JsonDocument.ParseAsync(context.Request.InputStream);
                Assert.Equal(recognized!.Text, json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
                Assert.Equal("controlled-fixture", json.RootElement.GetProperty("model").GetString());
                context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
                await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Event("First fixture sentence. "));
                await release.Task;
                await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Event("Second fixture sentence.") + ChatCompletionTests.Finish); context.Response.Close();
            });
            saved = saved with { OcrAction = OcrCompletionAction.AskAi };
            using var askRun = new OcrFollowUp(saved); Task? question = null;
            try
            {
                await askRun.RunAsync(recognized!, () => true, (_, _, _) => Task.CompletedTask, (_, _, _) => Assert.Fail(),
                    text => question = questions.AskAsync(text, saved, "controlled-fixture-key", QuestionSource.OcrAutomatic));
                await Pr9Tests.Until(() => questions.Snapshot.Answer.Contains("First fixture sentence."));
                answerWindow.Refresh(); Assert.False(question!.IsCompleted); Assert.False(saved.AutoAsk);
                release.TrySetResult(); await question.WaitAsync(TimeSpan.FromSeconds(20)); await serving;
                Assert.Equal(1, count); Assert.True(questions.Snapshot.AnswerComplete); Assert.Equal(QuestionState.Completed, questions.Snapshot.State);
                Assert.Equal(QuestionSource.OcrAutomatic, questions.Source); Assert.Equal(ReadingState.Idle, reading.Status.State);
                output.WriteLine("OCR source submitted once with Auto Ask off to a controlled loopback HTTP/SSE fixture; streamed text displayed and sentence speech completed on real Windows audio.");
                OcrDesktopTests.RenderIfRequested(answerWindow, "pr32-desktop-answer");
            }
            finally { release.TrySetResult(); questions.Cancel(); }
        }
        finally { reading.Stop(); answerWindow?.Close(); resultWindow?.Close(); source.Close(); SetForegroundWindow(originalFocus); }
    });
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
}
