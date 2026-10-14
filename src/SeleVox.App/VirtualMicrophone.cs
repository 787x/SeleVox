using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed record VirtualEndpoint(string Id, string Label, DataFlow Flow, DeviceState State, string DriverInstanceId);
internal static class VirtualMicrophones
{
    private static readonly object activityLock = new();
    private static string? activeRender;
    private static string activity = "";
    private static object? activityOwner;
    internal static string Activity(string id) { lock (activityLock) return activeRender == id ? activity : ""; }
    internal static void MarkFailure(string id, Settings settings)
    { if (settings.VirtualMicrophoneEnabled && settings.VirtualMicrophone?.RenderId == id) lock (activityLock) { activityOwner = null; activeRender = id; activity = "发生故障，虚拟输出已跳过"; } }
    internal static IAudioPlayback Track(IAudioPlayback player, string id, Settings settings) =>
        settings.VirtualMicrophoneEnabled && settings.VirtualMicrophone?.RenderId == id ? new TrackedPlayback(player, id) : player;
    private sealed class TrackedPlayback : IAudioPlayback
    {
        private readonly IAudioPlayback player; private readonly string id;
        public event Action<Exception?>? Finished;
        public TrackedPlayback(IAudioPlayback player, string id) { this.player = player; this.id = id; player.Finished += End; }
        private void State(string value, bool claim = false)
        { lock (activityLock) { if (claim) activityOwner = this; if (!ReferenceEquals(activityOwner, this)) return; activeRender = id; activity = value; if (value.Length == 0) activityOwner = null; } }
        private void End(Exception? error) { State(error is null ? "" : "发生故障，其他可用输出继续"); Finished?.Invoke(error); }
        public void Play() { State("正在路由", true); try { player.Play(); } catch { State("发生故障"); throw; } }
        public void Pause() { player.Pause(); State("路由已暂停"); }
        public void Stop() { try { player.Stop(); } finally { State(""); } }
        public void Dispose()
        {
            player.Finished -= End;
            try { player.Dispose(); }
            finally { lock (activityLock) { if (ReferenceEquals(activityOwner, this)) { activityOwner = null; if (!activity.StartsWith("发生故障")) activity = ""; } } }
        }
    }
    // Deliberately limited to ordinary VB-CABLE. Voicemeeter requires a running mixer.
    internal static bool IsSupportedDriver(string id) => id.StartsWith(@"ROOT\VBAUDIOVACWDM\", StringComparison.OrdinalIgnoreCase);
    internal static bool DriverPresent() => CM_Locate_DevNodeW(out _, @"ROOT\VBAUDIOVACWDM\0000", 0) == 0;
    public static IReadOnlyList<VirtualEndpoint> Scan()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All);
        var result = new List<VirtualEndpoint>();
        foreach (var device in devices)
        {
            using (device)
            {
                try
                {
                    // Some endpoint property stores omit PKEY_Device_InstanceId. Windows' MMDEVAPI
                    // software devnode uses the exact Core Audio endpoint ID; verify its PnP parent.
                    var parent = DriverParent(device.InstanceId);
                    if (parent.Length == 0) parent = DriverParent(@"SWD\MMDEVAPI\" + device.ID);
                    if (IsSupportedDriver(parent)) result.Add(new(device.ID, device.FriendlyName, device.DataFlow, device.State, parent));
                }
                catch { /* An endpoint removed during enumeration is unavailable, never guessed by name. */ }
            }
        }
        return result;
    }
    private static string DriverParent(string instance)
    {
        if (CM_Locate_DevNodeW(out var node, instance, 0) != 0) return "";
        for (var depth = 0; depth < 8; depth++)
        {
            var id = new StringBuilder(1024);
            if (CM_Get_Device_IDW(node, id, id.Capacity, 0) != 0) return "";
            if (IsSupportedDriver(id.ToString())) return id.ToString();
            if (CM_Get_Parent(out node, node, 0) != 0) break;
        }
        return "";
    }
    internal static bool IsReady(VirtualMicrophoneRoute? route, IReadOnlyList<VirtualEndpoint> devices) =>
        route is { RecordingVerified: true } && IsPair(route, devices, true);
    internal static bool IsPair(VirtualMicrophoneRoute route, IReadOnlyList<VirtualEndpoint> devices, bool active) =>
        IsSupportedDriver(route.DriverInstanceId) &&
        devices.Any(d => d.Id == route.RenderId && d.Flow == DataFlow.Render && SameDriver(d, route) && (!active || d.State == DeviceState.Active)) &&
        devices.Any(d => d.Id == route.CaptureId && d.Flow == DataFlow.Capture && SameDriver(d, route) && (!active || d.State == DeviceState.Active));
    private static bool SameDriver(VirtualEndpoint d, VirtualMicrophoneRoute route) =>
        string.Equals(d.DriverInstanceId, route.DriverInstanceId, StringComparison.OrdinalIgnoreCase);
    internal static VirtualMicrophoneRoute? Detect(IReadOnlyList<VirtualEndpoint> devices)
    {
        var active = devices.Where(d => d.State == DeviceState.Active).ToArray();
        var renders = active.Where(d => d.Flow == DataFlow.Render).ToArray();
        var captures = active.Where(d => d.Flow == DataFlow.Capture).ToArray();
        return renders.Length == 1 && captures.Length == 1 && renders[0].DriverInstanceId == captures[0].DriverInstanceId
            ? new(renders[0].Id, captures[0].Id, renders[0].DriverInstanceId) : null;
    }
    // Called on the existing audio worker for every new playback, never cached by friendly name.
    internal static Settings Resolve(Settings settings, IReadOnlyList<VirtualEndpoint>? devices = null)
    {
        if (!settings.VirtualMicrophoneEnabled)
        {
            if (settings.VirtualMicrophone is not { } known) return settings;
            if (settings.ExplicitOutputDevices)
            {
                var explicitlyRequested = (settings.OutputDeviceIds ?? []).Contains(known.RenderId);
                var captureReady = false;
                if (explicitlyRequested) try { captureReady = IsReady(known, devices ?? Scan()); } catch { }
                return settings with { NoFallbackOutputIds = [known.RenderId],
                    UnavailableOutputIds = explicitlyRequested && !captureReady ? [known.RenderId] : [] };
            }
            var disabledOutputs = settings.OutputDeviceIds is { Length: > 0 } output ? output : [Settings.DefaultOutput];
            var remaining = disabledOutputs.Where(id => id != known.RenderId).ToArray();
            if (remaining.Length == 0) throw new UserFeedbackException("虚拟麦克风已关闭，没有其他输出目标。请选择耳机 / 扬声器后朗读。");
            return settings with { OutputDeviceIds = remaining, NoFallbackOutputIds = [known.RenderId], UnavailableOutputIds = [known.RenderId] };
        }
        var route = settings.VirtualMicrophone;
        if (route is null) throw new UserFeedbackException("虚拟麦克风尚未选择并通过录音验证，请在设置中配置或关闭它。");
        var ready = false;
        try { ready = IsReady(route, devices ?? Scan()); } catch { }
        var ids = settings.OutputDeviceIds is { Length: > 0 } selected ? selected : [Settings.DefaultOutput];
        return settings with {
            OutputDeviceIds = ids.Append(route.RenderId).Distinct().ToArray(),
            NoFallbackOutputIds = [route.RenderId],
            UnavailableOutputIds = ready ? [] : [route.RenderId]
        };
    }
    internal static bool CanFallback(Settings settings) =>
        (settings.OutputDeviceIds is { Length: > 0 } ids ? ids : [Settings.DefaultOutput]).Any(id => !settings.NoFallbackOutputIds.Contains(id));

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
}
