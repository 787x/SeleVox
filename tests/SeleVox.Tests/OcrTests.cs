using System.Drawing;
using System.Text.Json;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class OcrTests
{
    [Theory] [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)]
    public void OverlayConversionUsesItsOwnDpiAndPhysicalOrigin(double scale)
    {
        var monitor = new Rectangle(-2560, -1440, 2560, 1440);
        var dip = ScreenGeometry.OverlayPoint(monitor, new(-2060, -1140), 2560 / scale, 1440 / scale);
        Assert.Equal(500 / scale, dip.X, 6); Assert.Equal(300 / scale, dip.Y, 6);
        Assert.Equal(new Rectangle(500, 300, 800, 600), ScreenGeometry.Crop(monitor, new(-2060, -1140), new(-1260, -540)));
    }
    [Theory]
    [InlineData(0, 0, 100, 100, 400, 250, 100, 100, 300, 150)]
    [InlineData(-1920, -1080, -1800, -1000, -1400, -750, 120, 80, 400, 250)]
    [InlineData(1920, 0, 2400, 500, 2000, 100, 80, 100, 400, 400)]
    [InlineData(0, -1440, 200, -1300, 100, -1400, 100, 40, 100, 100)]
    public void PhysicalCropNormalizesDirectionsAndMonitorOffsets(int x, int y, int ax, int ay, int bx, int by, int cx, int cy, int width, int height)
    {
        Assert.Equal(new Rectangle(cx, cy, width, height), ScreenGeometry.Crop(new(x, y, 1920, 1080), new(ax, ay), new(bx, by)));
    }
    [Fact] public void RejectsCrossMonitorSmallEmptyAndOversizeRegions()
    {
        var monitor = new Rectangle(-1920, 0, 1920, 1080);
        Assert.Throws<UserFeedbackException>(() => ScreenGeometry.Crop(monitor, new(-100, 100), new(10, 200)));
        Assert.Throws<UserFeedbackException>(() => ScreenGeometry.Crop(monitor, new(-100, 100), new(-100, 200)));
        Assert.Throws<UserFeedbackException>(() => ScreenGeometry.Crop(monitor, new(-100, 100), new(-99, 200)));
        Assert.Throws<UserFeedbackException>(() => ScreenGeometry.Crop(new(0, 0, 4000, 3000), new(0, 0), new(4000, 3000)));
        Assert.Equal(new Rectangle(1800, 100, 120, 980), ScreenGeometry.Crop(monitor, new(-120, 100), new(0, 1080)));
    }
    [Fact] public void OldSettingsRemainLocalAndOcrLanguageRoundTrips()
    {
        var old = JsonSerializer.Deserialize<Settings>("{\"Rate\":1,\"Provider\":3}")!;
        Assert.True(old.IsValid); Assert.Equal(OcrLanguage.ChineseEnglish, old.OcrLanguage); Assert.Equal(TtsProvider.MiMo, old.Provider);
        var updated = old with { OcrLanguage = OcrLanguage.English };
        Assert.Equal(updated, JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(updated)));
        Assert.False((old with { OcrLanguage = (OcrLanguage)99 }).IsValid);
    }
    [Fact] public async Task LateResultAfterReplacementCancelAndDisposeNeverPublishes()
    {
        using var operation = new TextCaptureOperation();
        var first = new TaskCompletionSource<CapturedText>(); var texts = new List<string>(); var errors = new List<string>();
        var pending = operation.RunAsync(_ => first.Task, r => texts.Add(r.Text), errors.Add);
        await operation.RunAsync(_ => Task.FromResult(new CapturedText("new", TextSource.ScreenOcr)), r => texts.Add(r.Text), errors.Add);
        first.SetResult(new("old", TextSource.ScreenOcr)); await pending;
        Assert.Equal(new[] { "new" }, texts); Assert.Empty(errors);
        var late = new TaskCompletionSource<CapturedText>();
        pending = operation.RunAsync(_ => late.Task, r => texts.Add(r.Text), errors.Add); operation.Cancel();
        late.SetException(new UserFeedbackException("obsolete")); await pending; Assert.Empty(errors);
        late = new(); pending = operation.RunAsync(_ => late.Task, r => texts.Add(r.Text), errors.Add); operation.Dispose();
        late.SetResult(new("disposed", TextSource.ScreenOcr)); await pending; Assert.Single(texts);
    }
    [Theory]
    [InlineData(OcrLanguage.English, "English screen capture\nSecond line reading order", "English screen capture", "Second line reading order", "Arial")]
    [InlineData(OcrLanguage.ChineseEnglish, "屏幕文字识别测试\n中文段落阅读顺序", "屏幕文字识别测试", "中文段落阅读顺序", "Microsoft YaHei")]
    [InlineData(OcrLanguage.ChineseEnglish, "本地 OCR English 123\n第二行 Screen capture", "本地 OCR English 123", "第二行 Screen capture", "Microsoft YaHei")]
    public async Task RealEngineRecognizesFixedNonSensitiveParagraphs(OcrLanguage language, string text, string first, string second, string font)
    {
        using var image = Fixture(text, font);
        var result = await new LocalOcr().RecognizeAsync(image, language, CancellationToken.None);
        var normalized = result.Text.Replace(" ", "");
        Assert.Contains(first.Replace(" ", ""), normalized); Assert.Contains(second.Replace(" ", ""), normalized);
        Assert.True(normalized.IndexOf(first.Replace(" ", ""), StringComparison.Ordinal) < normalized.IndexOf(second.Replace(" ", ""), StringComparison.Ordinal));
        Assert.Contains('\n', result.Text); Assert.Equal(TextSource.ScreenOcr, result.Source);
    }
    [Fact] public async Task RealEngineBlankAndCancellationFailWithoutText()
    {
        using var image = Fixture("", "Arial");
        await Assert.ThrowsAsync<UserFeedbackException>(() => new LocalOcr().RecognizeAsync(image, OcrLanguage.ChineseEnglish, CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalOcr().RecognizeAsync(image, OcrLanguage.English, cancellation.Token));
    }
    [Fact] public async Task RealCancelledRecognitionReleasesGateForRetryAndMissingModelExplainsRecovery()
    {
        using var image = Fixture("English capture fixture\nSecond line text", "Arial");
        var engine = new LocalOcr(); using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RecognizeAsync(image, OcrLanguage.ChineseEnglish, cancellation.Token));
        Assert.Contains("English", (await engine.RecognizeAsync(image, OcrLanguage.English, CancellationToken.None)).Text);
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new LocalOcr(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))
            .RecognizeAsync(image, OcrLanguage.English, CancellationToken.None));
        Assert.Contains("OcrData", error.Message);
    }
    internal static Bitmap Fixture(string text, string family)
    {
        var image = new Bitmap(1100, 240, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(image); graphics.Clear(Color.White);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(family, 30, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString(text, font, Brushes.Black, new PointF(30, 25)); return image;
    }
}
