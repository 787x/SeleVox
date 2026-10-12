using System.Runtime.InteropServices;
using System.Text;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed class AzureCredentials(string target = "SeleVox/AzureSpeech")
{
    private const int Generic = 1, NotFound = 1168;
    public string? Read()
    {
        if (!CredRead(target, Generic, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return null;
            throw new UserFeedbackException("无法读取 Windows 凭据管理器中的 Azure Key。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
        }
        finally { CredFree(pointer); }
    }
    public void Write(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Encoding.Unicode.GetByteCount(key) > 2560)
            throw new UserFeedbackException("Azure Key 为空或过长。");
        var blob = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var credential = new Credential { Type = Generic, TargetName = target, BlobSize = (uint)Encoding.Unicode.GetByteCount(key),
                Blob = blob, Persist = 2, UserName = "AzureSpeech" };
            if (!CredWrite(ref credential, 0)) throw new UserFeedbackException("无法保存 Azure Key 到 Windows 凭据管理器。");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
    public void Clear()
    {
        if (!CredDelete(target, Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new UserFeedbackException("无法清除 Windows 凭据管理器中的 Azure Key。");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags; public int Type;
        public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize; public nint Blob; public uint Persist, AttributeCount;
        public nint Attributes; public string? TargetAlias; public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, int type, int flags, out nint pointer);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint pointer);
}
