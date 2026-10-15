using System.Windows;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using SeleVox.App;
using SeleVox.Core;
using NAudio.Wave;
using System.IO;

internal static class Program
{
    [STAThread] public static void Main()
    {
        var app = new Application(); var backend = new SpeechAudio();
        using var reading = new ReadingSession(backend, backend);
        var root = "D:/Projects/SeleVox/artifacts/pr12-desktop/fixtures"; Directory.CreateDirectory(root);
        var options = new Settings(Provider: TtsProvider.MiMo, Volume: .05f);
        var speech = new FixtureSpeech();
        var window = new AudioWorkbenchWindow(() => options, () => { }, (audio, token) => reading.PlayAudioAsync(audio.Open, options, token), reading.TogglePause, reading.Stop, Path.Combine(root, "workbench.json"), speech);
        window.Title = "SeleVox · PR12 桌面验收（可控 HTTP；无真实 MiMo 请求）";
        reading.Changed += status => window.Dispatcher.InvokeAsync(() => window.SetPlaybackStatus(status));
        app.Run(window);
        File.WriteAllText(Path.Combine(root, "request-count.txt"), speech.Count.ToString());
    }
    private sealed class FixtureSpeech : ISpeechSynthesis
    {
        internal int Count;
        public async Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
        {
            using var client = new HttpClient(new Handler(() => Interlocked.Increment(ref Count)));
            return await new MiMoSpeech(client, () => "fixture").SynthesizeAsync(text, settings, cancellation);
        }
    }
    private sealed class Handler(Action count) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken token)
        {
            count(); await Task.Delay(300, token);
            using var output = new MemoryStream();
            using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(output), new WaveFormat(24000, 16, 1)))
            {
                var data = new byte[6 * 24000 * 2];
                for (var i = 0; i < data.Length / 2; ++i) BitConverter.TryWriteBytes(data.AsSpan(i * 2, 2), (short)(1200 * Math.Sin(2 * Math.PI * 440 * i / 24000)));
                writer.Write(data, 0, data.Length);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(output.ToArray()) } } } } })) };
        }
    }
}

