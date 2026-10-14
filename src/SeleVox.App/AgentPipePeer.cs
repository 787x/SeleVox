using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace SeleVox.App;
internal static class AgentPipePeer
{
    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientComputerNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComputer(SafePipeHandle pipe, StringBuilder name, uint capacity);
    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcess(SafePipeHandle pipe, out uint id);
    internal static void ValidateLocal(NamedPipeServerStream pipe)
    {
        var name = new StringBuilder(256);
        // Windows returns ERROR_PIPE_LOCAL (229), rather than a computer name, for a local peer.
        var remote = GetComputer(pipe.SafePipeHandle, name, 256);
        var error = Marshal.GetLastWin32Error();
        if (remote || error != 229 || !GetProcess(pipe.SafePipeHandle, out var id) || id == 0)
            throw new UnauthorizedAccessException();
    }
}
