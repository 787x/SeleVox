using System.Runtime.InteropServices;
using SeleVox.Core;

namespace SeleVox.App;

[StructLayout(LayoutKind.Sequential)]
internal struct OcrOptions
{
    public uint Size, Version;
    public nint Model, Projector;
    public uint ContextTokens, OutputTokens;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OcrImage
{
    public uint Size, Version;
    public nint Pixels;
    public ulong Length;
    public uint Width, Height, Stride;
}

internal interface IOcrNative
{
    uint Version();
    int Create(in OcrOptions options, out ulong handle);
    int Recognize(ulong handle, in OcrImage image, out nint text);
    int Cancel(ulong handle);
    int Destroy(ulong handle);
    void FreeText(nint text);
}

internal sealed class OcrNative : IOcrNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VersionCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateCall(in OcrOptions options, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RecognizeCall(ulong handle, in OcrImage image, out nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HandleCall(ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeCall(nint text);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LoadLibraryExW(string path, nint reserved, uint flags);

    private readonly VersionCall version;
    private readonly CreateCall create;
    private readonly RecognizeCall recognize;
    private readonly HandleCall cancel, destroy;
    private readonly FreeCall free;

    internal OcrNative()
    {
        const uint searchDllLoadDir = 0x100, searchSystem32 = 0x800;
        var path = Path.Combine(AppContext.BaseDirectory, "OcrCuda", "selevox_ocr.dll");
        var module = LoadLibraryExW(path, 0, searchDllLoadDir | searchSystem32);
        if (module == 0)
            throw new UserFeedbackException("无法加载 Xiaomi CUDA OCR。请检查程序 OcrCuda 目录中的原生运行时和 x64 VC++ 运行库。");
        try
        {
            T Export<T>(string name) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
            version = Export<VersionCall>("ocr_abi_version");
            XiaomiOcr.CheckAbi(version());
            create = Export<CreateCall>("ocr_create");
            recognize = Export<RecognizeCall>("ocr_recognize");
            cancel = Export<HandleCall>("ocr_cancel");
            destroy = Export<HandleCall>("ocr_destroy");
            free = Export<FreeCall>("ocr_free_text");
            // Keep the CUDA module mapped until process exit. Model handles are released separately.
        }
        catch (EntryPointNotFoundException)
        {
            NativeLibrary.Free(module);
            throw new UserFeedbackException("Xiaomi CUDA OCR DLL 接口不完整，请更换匹配的 ABI 1 运行时。");
        }
        catch
        {
            NativeLibrary.Free(module);
            throw;
        }
    }

    public uint Version() => version();
    public int Create(in OcrOptions options, out ulong handle) => create(options, out handle);
    public int Recognize(ulong handle, in OcrImage image, out nint text) => recognize(handle, image, out text);
    public int Cancel(ulong handle) => cancel(handle);
    public int Destroy(ulong handle) => destroy(handle);
    public void FreeText(nint text) => free(text);
}
