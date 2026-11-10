using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class XiaomiOcrTests
{
    private const ulong Handle = 0x1234567887654321;

    [Fact]
    public void AbiLayoutAndVersionMatchHeader()
    {
        XiaomiOcr.CheckAbi(1);
        Assert.Throws<UserFeedbackException>(() => XiaomiOcr.CheckAbi(2));
        Assert.Equal(32, Marshal.SizeOf<OcrOptions>());
        Assert.Equal(40, Marshal.SizeOf<OcrImage>());
        Assert.Equal(8, Marshal.OffsetOf<OcrOptions>(nameof(OcrOptions.Model)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<OcrOptions>(nameof(OcrOptions.Projector)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<OcrOptions>(nameof(OcrOptions.ContextTokens)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<OcrImage>(nameof(OcrImage.Length)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<OcrImage>(nameof(OcrImage.Stride)).ToInt32());
    }

    [Theory]
    [InlineData(PixelFormat.Format24bppRgb, 4)]
    [InlineData(PixelFormat.Format24bppRgb, -4)]
    [InlineData(PixelFormat.Format32bppArgb, 4)]
    public void RgbConversionKeepsLogicalRowsAndSkipsPadding(PixelFormat format, int stride)
    {
        var buffer = Marshal.AllocHGlobal(8);
        try
        {
            var scan0 = stride < 0 ? buffer + 4 : buffer;
            Marshal.Copy(new byte[] { 3, 2, 1, 255 }, 0, scan0, 4);
            Marshal.Copy(new byte[] { 6, 5, 4, 255 }, 0, scan0 + stride, 4);
            using var image = new Bitmap(1, 2, stride, format, scan0);
            var rgb = OcrRgb24.FromBitmap(image);
            Assert.Equal((1, 2, 3), (rgb.Width, rgb.Height, rgb.Stride));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, rgb.Pixels);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Fact]
    public void OversizedImageRejectedBeforeConversion()
    {
        using var image = new Bitmap(8193, 1);
        Assert.Throws<UserFeedbackException>(() => OcrRgb24.FromBitmap(image));
    }

    [Fact]
    public void OldJsonUsesTesseractAndMissingFilesDoNotInvalidateSettings()
    {
        var old = JsonSerializer.Deserialize<Settings>("{\"Provider\":3}")!;
        Assert.Equal(OcrEngine.Tesseract, old.OcrEngine);
        var changed = old with { OcrEngine = OcrEngine.XiaomiCuda, XiaomiModelPath = @"Z:\模型 路径\语言.gguf", XiaomiProjectorPath = @"Y:\视觉.gguf" };
        var loaded = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(changed))!;
        Assert.True(loaded.IsValid);
        Assert.Equal(changed, loaded);
        Assert.Throws<UserFeedbackException>(() => OcrModels.From(loaded).Validate());
    }

    [Fact]
    public async Task DefaultRouteRecognizesWithoutLoadingCuda()
    {
        var xiaomi = new XiaomiOcr(() => throw new InvalidOperationException("CUDA was loaded for Tesseract."));
        var route = new ScreenOcr(new LocalOcr(), xiaomi);
        using var image = OcrTests.Fixture("SeleVox OCR 123", "Arial");
        try
        {
            var result = await route.RecognizeAsync(image, new Settings(OcrLanguage: OcrLanguage.English), default);
            Assert.Contains("OCR 123", result.Text);
        }
        finally { await xiaomi.ShutdownAsync(); }
    }

    [Fact]
    public async Task ReuseUtf8OutputOwnershipAndModelSwitch()
    {
        using var files = new ModelFiles();
        var api = new FakeNative();
        var engine = new XiaomiOcr(() => api);
        using var image = new Bitmap(1, 2);
        try
        {
            Assert.Equal(new CapturedText("中文\nsecond\n\nthird", TextSource.ScreenOcr),
                await engine.RecognizeAsync(image, files.Models, default));
            await engine.RecognizeAsync(image, files.Models, default);
            Assert.Equal(files.Models, api.Paths);
            Assert.Equal(1, api.Creates);
            Assert.Equal(2, api.Frees);
            var changed = files.Models with { Model = files.Models.Projector };
            await engine.RecognizeAsync(image, changed, default);
            Assert.Equal(changed, api.Paths);
            Assert.Equal(2, api.Creates);
            Assert.Equal(1, api.Destroys);
            await engine.RetireAsync(null);
            Assert.Equal(2, api.Destroys);
        }
        finally { await engine.ShutdownAsync(); }
    }

    [Fact]
    public async Task FailedCandidateLeavesSavedModelsUsable()
    {
        using var files = new ModelFiles();
        var saved = new Settings { OcrEngine = OcrEngine.XiaomiCuda, XiaomiModelPath = files.Models.Model,
            XiaomiProjectorPath = files.Models.Projector };
        var candidate = files.Models with { Model = files.Models.Projector };
        var api = new FakeNative { FailModel = candidate.Model };
        var engine = new XiaomiOcr(() => api);
        var route = new ScreenOcr(new LocalOcr(), engine);
        using var image = new Bitmap(1, 2);
        try
        {
            var before = await route.RecognizeAsync(image, saved, default);
            await Assert.ThrowsAsync<UserFeedbackException>(() => engine.TestAsync(candidate, default));
            Assert.Equal(1, api.Destroys);
            Assert.Equal(files.Models, OcrModels.From(saved));
            Assert.Equal(OcrEngine.XiaomiCuda, saved.OcrEngine);
            Assert.Equal(before, await route.RecognizeAsync(image, saved, default));
            Assert.Equal(3, api.Creates);
            Assert.Equal(files.Models, api.Paths);
        }
        finally { await engine.ShutdownAsync(); }
        Assert.Equal(2, api.Destroys);
    }

    [Fact]
    public async Task CancellationDuringCreateDestroysWithoutRecognizing()
    {
        using var files = new ModelFiles();
        using var image = new Bitmap(1, 2);
        using var token = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeNative { Creating = () => { entered.SetResult(); Wait(resume); } };
        var engine = new XiaomiOcr(() => api);
        var work = engine.RecognizeAsync(image, files.Models, token.Token);
        try
        {
            await WaitAsync(entered);
            token.Cancel();
            Assert.Equal(0, api.Destroys);
            resume.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.Equal(0, api.Recognitions);
            Assert.Equal(1, api.Destroys);
        }
        finally { resume.TrySetResult(); await engine.ShutdownAsync(); }
    }

    [Fact]
    public async Task InferenceCancelJoinsBeforeReuseAndShutdown()
    {
        using var files = new ModelFiles();
        using var image = new Bitmap(1, 2);
        using var token = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeNative
        {
            Recognizing = () => { entered.TrySetResult(); Wait(resume); },
            Cancelling = () => cancelled.TrySetResult()
        };
        var engine = new XiaomiOcr(() => api);
        var first = engine.RecognizeAsync(image, files.Models, token.Token);
        try
        {
            await WaitAsync(entered);
            token.Cancel();
            await WaitAsync(cancelled);
            var second = engine.RecognizeAsync(image, files.Models, default);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, api.Destroys);
            resume.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await second;
            Assert.Equal(1, api.Creates);
            Assert.Equal(2, api.Frees);
        }
        finally { resume.TrySetResult(); await engine.ShutdownAsync(); }
        Assert.Equal(1, api.Destroys);
    }

    [Theory]
    [InlineData(3, "语言")]
    [InlineData(5, "mmproj")]
    [InlineData(6, "CUDA")]
    [InlineData(7, "显存")]
    [InlineData(11, "预算")]
    public void ErrorsHaveActionableMessages(int code, string expected) =>
        Assert.Contains(expected, Assert.Throws<UserFeedbackException>(() => XiaomiOcr.CheckStatus(code)).Message);

    [Fact]
    public void NativeCancelledIsCancellation() =>
        Assert.Throws<OperationCanceledException>(() => XiaomiOcr.CheckStatus(8));

    private static Task WaitAsync(TaskCompletionSource point) => point.Task.WaitAsync(TimeSpan.FromSeconds(10));
    private static void Wait(TaskCompletionSource point) => WaitAsync(point).GetAwaiter().GetResult();

    private sealed class ModelFiles : IDisposable
    {
        private readonly string dir = Directory.CreateTempSubdirectory("SeleVox OCR ").FullName;
        public OcrModels Models { get; }
        public ModelFiles()
        {
            var model = Path.Combine(dir, "语言 模型.gguf");
            var projector = Path.Combine(dir, "视觉 投影.gguf");
            File.WriteAllBytes(model, []);
            File.WriteAllBytes(projector, []);
            Models = new(model, projector);
        }
        public void Dispose()
        {
            File.Delete(Models.Model!);
            File.Delete(Models.Projector!);
            Directory.Delete(dir);
        }
    }

    private sealed class FakeNative : IOcrNative
    {
        public int Creates, Destroys, Recognitions, Frees;
        public OcrModels? Paths;
        public string? FailModel;
        public Action? Creating, Recognizing, Cancelling;
        public uint Version() => 1;
        public int Create(in OcrOptions options, out ulong handle)
        {
            Assert.Equal((32u, 1u), (options.Size, options.Version));
            Paths = new(Marshal.PtrToStringUTF8(options.Model), Marshal.PtrToStringUTF8(options.Projector));
            Creates++;
            Creating?.Invoke();
            if (Paths.Model == FailModel)
            {
                handle = 0;
                return 4;
            }
            handle = Handle;
            return 0;
        }
        public int Recognize(ulong handle, in OcrImage image, out nint text)
        {
            Assert.Equal(Handle, handle);
            Assert.Equal((40u, 1u, 6ul, 1u, 2u, 3u), (image.Size, image.Version, image.Length, image.Width, image.Height, image.Stride));
            Recognitions++;
            Recognizing?.Invoke();
            text = Marshal.StringToCoTaskMemUTF8(" 中文\r\nsecond\r\n\r\nthird ");
            return 0;
        }
        public int Cancel(ulong handle) { Cancelling?.Invoke(); return handle == Handle ? 0 : 2; }
        public int Destroy(ulong handle) { Destroys++; return handle == Handle ? 0 : 2; }
        public void FreeText(nint text) { Frees++; Marshal.FreeCoTaskMem(text); }
    }
}
