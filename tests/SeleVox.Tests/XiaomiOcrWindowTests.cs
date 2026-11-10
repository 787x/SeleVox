using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class XiaomiGpuFactAttribute : FactAttribute
{
    public XiaomiGpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_XIAOMI_GPU_TESTS") != "1")
            Skip = "Enable explicitly with an ABI 1 runtime and local GGUF files.";
    }
}

[Collection("WPF desktop")]
public sealed class XiaomiOcrWindowTests
{
    [Fact]
    [Trait("Category", "Offline")]
    public Task OnlyVerifiedCandidateSavesAndLateTestsCannotOverwriteState() => OcrDesktopTests.RunSta(async () =>
    {
        var saved = new Settings(Provider: TtsProvider.MiMo);
        var writes = 0;
        var pending = new TaskCompletionSource();
        CancellationToken testToken = default;
        MainWindow? window = null;
        window = new MainWindow(saved, updated =>
        {
            writes++;
            saved = updated;
            window!.SetAutomaticSettings(updated);
            return null;
        }, () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { })
        {
            TestXiaomiOcr = (_, token) => { testToken = token; return pending.Task; }
        };
        try
        {
            var engine = Find<ComboBox>(window, "OcrEngineChoice");
            var model = Find<TextBox>(window, "XiaomiModelPath");
            var projector = Find<TextBox>(window, "XiaomiProjectorPath");
            var status = Find<TextBlock>(window, "OcrModelStatus");
            var test = Find<Button>(window, "TestOcrModel");
            var save = Buttons(window).Single(b => b.Content as string == "保存设置");
            await WaitForOutputs(window);
            Assert.Equal(0, engine.SelectedIndex);
            engine.SelectedIndex = 1;
            model.Text = @"C:\模型 路径\语言.gguf";
            projector.Text = @"D:\投影器.gguf";
            Find<TextBox>(window, "LlmModel").Text = "unsaved-model";
            Click(save);
            Assert.Equal(0, writes);
            Click(test);
            Assert.False(test.IsEnabled);
            model.Text = @"C:\模型 路径\新语言.gguf";
            Assert.True(testToken.IsCancellationRequested);
            pending.SetResult();
            await DrainDispatcher();
            Assert.Empty(status.Text);
            Click(save);
            Assert.Equal(0, writes);

            pending = new();
            Click(test);
            pending.SetException(new UserFeedbackException("模型错误"));
            await DrainDispatcher();
            Assert.Equal("模型错误", status.Text);
            Assert.Equal(OcrEngine.Tesseract, saved.OcrEngine);

            pending = new();
            Click(test);
            pending.SetResult();
            await DrainDispatcher();
            Assert.Contains("测试成功", status.Text);
            Assert.Equal(0, writes);
            Assert.NotEqual("unsaved-model", saved.LlmModel);
            Click(save);
            Assert.Equal(1, writes);
            Assert.Equal(OcrEngine.XiaomiCuda, saved.OcrEngine);
            Assert.Equal(model.Text, saved.XiaomiModelPath);
            Assert.Equal("unsaved-model", saved.LlmModel);

            engine.SelectedIndex = 0;
            Click(save);
            Assert.Equal(OcrEngine.Tesseract, saved.OcrEngine);
            Assert.Equal(model.Text, saved.XiaomiModelPath);
            Assert.Equal(projector.Text, saved.XiaomiProjectorPath);
        }
        finally { window.Close(); }
    });

    [XiaomiGpuFact]
    [Trait("Category", "WindowsOcr")]
    public Task RealSettingsTestAndMixedTextRecognition() => OcrDesktopTests.RunSta(async () =>
    {
        var bf16 = Required("SELEVOX_XIAOMI_BF16");
        var q4 = Required("SELEVOX_XIAOMI_Q4");
        var projector = Required("SELEVOX_XIAOMI_MMPROJ");
        var runtime = new XiaomiOcr();
        var route = new ScreenOcr(new LocalOcr(), runtime);
        var saved = new Settings(Provider: TtsProvider.MiMo);
        var completion = new TaskCompletionSource();
        MainWindow? window = null;
        window = new MainWindow(saved, updated => { saved = updated; window!.SetAutomaticSettings(updated); return null; },
            () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { })
        {
            TestXiaomiOcr = async (models, token) =>
            {
                try { await runtime.TestAsync(models, token); }
                finally { completion.TrySetResult(); }
            }
        };
        try
        {
            await WaitForOutputs(window);
            Find<ComboBox>(window, "OcrEngineChoice").SelectedIndex = 1;
            Find<TextBox>(window, "XiaomiProjectorPath").Text = projector;
            var save = Buttons(window).Single(b => b.Content as string == "保存设置");
            using var mixed = new System.Drawing.Bitmap(Required("SELEVOX_XIAOMI_FIXTURE"));
            if (Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES") == "-1")
            {
                var error = await Assert.ThrowsAsync<UserFeedbackException>(() => runtime.RecognizeAsync(mixed, new(bf16, projector), default));
                Assert.Contains("CUDA 不可用", error.Message);
                return;
            }
            var missing = await Assert.ThrowsAsync<UserFeedbackException>(() => runtime.RecognizeAsync(mixed, new(bf16, projector + ".missing.gguf"), default));
            Assert.Contains("mmproj", missing.Message);
            var wrong = await Assert.ThrowsAsync<UserFeedbackException>(() => runtime.RecognizeAsync(mixed, new(projector, projector), default));
            Assert.Contains("架构不兼容", wrong.Message);
            foreach (var model in new[] { bf16, q4 })
            {
                Find<TextBox>(window, "XiaomiModelPath").Text = model;
                completion = new();
                Click(Find<Button>(window, "TestOcrModel"));
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
                await DrainDispatcher();
                var status = Find<TextBlock>(window, "OcrModelStatus").Text;
                Assert.Contains("测试成功", status);
                Click(save);
                Assert.Equal(model, saved.XiaomiModelPath);
                for (var i = 0; i < 2; i++)
                {
                    var result = await route.RecognizeAsync(mixed, saved, default);
                    Assert.Equal(TextSource.ScreenOcr, result.Source);
                    Assert.Contains("订单编号", result.Text);
                    Assert.Contains("128.50", result.Text);
                }
            }
            Find<ComboBox>(window, "OcrEngineChoice").SelectedIndex = 0;
            Click(save);
            await runtime.RetireAsync(null);
            Assert.Equal(OcrEngine.Tesseract, saved.OcrEngine);
            Assert.Contains("128.50", (await route.RecognizeAsync(mixed, saved, default)).Text);
        }
        finally { window.Close(); await runtime.ShutdownAsync(); }
    });

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Set {name} before running this test.");
    private static T Find<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task DrainDispatcher() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static async Task WaitForOutputs(MainWindow window)
    {
        var outputs = Find<ListBox>(window, "Outputs");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (outputs.Items.Count == 0) await Task.Delay(10, timeout.Token);
    }

    private static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            foreach (var nested in Buttons(child)) yield return nested;
        }
    }
}
