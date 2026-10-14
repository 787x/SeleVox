using System.Security.Principal;
namespace SeleVox.App;
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle show;
    private RegisteredWaitHandle? listener;
    public bool IsFirst { get; }
    public SingleInstance(string scope = "SeleVox", bool showExisting = true)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!.Value;
        mutex = new Mutex(true, $"Local\\{scope}-{user}", out var created);
        IsFirst = created;
        show = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{scope}-Show-{user}");
        if (!IsFirst && showExisting) show.Set();
    }
    public void Listen(Action callback) => listener = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => callback(), null, Timeout.Infinite, false);
    public void Dispose()
    {
        listener?.Unregister(null); show.Dispose();
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
