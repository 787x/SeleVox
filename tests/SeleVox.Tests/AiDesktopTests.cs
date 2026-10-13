using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class AiDesktopTests(ITestOutputHelper output)
{
    [WindowsOcrFact, Trait("Category", "WindowsQaDesktop")]
    public Task CompletedOcrRemainsAboveRestoredAnswerOnFirstAndRepeatedCapture() => OcrDesktopTests.RunSta(async () =>
    {
        using var session = new QuestionSession((_, _, _, _, _) => Task.FromResult("answer"), (_, _) => Task.CompletedTask, () => { });
        var answer = new AiAnswerWindow(session, () => new(), () => "idle", session.Cancel, () => { }) { Topmost = true };
        OcrResultWindow? result = null;
        var original = Native.GetForegroundWindow();
        try
        {
            answer.ShowAnswer();
            for (var capture = 0; capture < 3; ++capture)
            {
                answer.HideForCapture(); result?.HideForCapture();
                result ??= new OcrResultWindow(new("original", TextSource.ScreenOcr), () => new(), _ => Task.CompletedTask) { Topmost = true };
                SeleVox.App.App.PresentOcrResult(result, answer, new("fresh OCR", TextSource.ScreenOcr));
                // The operation's finally restoration must now be a no-op for the answer.
                answer.RestoreAfterCapture(); result.RestoreAfterCapture(); await Task.Delay(80);
                var answerHandle = new WindowInteropHelper(answer).Handle;
                var below = GetWindow(new WindowInteropHelper(result).Handle, 2); var found = false;
                for (var i = 0; below != 0 && i < 1000; ++i, below = GetWindow(below, 2))
                    if (below == answerHandle) { found = true; break; }
                Assert.True(found, "OCR result must remain above the restored answer in native window z-order.");
                Assert.True(result.IsVisible); Assert.True(answer.IsVisible);
                Assert.Equal("fresh OCR", AiWindowTests.Descendants<TextBox>(result).Single().Text);
            }
        }
        finally { result?.Close(); answer.Close(); if (original != 0) SetForegroundWindow(original); }
    });
    [WindowsOcrFact, Trait("Category", "WindowsQaDesktop")]
    public Task AnswerHiddenDuringRealScreenshotRestoresPlacementWithoutFocus() => OcrDesktopTests.RunSta(async () =>
    {
        using var session = new QuestionSession((_, _, _, _, _) => Task.FromResult("fixed answer"), (_, _) => Task.CompletedTask, () => { });
        await session.AskAsync("fixed question", ChatCompletionTests.Options with { LlmAutoRead = false }, "fake-key");
        var fixture = new Window { Topmost = true, Background = System.Windows.Media.Brushes.CadetBlue, Width = 1000, Height = 750, Left = 50, Top = 50, WindowStartupLocation = WindowStartupLocation.Manual };
        var answer = new AiAnswerWindow(session, () => new(), () => "idle", session.Cancel, () => { })
            { Topmost = true, Width = 660, Height = 480, Left = 120, Top = 120, WindowStartupLocation = WindowStartupLocation.Manual };
        var originalFocus = Native.GetForegroundWindow();
        try
        {
            fixture.Show(); answer.ShowAnswer(); await Task.Delay(100);
            var hwnd = new WindowInteropHelper(answer).Handle;
            Assert.True(SetWindowPos(hwnd, new nint(-1), 120, 120, 660, 480, 0x0040)); await Task.Delay(100);
            var placement = (answer.Left, answer.Top, answer.Width, answer.Height);
            Assert.True(GetWindowRect(hwnd, out var rect));
            var x = (rect.Left + rect.Right) / 2; var y = rect.Bottom - 110;
            var fixtureHwnd = new WindowInteropHelper(fixture).Handle;
            Assert.True(GetWindowRect(fixtureHwnd, out var fixtureRect));
            output.WriteLine($"answer-rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}; fixture-rect={fixtureRect.Left},{fixtureRect.Top},{fixtureRect.Right},{fixtureRect.Bottom}; sample={x},{y}; dpi={GetDpiForWindow(hwnd)}");
            var shownOwner = GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2);
            output.WriteLine($"shown-sample-owner={shownOwner}; answer={hwnd}; fixture={fixtureHwnd}");
            Assert.Equal(hwnd, shownOwner);
            var before = await ScreenSnapshot.CaptureAsync(default); int shown;
            try { var shot = before.Single(s => s.Bounds.Contains(x, y)); shown = shot.Image.GetPixel(x - shot.Bounds.X, y - shot.Bounds.Y).ToArgb(); }
            finally { foreach (var shot in before) shot.Dispose(); }
            output.WriteLine($"shown-pixel={shown:X8}");
            answer.HideForCapture(); answer.HideForCapture();
            var hiddenOwner = GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2);
            output.WriteLine($"hidden-sample-owner={hiddenOwner}; visible={answer.IsVisible}");
            Assert.Equal(fixtureHwnd, hiddenOwner);
            var hidden = await ScreenSnapshot.CaptureAsync(default);
            try { var shot = hidden.Single(s => s.Bounds.Contains(x, y)); var pixel = shot.Image.GetPixel(x - shot.Bounds.X, y - shot.Bounds.Y).ToArgb(); output.WriteLine($"hidden-pixel={pixel:X8}"); Assert.NotEqual(shown, pixel); Assert.Equal(System.Drawing.Color.CadetBlue.ToArgb(), pixel); }
            finally { foreach (var shot in hidden) shot.Dispose(); }
            var focus = Native.GetForegroundWindow(); answer.RestoreAfterCapture(); await Task.Delay(80);
            Assert.True(answer.IsVisible); Assert.Equal(focus, Native.GetForegroundWindow()); Assert.Equal(placement, (answer.Left, answer.Top, answer.Width, answer.Height));
            Assert.Equal(hwnd, new WindowInteropHelper(answer).Handle);
            Assert.Contains(AiWindowTests.Descendants<TextBox>(answer), b => b.Text == "fixed answer");
            answer.WindowState = WindowState.Minimized; await Task.Delay(80); answer.HideForCapture(); answer.RestoreAfterCapture();
            Assert.Equal(WindowState.Minimized, answer.WindowState);
        }
        finally { answer.Close(); fixture.Close(); if (originalFocus != 0) SetForegroundWindow(originalFocus); }
    });
    [WindowsOcrFact, Trait("Category", "WindowsQaDesktop")]
    public Task ExternalUiaSelectionActualAskHotkeyLoopbackStreamAndAnswerWindow() => OcrDesktopTests.RunSta(async () =>
    {
        // Isolated external WPF UIA provider. Never type into, close, or inspect a user's existing document.
        const string text = "What is two plus two?";
        var script = "Add-Type -AssemblyName PresentationFramework; $w = New-Object Windows.Window; $w.Title = 'SeleVox PR8 UIA fixture'; $w.Topmost=$true; $w.Width=650; $w.Height=300; $t=New-Object Windows.Controls.TextBox; $t.Text='What is two plus two?'; $t.FontSize=24; $w.Content=$t; $w.Add_ContentRendered({$t.Focus(); $t.SelectAll(); $w.Activate()}); $w.ShowDialog() | Out-Null";
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-STA", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        var original = Native.GetForegroundWindow(); GetCursorPos(out var cursor);
        using var fixture = Process.Start(info)!;
        using var server = new ChatNetworkTests.Server();
        using var qa = new QuestionSession(new ChatCompletionClient().GenerateAsync, (_, _) => Task.CompletedTask, () => { });
        AiAnswerWindow? answer = null;
        try
        {
            nint hwnd = 0;
            for (var i = 0; i < 100 && hwnd == 0; ++i) { await Task.Delay(50); fixture.Refresh(); hwnd = fixture.MainWindowHandle; }
            Assert.NotEqual(0, hwnd); await Task.Delay(500); fixture.Refresh(); hwnd = fixture.MainWindowHandle;
            Assert.True(SetWindowPos(hwnd, new nint(-1), 200, 150, 650, 300, 0x0040));
            GetWindowRect(hwnd, out var bounds); SetCursorPos(bounds.Left + 120, bounds.Top + 15);
            mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0);
            await Task.Delay(150); SetForegroundWindow(hwnd); await Task.Delay(100); Assert.Equal(hwnd, Native.GetForegroundWindow());
            using var hotkeys = new Hotkeys(); Assert.DoesNotContain(hotkeys.Conflicts, c => c.StartsWith("Ctrl+Alt+A"));
            Assert.Equal(hwnd, Native.GetForegroundWindow()); // Constructing the receiver must not activate it.
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var capture = new SelectionCapture();
            var presses = 0;
            hotkeys.Pressed += async (id, foreground) =>
            {
                if (id != 5) return;
                try
                {
                    Native.GetWindowThreadProcessId(foreground, out var foregroundPid);
                    output.WriteLine($"ask-hotkey-press={++presses}; foreground={foreground}; pid={foregroundPid}; fixture={hwnd}; fixture-pid={fixture.Id}");
                    Assert.Equal(hwnd, foreground); var selected = await capture.CaptureTextAsync(foreground, default); Assert.Equal(text, selected.Text);
                    var running = qa.AskAsync(selected.Text, ChatCompletionTests.Options with { LlmBaseUrl = server.Address, LlmAutoRead = false }, "fake-key");
                    answer = new AiAnswerWindow(qa, () => new(), () => "idle", qa.Cancel, () => { }); answer.ShowAnswer();
                    await running; answer.Refresh(); Assert.Equal(QuestionState.Completed, qa.Snapshot.State); Assert.Equal("Four.", qa.Snapshot.Answer); done.SetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
            };
            var serving = Task.Run(async () =>
            {
                var context = await server.Next();
                using var reader = new StreamReader(context.Request.InputStream); using var body = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
                Assert.Equal(text, body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
                context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
                await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Event("Four.")); await Task.Delay(150);
                await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Finish); context.Response.Close();
            });
            var readyForeground = Native.GetForegroundWindow(); Native.GetWindowThreadProcessId(readyForeground, out var readyPid);
            output.WriteLine($"before-send-foreground={readyForeground}; pid={readyPid}; expected={hwnd}; fixture-pid={fixture.Id}");
            Assert.Equal(hwnd, readyForeground);
            SendHotkey(0x41); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); await serving;
            OcrDesktopTests.RenderIfRequested(answer!, "pr8-uia-loopback");
        }
        finally { answer?.Close(); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } SetCursorPos(cursor.X, cursor.Y); if (original != 0) SetForegroundWindow(original); }
    });
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task CompleteAnswerUsesRealLocalTtsSingleAndDualOutputsPauseResumeStop()
    {
        var speech = new SpeechAudio(); using var reading = new ReadingSession(speech, speech);
        using var qa = new QuestionSession((_, _, _, delta, _) => { delta("fixture"); return Task.FromResult("This is a fixed answer for a local speech test. Pause, resume and stop preserve the answer text."); },
            (text, options) => reading.ReadCapturedTextAsync(new(text, TextSource.Selection), options), reading.Stop);
        var devices = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).Take(2).Select(d => d.Id).ToArray(); Assert.Equal(2, devices.Length);
        foreach (var outputs in new[] { new[] { Settings.DefaultOutput }, devices })
        {
            var settings = ChatCompletionTests.Options with { VoiceId = SpeechAudio.GetVoices().First().Id, Volume = .1f, OutputDeviceIds = outputs };
            await qa.AskAsync("fixed question", settings, "fake-key").WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(ReadingState.Playing, reading.Status.State); reading.TogglePause(); Assert.Equal(ReadingState.Paused, reading.Status.State);
            await Task.Delay(100); reading.TogglePause(); Assert.Equal(ReadingState.Playing, reading.Status.State);
            qa.Cancel(); Assert.Equal(ReadingState.Idle, reading.Status.State); Assert.Equal(QuestionState.Completed, qa.Snapshot.State); Assert.NotEmpty(qa.Snapshot.Answer);
        }
    }
    [WindowsOcrFact, Trait("Category", "WindowsQaDesktop")]
    public Task RealLocalOcrEditedQuestionUsesLoopbackLlmAndRetainsOcrWindow() => OcrDesktopTests.RunSta(async () =>
    {
        using var image = OcrTests.Fixture("English screen capture\nSecond line text", "Arial");
        var captured = await new LocalOcr().RecognizeAsync(image, OcrLanguage.English, default);
        using var server = new ChatNetworkTests.Server();
        var options = ChatCompletionTests.Options with { LlmBaseUrl = server.Address, LlmSystemPrompt = "updated prompt", LlmAutoRead = false };
        using var qa = new QuestionSession(new ChatCompletionClient().GenerateAsync, (_, _) => Task.CompletedTask, () => { });
        Task? running = null; var reads = 0;
        var ocr = new OcrResultWindow(captured, () => options, _ => { ++reads; return Task.CompletedTask; }, text => running = qa.AskAsync(text, options, "fake-key"));
        var answer = new AiAnswerWindow(qa, () => options, () => "idle", qa.Cancel, () => { });
        try
        {
            ocr.ShowResult(captured); AiWindowTests.Descendants<TextBox>(ocr).Single().Text = "edited question";
            var serving = Task.Run(async () =>
            {
                var context = await server.Next(); using var reader = new StreamReader(context.Request.InputStream);
                using var json = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync()); var messages = json.RootElement.GetProperty("messages");
                Assert.Equal("updated prompt", messages[0].GetProperty("content").GetString()); Assert.Equal("edited question", messages[1].GetProperty("content").GetString());
                context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
                await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Event("fixed answer") + ChatCompletionTests.Finish); context.Response.Close();
            });
            AiWindowTests.Descendants<Button>(ocr).Single(b => b.Content as string == "问 AI").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.NotNull(running); answer.ShowAnswer(); await running; await serving; answer.Refresh();
            Assert.True(ocr.IsVisible); Assert.Equal("edited question", qa.Snapshot.Question); Assert.Equal("fixed answer", qa.Snapshot.Answer);
            Assert.Equal(QuestionState.Completed, qa.Snapshot.State); Assert.Equal(0, reads);
        }
        finally { answer.Close(); ocr.Close(); }
    });
    private static void SendHotkey(byte key)
    {
        keybd_event(0x11, 0, 0, 0); keybd_event(0x12, 0, 0, 0); keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0); keybd_event(0x12, 0, 2, 0); keybd_event(0x11, 0, 2, 0);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
}
