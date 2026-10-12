using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed class SelectionCapture
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string> CaptureAsync(nint foreground, CancellationToken cancellation)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(3), cancellation).ConfigureAwait(false))
            throw new UserFeedbackException("上次选区读取尚未返回，请稍后重试。");
        // UIA calls cannot always be cancelled. Keep the gate held until the actual call exits,
        // even when the caller times out, so an unresponsive provider cannot accumulate workers.
        var worker = Task.Run(() =>
        {
            try { return Capture(foreground, cancellation); }
            finally { gate.Release(); }
        });
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(3), cancellation).ConfigureAwait(false); }
        catch (TimeoutException) { throw new UserFeedbackException("目标应用的选区读取超时，请稍后重试。"); }
        finally
        {
            if (!worker.IsCompleted) _ = worker.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static string Capture(nint foreground, CancellationToken cancellation)
    {
        try
        {
            if (foreground == 0 || Native.GetForegroundWindow() != foreground)
                throw new UserFeedbackException("前台应用已改变，请重新选择后按快捷键。");
            Native.GetWindowThreadProcessId(foreground, out var process);
            if (process == Environment.ProcessId) throw new UserFeedbackException("请在其他应用中选中文字。");
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
                throw new UserFeedbackException("无法取得前台应用的焦点选区。");
            var walker = TreeWalker.RawViewWalker;
            var foregroundElement = AutomationElement.FromHandle(foreground);
            var ancestors = SelectionScope.Resolve(focused, walker.GetParent,
                element => element.Current.IsPassword,
                element => (nint)element.Current.NativeWindowHandle == foreground || Automation.Compare(element, foregroundElement),
                cancellation);
            // The verified ancestor chain may contain the browser document beyond six levels.
            // GetSelection remains the only text source; never read DocumentRange.
            foreach (var candidate in ancestors)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!candidate.TryGetCurrentPattern(TextPattern.Pattern, out var value)) continue;
                var pattern = (TextPattern)value;
                if (pattern.SupportedTextSelection == SupportedTextSelection.None) continue;
                var ranges = pattern.GetSelection();
                if (ranges.Length == 0) throw new UserFeedbackException("没有选中文字。");
                if (ranges.Length > 128) throw new UserFeedbackException("选区分段过多，请缩小选区。");
                var parts = new List<string>();
                var length = 0;
                foreach (var range in ranges)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (range.GetEnclosingElement().Current.IsPassword) throw new UserFeedbackException("密码控件不会被读取。");
                    if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0) continue;
                    var part = range.GetText(10001);
                    length += part.Length;
                    if (length > 10000) throw new UserFeedbackException("选区超过 10,000 字符，请缩小选区。");
                    if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
                }
                if (Native.GetForegroundWindow() != foreground || !Automation.Compare(focused, AutomationElement.FocusedElement))
                    throw new UserFeedbackException("前台应用或焦点已改变，请重试。");
                if (parts.Count > 0) return string.Join(Environment.NewLine, parts);
                // An actual caret-only selection must not fall back to another ancestor.
                throw new UserFeedbackException("没有选中文字。");
            }
            throw new UserFeedbackException("此控件未提供可读取的 UIA 文本选区。");
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException)
        { throw new UserFeedbackException($"无法访问选区（{ex.GetType().Name}）。目标应用可能不支持 UIA 或权限不同。"); }
    }
}
