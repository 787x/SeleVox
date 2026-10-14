using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace SeleVox.App;
internal static partial class AgentExportProtection
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
