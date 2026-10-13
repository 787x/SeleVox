using System.Net;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

public sealed class HttpSpeechAudioTests(ITestOutputHelper log)
{
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task BothHttpProtocolResultsUseRealSingleAndMultipleWasapiOutputs()
    {
        using var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), new WaveFormat(24000, 16, 1)))
        {
            var samples = new byte[24000 * 8 * 2];
            for (var i = 0; i < samples.Length / 2; ++i)
                BitConverter.TryWriteBytes(samples.AsSpan(i * 2, 2), (short)(1200 * Math.Sin(2 * Math.PI * 440 * i / 24000)));
            writer.Write(samples, 0, samples.Length);
        }
        var bytes = memory.ToArray();
        var devices = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).OrderBy(d => d.Label.Contains("Realtek") ? 0 : 1).Take(2).ToArray();
        Assert.Equal(2, devices.Length); using var enumerator = new MMDeviceEnumerator();
        foreach (var mimo in new[] { true, false })
        {
            using var http = new HttpClient(new Handler(mimo, bytes));
            async Task<Stream> Synthesize() => mimo ? await new MiMoSpeech(http, () => "fixture").SynthesizeAsync("fixture", new(), default) :
                await new OpenAiSpeech(http, () => "fixture").SynthesizeAsync("fixture", new(), default);
            foreach (var ids in new[] { new[] { Settings.DefaultOutput }, new[] { devices[0].Id }, devices.Select(d => d.Id).ToArray() })
            {
                var opened = new List<WasapiPlayback>();
                using var group = AudioOutputs.CreatePlayback(await Synthesize(), new(Volume: .1f, OutputDeviceIds: ids), (id, audio, volume) =>
                {
                    var device = id == Settings.DefaultOutput ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
                    var player = new WasapiPlayback(audio, volume, device); opened.Add(player); return player;
                });
                Assert.Equal(ids.Length, opened.Count); group.Play(); await Task.Delay(350); group.Pause(); await Task.Delay(150);
                var positions = opened.Select(p => p.SourcePosition).ToArray(); Assert.All(positions, position => Assert.True(position > 0));
                await Task.Delay(250); Assert.Equal(positions, opened.Select(p => p.SourcePosition).ToArray());
                group.Play(); await Task.Delay(350); Assert.All(opened.Select((p, i) => p.SourcePosition > positions[i]), Assert.True); group.Stop();
            }
        }
        log.WriteLine($"Synthetic HTTP audio through both protocols; real single/multiple endpoint pause/resume/stop passed: {string.Join("; ", devices.Select(d => d.Label))}.");
    }
    private sealed class Handler(bool mimo, byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = mimo ? new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(bytes) } } } } })) : new ByteArrayContent(bytes) });
    }
}
