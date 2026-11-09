using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed record OcrModels(string? Model, string? Projector)
{
    internal static OcrModels From(Settings settings) => new(settings.XiaomiModelPath, settings.XiaomiProjectorPath);

    internal void Validate()
    {
        Check(Model, "语言模型");
        Check(Projector, "视觉投影器 mmproj");

        static void Check(string? path, string label)
        {
            if (path is null || !Path.IsPathFullyQualified(path) || path.Contains('\0') ||
                !string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new UserFeedbackException($"找不到有效的{label} GGUF，请重新选择文件并检查磁盘是否可用。");
        }
    }
}

internal sealed class XiaomiOcr(Func<IOcrNative>? load = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private IOcrNative? native;
    private OcrModels? loaded;
    private ulong handle;

    internal Task<CapturedText> RecognizeAsync(Bitmap image, OcrModels models, CancellationToken token) =>
        RunAsync(ct => Recognize(image, models, ct), token);

    internal Task TestAsync(OcrModels models, CancellationToken token) => RunAsync(ct =>
    {
        var alreadyLoaded = loaded == models;
        try
        {
            using var image = new Bitmap(640, 128, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(image))
            using (var font = new Font("Arial", 42, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("SeleVox OCR 123", font, Brushes.Black, 24, 32);
            }
            var text = Recognize(image, models, ct).Text;
            if (!string.Equals(string.Concat(text.Where(c => !char.IsWhiteSpace(c))), "SeleVoxOCR123", StringComparison.OrdinalIgnoreCase))
                throw new UserFeedbackException("模型未正确识别测试图片，请核对语言模型与 mmproj 是否配对。");
            return true;
        }
        finally { if (!alreadyLoaded) Release(); }
    }, token);

    internal async Task RetireAsync(OcrModels? keep)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (loaded != keep) await Task.Run(Release).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal Task ShutdownAsync()
    {
        shutdown.Cancel();
        return RetireAsync(null);
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await Task.Run(() => work(linked.Token), linked.Token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private unsafe CapturedText Recognize(Bitmap bitmap, OcrModels models, CancellationToken token)
    {
        models.Validate();
        var rgb = OcrRgb24.FromBitmap(bitmap);
        token.ThrowIfCancellationRequested();
        EnsureModel(models, token);
        var activeHandle = handle;
        var api = native!;
        int cancelStatus = 0;
        // ABI 1 ignores cancellation while idle. Retry across the entry boundary and
        // join all callbacks before releasing the gate or reusing the handle.
        var cancelTimer = new Timer(_ =>
        {
            var status = api.Cancel(activeHandle);
            if (status != 0) Interlocked.CompareExchange(ref cancelStatus, status, 0);
        });
        nint output = 0;
        try
        {
            int status;
            try
            {
                using var registration = token.Register(() => cancelTimer.Change(0, 20));
                token.ThrowIfCancellationRequested();
                fixed (byte* pixels = rgb.Pixels)
                {
                    var image = new OcrImage
                    {
                        Size = (uint)Marshal.SizeOf<OcrImage>(), Version = 1, Pixels = (nint)pixels,
                        Length = (ulong)rgb.Pixels.Length, Width = (uint)rgb.Width,
                        Height = (uint)rgb.Height, Stride = (uint)rgb.Stride
                    };
                    status = api.Recognize(activeHandle, in image, out output);
                }
            }
            finally { cancelTimer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            CheckStatus(cancelStatus);
            token.ThrowIfCancellationRequested();
            CheckStatus(status);
            var text = Marshal.PtrToStringUTF8(output)?.ReplaceLineEndings("\n").Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new UserFeedbackException("未识别到文字，请框选清晰的文字后重试。");
            return new CapturedText(text, TextSource.ScreenOcr);
        }
        finally { if (output != 0) api.FreeText(output); }
    }

    private void EnsureModel(OcrModels models, CancellationToken token)
    {
        if (loaded == models) return;
        Release();
        token.ThrowIfCancellationRequested();
        if (native is null)
        {
            var api = (load ?? (() => new OcrNative()))();
            CheckAbi(api.Version());
            native = api;
        }
        nint model = 0, projector = 0;
        try
        {
            model = Marshal.StringToCoTaskMemUTF8(models.Model);
            projector = Marshal.StringToCoTaskMemUTF8(models.Projector);
            var options = new OcrOptions { Size = (uint)Marshal.SizeOf<OcrOptions>(), Version = 1, Model = model, Projector = projector };
            token.ThrowIfCancellationRequested();
            CheckStatus(native.Create(in options, out var created));
            handle = created;
            if (token.IsCancellationRequested)
            {
                Release();
                token.ThrowIfCancellationRequested();
            }
            loaded = models;
        }
        finally
        {
            Marshal.FreeCoTaskMem(projector);
            Marshal.FreeCoTaskMem(model);
        }
    }

    private void Release()
    {
        if (handle == 0) return;
        CheckStatus(native!.Destroy(handle));
        handle = 0;
        loaded = null;
    }

    internal static void CheckAbi(uint version)
    {
        if (version != 1 || IntPtr.Size != 8 || Marshal.SizeOf<OcrOptions>() != 32 || Marshal.SizeOf<OcrImage>() != 40)
            throw new UserFeedbackException("Xiaomi CUDA OCR 需要匹配的 Windows x64 ABI 1 运行时。");
    }

    internal static void CheckStatus(int status)
    {
        if (status == 0) return;
        if (status == 8) throw new OperationCanceledException();
        throw new UserFeedbackException(status switch
        {
            1 => "Xiaomi OCR 图像或模型参数无效。",
            2 => "Xiaomi OCR 模型实例不可用，请重启程序后重试。",
            3 => "无法读取 Xiaomi OCR 语言 GGUF，请检查文件和磁盘。",
            4 => "语言 GGUF 架构不兼容，请选择 Xiaomi-OCR-0 语言模型。",
            5 => "视觉投影器 mmproj 无效或不兼容，请选择配套文件。",
            6 => "CUDA 不可用，请检查 NVIDIA GPU 和驱动。",
            7 => "显存不足，请释放显存或使用兼容的较小 GGUF。",
            9 => "Xiaomi OCR 推理失败，请核对模型配对和 NVIDIA 驱动。",
            10 => "Xiaomi OCR 实例忙，请稍后重试。",
            11 => "超出 Xiaomi OCR 上下文或输出预算，请缩小框选区域。",
            12 => "系统内存不足，请关闭其他程序后重试。",
            _ => $"Xiaomi OCR 返回未知错误（{status}）。"
        });
    }
}
