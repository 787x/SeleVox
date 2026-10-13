using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class Pr9WindowsTests(ITestOutputHelper log)
{
    [Fact] public Task PromptDraftManagementThinkingAndPermissionUi() => OcrDesktopTests.RunSta(async () =>
    {
        var saved = new Settings(Provider: TtsProvider.MiMo, LlmModel: "fixture", LlmSystemPrompt: "旧自由 Prompt", StartupSilenceMilliseconds: 150); MainWindow? window = null;
        window = new MainWindow(saved, value => { saved = value; window!.SetAutomaticSettings(value); return null; }, () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { });
        try
        {
            var prompt = (TextBox)window.FindName("LlmSystemPrompt"); Assert.Equal("旧自由 Prompt", prompt.Text);
            var startupSilence = (Slider)window.FindName("StartupSilence"); Assert.Equal(150, startupSilence.Value);
            startupSilence.Value = 250;
            var presets = (ComboBox)window.FindName("PromptChoice"); presets.SelectedIndex = 1;
            Assert.Equal(PromptPresets.BuiltIn[1].Prompt, prompt.Text); Assert.Equal("旧自由 Prompt", saved.LlmSystemPrompt);
            prompt.Text = "编辑后的自由 Prompt"; ((TextBox)window.FindName("PromptName")).Text = "自定义";
            Click(window, "保存为自定义预设"); Assert.Null(saved.CustomPrompts);
            ((ComboBox)window.FindName("ThinkingProtocolChoice")).SelectedIndex = 2;
            ((ComboBox)window.FindName("ThinkingOptionChoice")).SelectedValue = ThinkingOption.Enabled;
            await Pr9Tests.Until(() => ((ListBox)window.FindName("Outputs")).Items.Count > 0);
            Click(window, "保存设置"); Assert.Equal("编辑后的自由 Prompt", saved.LlmSystemPrompt); Assert.Single(saved.CustomPrompts!);
            Assert.Equal(250, saved.StartupSilenceMilliseconds); startupSilence.Value = 0;
            Assert.Equal(ThinkingProtocol.MiMo, saved.LlmThinkingProtocol); Assert.Equal(ThinkingOption.Enabled, saved.LlmThinking);
            Click(window, "删除所选自定义预设"); Assert.Single(saved.CustomPrompts!); Click(window, "保存设置"); Assert.Empty(saved.CustomPrompts!);
            Assert.Equal("编辑后的自由 Prompt", saved.LlmSystemPrompt); Assert.Equal(4, presets.Items.Count);
            Assert.Equal(0, saved.StartupSilenceMilliseconds);
            var consent = (CheckBox)window.FindName("AutomaticLlm"); consent.IsChecked = true; consent.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(saved.AllowsAutomaticLlm); Assert.False(saved.AutoAsk);
            ((TextBox)window.FindName("LlmBaseUrl")).Text = "https://different.invalid/v1"; Click(window, "保存设置");
            Assert.False(saved.AllowsAutomaticLlm); Assert.Null(saved.LlmAuthorizedBaseUrl);
        }
        finally { window.Close(); }
    });
    [WindowsOcrFact, Trait("Category", "WindowsQaDesktop")]
    public Task AutomaticAnswerFirstShowAndUpdateDoNotStealFocus() => OcrDesktopTests.RunSta(async () =>
    {
        var fixture = new Window { Title = "PR9 focus fixture", Width = 450, Height = 250 };
        using var qa = new QuestionSession((_, _, _, _, _) => Task.FromResult("fixture"), (_, _) => Task.CompletedTask, () => { });
        var answer = new AiAnswerWindow(qa, () => new(), () => "idle", qa.Cancel, () => { });
        var original = Native.GetForegroundWindow();
        try
        {
            fixture.Show(); fixture.Activate(); await Task.Delay(100); var foreground = Native.GetForegroundWindow();
            answer.ShowAnswer(false); await Task.Delay(100); Assert.Equal(foreground, Native.GetForegroundWindow());
            answer.ShowAnswer(false); await Task.Delay(100); Assert.Equal(foreground, Native.GetForegroundWindow());
            answer.HideForCapture(); answer.RestoreAfterCapture(); await Task.Delay(100); Assert.Equal(foreground, Native.GetForegroundWindow());
        }
        finally { answer.Close(); fixture.Close(); if (original != 0) SetForegroundWindow(original); }
    });
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task DelayedDoneSseConsumesActualAudioBeforeGenerationEndsSingleAndDual()
    {
        var devices = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).Take(2).Select(d => d.Id).ToArray();
        Assert.Equal(2, devices.Length);
        foreach (var outputs in new[] { new[] { Settings.DefaultOutput }, devices })
        {
            using var server = new ChatNetworkTests.Server(); var release = Pr9Tests.Signal();
            var clock = Stopwatch.StartNew(); long firstDelta = 0, sentence = 0, consumed = 0, generated = 0;
            var backend = new MeasuredAudio(); using var reading = new ReadingSession(backend, backend);
            var client = new ChatCompletionClient();
            using var qa = new QuestionSession(async (text, options, key, delta, token) =>
            {
                var result = await client.GenerateAsync(text, options, key, d => { if (firstDelta == 0) firstDelta = clock.ElapsedMilliseconds; delta(d); }, token);
                generated = clock.ElapsedMilliseconds; return result;
            }, (_, _) => throw new Exception(), reading.Stop, (text, options, token) => { if (sentence == 0) sentence = clock.ElapsedMilliseconds; return reading.ReadSentenceAsync(text, options, token); });
            var serving = Task.Run(async () =>
            {
                var context = await server.Next(); context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
                await ChatNetworkTests.Server.Write(context.Response, "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"never spoken\"},\"finish_reason\":null}]}\n\n");
                await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Event("This is the first fixed sentence. "));
                await release.Task;
                await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Event("This is the last fixed sentence."));
                await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Finish); context.Response.Close();
            });
            var options = Pr9Tests.Incremental with { LlmBaseUrl = server.Address, VoiceId = SpeechAudio.GetVoices().First().Id, Volume = .08f, OutputDeviceIds = outputs };
            var running = qa.AskAsync("fixed fixture", options, "fake");
            try
            {
                await Pr9Tests.Until(() => backend.Players.Any(p => p.SourcePosition > 0)); consumed = clock.ElapsedMilliseconds;
                Assert.False(running.IsCompleted); Assert.Equal(0, generated); Assert.Single(backend.Text);
                reading.TogglePause(); Assert.Equal(ReadingState.Paused, reading.Status.State); await Task.Delay(120);
                var position = backend.Players.First().SourcePosition; await Task.Delay(120); Assert.Equal(position, backend.Players.First().SourcePosition);
                reading.TogglePause(); release.TrySetResult();
                await running.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(QuestionState.Completed, qa.Snapshot.State);
                Assert.Equal(2, backend.Text.Count); Assert.DoesNotContain(backend.Text, t => t.Contains("never spoken"));
                log.WriteLine($"outputs={outputs.Length}; request=0ms; first-content={firstDelta}ms; first-sentence={sentence}ms; first-audio-consumption={consumed}ms; generated={generated}ms; audio-ended={clock.ElapsedMilliseconds}ms");
            }
            finally { release.TrySetResult(); qa.Cancel(); await serving; }
        }
    }
    [WindowsOcrFact, Trait("Category", "WindowsQaDesktop")]
    public Task ExternalMouseSelectionAutomaticallyAsksOnceWithoutFocusChange() => OcrDesktopTests.RunSta(async () =>
    {
        const string script = "Add-Type -AssemblyName PresentationFramework; $w=New-Object Windows.Window; $w.Title='PR9 isolated UIA fixture'; $w.Width=650; $w.Height=300; $w.Topmost=$true; $t=New-Object Windows.Controls.TextBox; $t.Text='What is two plus two?'; $t.FontSize=24; $w.Content=$t; $w.ShowDialog() | Out-Null";
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-STA", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        var original = Native.GetForegroundWindow(); GetCursorPos(out var cursor);
        using var fixture = Process.Start(info)!; using var server = new ChatNetworkTests.Server();
        using var qa = new QuestionSession(new ChatCompletionClient().GenerateAsync, (_, _) => Task.CompletedTask, () => { });
        using var selection = new AutomaticSelection(); var capture = new SelectionCapture(); var done = Pr9Tests.Signal();
        AiAnswerWindow? answer = null; var calls = 0;
        try
        {
            nint hwnd = 0;
            for (var i = 0; i < 100 && hwnd == 0; i++) { await Task.Delay(50); fixture.Refresh(); hwnd = fixture.MainWindowHandle; }
            Assert.NotEqual(0, hwnd); Assert.True(SetWindowPos(hwnd, new nint(-1), 200, 150, 650, 300, 0x0040));
            SetCursorPos(300, 165); mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0);
            await Task.Delay(200); SetForegroundWindow(hwnd); await Task.Delay(150); Assert.Equal(hwnd, Native.GetForegroundWindow());
            var path = SeleVox.App.ApplicationIdentity.FromWindow(hwnd); Assert.NotNull(path);
            var bounds = await Task.Run(() => System.Windows.Automation.AutomationElement.FromHandle(hwnd).FindFirst(
                System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Edit)).Current.BoundingRectangle);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            var options = ChatCompletionTests.Options with { LlmBaseUrl = server.Address, AutoAsk = true, AllowAutomaticLlm = true,
                LlmAuthorizedBaseUrl = server.Address, Applications = [new(path!, true, true)], LlmAutoRead = false };
            using var monitor = new AutomaticMouseMonitor();
            monitor.Invalidated += () => selection.Invalidate();
            monitor.Selected += async (window, start, end) =>
            {
                try
                {
                    if (window != hwnd || !options.AllowsAutomaticAsk(SeleVox.App.ApplicationIdentity.FromWindow(window))) return;
                    await selection.RequestAsync(path! + "|" + hwnd, async token =>
                        (await StableSelection.CaptureAsync((focus, sample) => capture.CaptureSnapshotAsync(hwnd, sample, start, end, focus), token)).Text,
                        () => Native.GetForegroundWindow() == hwnd, text =>
                        {
                            Assert.True(options.AllowsAutomaticLlm); ChatCompletionClient.Validate(text, options, "fake");
                            Interlocked.Increment(ref calls); var task = qa.AskAsync(text, options, "fake", true);
                            answer ??= new AiAnswerWindow(qa, () => options, () => "idle", qa.Cancel, () => { }); answer.ShowAnswer(false);
                            _ = task.ContinueWith(_ => done.TrySetResult());
                        }, message => done.TrySetException(new Exception(message)));
                }
                catch (Exception ex) { done.TrySetException(ex); }
            };
            var serving = Task.Run(async () =>
            {
                var context = await server.Next(); context.Response.ContentType = "text/event-stream";
                await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Event("Four.") + ChatCompletionTests.Finish); context.Response.Close();
            });
            async Task Drag()
            {
                var left = (int)(bounds.Left + 5 * scale); var top = (int)(bounds.Top + 15 * scale);
                Assert.Equal(hwnd, Native.GetForegroundWindow());
                Assert.True(SetCursorPos(left, top));
                Assert.Equal(hwnd, GetAncestor(WindowFromPoint(new CursorPoint { X = left, Y = top }), 2));
                mouse_event(2, 0, 0, 0, 0);
                try
                {
                    for (var x = left + 10; x <= left + 270 * scale; x += 10) { Assert.True(SetCursorPos(x, top)); await Task.Delay(8); }
                }
                finally { mouse_event(4, 0, 0, 0, 0); }
            }
            await Drag(); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); await serving;
            Assert.Equal(QuestionState.Completed, qa.Snapshot.State); Assert.Equal(hwnd, Native.GetForegroundWindow()); Assert.Equal(1, calls);
            await Drag(); await Task.Delay(600); Assert.Equal(1, calls);
            SetCursorPos(500, 250); mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0); await Task.Delay(250); Assert.Equal(1, calls);
        }
        finally { answer?.Close(); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } SetCursorPos(cursor.X, cursor.Y); if (original != 0) SetForegroundWindow(original); }
    });
    private sealed class MeasuredAudio : ISpeechSynthesis, IAudioPlaybackFactory
    {
        private readonly SpeechAudio speech = new();
        internal ConcurrentQueue<WasapiPlayback> Players = new(); internal ConcurrentQueue<string> Text = new();
        public Task<Stream> SynthesizeAsync(string text, Settings options, CancellationToken token) { Text.Enqueue(text); return speech.SynthesizeAsync(text, options, token); }
        public IAudioPlayback CreatePlayback(Stream audio, Settings options)
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var ids = options.OutputDeviceIds ?? [Settings.DefaultOutput];
            var bytes = new MemoryStream(); audio.CopyTo(bytes); audio.Dispose(); var data = bytes.ToArray(); bytes.Dispose();
            var players = ids.Select(id => new WasapiPlayback(new MemoryStream(data), options.Volume, id == Settings.DefaultOutput ? null : enumerator.GetDevice(id))).ToArray();
            foreach (var p in players) Players.Enqueue(p);
            return new MultiOutputPlayback(players);
        }
    }
    private static void Click(Window w, string name) => AiWindowTests.Descendants<Button>(w).Single(b => Equals(b.Content, name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { internal int X, Y; }
}

