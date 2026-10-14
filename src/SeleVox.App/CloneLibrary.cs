using System.Text.Json;
using NAudio.Wave;
using SeleVox.Core;

namespace SeleVox.App;

internal enum CloneSource { VoiceDesign, Upload, Recording }
internal sealed record CloneSample(Guid Id, string Name, CloneSource Source, string FilePath, DateTimeOffset Created)
{
    public string Label => $"{Name} · {Source} · {Created.ToLocalTime():yyyy-MM-dd HH:mm}";
}
internal sealed record CloneAudio(byte[] Bytes, string Extension)
{
    // Official limit is on Base64, so raw files are bounded more conservatively.
    internal const int MaxEncodedBytes = 10 * 1024 * 1024;
    internal const int MaxFileBytes = (MaxEncodedBytes - 32) / 4 * 3;
    internal string DataUri => "data:" + (Extension == ".wav" ? "audio/wav" : "audio/mpeg") + ";base64," + Convert.ToBase64String(Bytes);
    internal static async Task<CloneAudio> ReadAsync(string path, CancellationToken cancellation)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".wav" or ".mp3")) throw new UserFeedbackException("克隆样本仅支持 WAV / MP3。");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > MaxFileBytes) throw new UserFeedbackException("样本为空或过大；文件须小于约 7.5 MiB（Base64 不超过 10 MiB）。");
            var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Validate(bytes, extension, cancellation); return new(bytes, extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new UserFeedbackException("无法读取克隆样本，请重新选择可访问的本地文件。"); }
    }
    internal static void Validate(byte[] bytes, string extension, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (bytes.Length is <= 0 or > MaxFileBytes) throw new UserFeedbackException("克隆样本为空或超过 Base64 10 MiB 上限。");
        try
        {
            using WaveStream audio = extension switch {
                ".wav" => HttpSpeech.ValidatedWave(bytes, cancellation),
                ".mp3" => new Mp3FileReaderBase(new MemoryStream(bytes, false), f => new AcmMp3FrameDecompressor(f)),
                _ => throw new UserFeedbackException("克隆样本仅支持 WAV / MP3。") };
            if (audio.TotalTime.TotalSeconds is < 5 or > 30) throw new UserFeedbackException($"克隆样本时长须为 5–30 秒（当前 {audio.TotalTime.TotalSeconds:0.00} 秒），请录制或选择合适的样本。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or FormatException or System.Runtime.InteropServices.COMException)
        { throw new UserFeedbackException("样本音频格式无效或无法解码，请选择有效 WAV / MP3。"); }
    }
    internal GeneratedAudio Preview(CancellationToken cancellation)
    {
        if (Extension == ".wav") return GeneratedAudio.Create(Bytes, new(), cancellation);
        using var mp3 = new Mp3FileReaderBase(new MemoryStream(Bytes, false), f => new AcmMp3FrameDecompressor(f));
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(output), mp3.WaveFormat))
        {
            var buffer = new byte[16384]; int read;
            while ((read = mp3.Read(buffer, 0, buffer.Length)) > 0) { cancellation.ThrowIfCancellationRequested(); writer.Write(buffer, 0, read); }
        }
        return GeneratedAudio.Create(output.ToArray(), new(), cancellation);
    }
}

// Assets are copied into this owned directory; deletion never touches the original upload.
internal sealed class CloneLibrary
{
    private readonly string root, index;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal CloneLibrary(string directory) { root = Path.GetFullPath(directory); index = Path.Combine(root, "samples.json"); }
    internal IReadOnlyList<CloneSample> Load()
    {
        if (!File.Exists(index)) return [];
        try
        {
            var samples = JsonSerializer.Deserialize<CloneSample[]>(File.ReadAllText(index)) ?? throw new JsonException();
            if (samples.Length > 1000 || samples.Any(s => s is null || !Enum.IsDefined(s.Source) || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 80 || !Owned(s)) || samples.Select(s => s.Id).Distinct().Count() != samples.Length)
                throw new JsonException();
            return samples;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw new UserFeedbackException("本地样本库无法读取；请检查样本目录或元数据。未覆盖已有资产。"); }
    }
    private bool Owned(CloneSample sample) => sample.FilePath == Path.Combine(root, sample.Id.ToString("N") + Path.GetExtension(sample.FilePath)) && Path.GetExtension(sample.FilePath) is ".wav" or ".mp3";
    private void Save(IEnumerable<CloneSample> samples)
    {
        var temp = index + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(samples)); File.Move(temp, index, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal async Task<CloneSample> AddAsync(string name, CloneSource source, CloneAudio audio, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl) || !Enum.IsDefined(source)) throw new UserFeedbackException("请填写 1–80 字符的样本名称。");
        CloneAudio.Validate(audio.Bytes, audio.Extension, cancellation);
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        string? path = null; bool committed = false;
        try
        {
            var samples = Load(); if (samples.Count >= 1000) throw new UserFeedbackException("样本库已满，请先删除不再使用的样本。");
            Directory.CreateDirectory(root); var id = Guid.NewGuid(); path = Path.Combine(root, id.ToString("N") + audio.Extension);
            var sample = new CloneSample(id, name.Trim(), source, path, DateTimeOffset.UtcNow);
            await File.WriteAllBytesAsync(path, audio.Bytes, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); Save(samples.Append(sample)); committed = true; return sample;
        }
        finally { try { if (!committed && path is not null && File.Exists(path)) File.Delete(path); } finally { gate.Release(); } }
    }
    internal async Task DeleteAsync(CloneSample sample, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        var tombstone = sample.FilePath + ".deleted"; bool moved = false;
        try
        {
            var samples = Load(); if (!Owned(sample) || !samples.Contains(sample)) throw new UserFeedbackException("样本不属于当前本地库。");
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(sample.FilePath)) { File.Move(sample.FilePath, tombstone); moved = true; }
            try { Save(samples.Where(s => s.Id != sample.Id)); }
            catch { if (moved) File.Move(tombstone, sample.FilePath); throw; }
            if (moved)
            {
                try { File.Delete(tombstone); }
                catch { File.Move(tombstone, sample.FilePath); Save(samples); throw; }
            }
        }
        finally { gate.Release(); }
    }
}
