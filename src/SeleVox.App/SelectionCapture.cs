using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed class SelectionCapture
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string> CaptureAsync(nint foreground, CancellationToken cancellation)
        => (await CaptureSnapshotAsync(foreground, cancellation).ConfigureAwait(false)).Text;

    internal async Task<SelectionSnapshot> CaptureSnapshotAsync(nint foreground, CancellationToken cancellation,
        System.Windows.Point? selectionStart = null, System.Windows.Point? selectionEnd = null, string? expectedFocus = null)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(3), cancellation).ConfigureAwait(false))
            throw new UserFeedbackException("上次选区读取尚未返回，请稍后重试。");
        // UIA calls cannot always be cancelled. Keep the gate held until the actual call exits,
        // even when the caller times out, so an unresponsive provider cannot accumulate workers.
        var worker = Task.Run(() =>
        {
            try { return Capture(foreground, cancellation, selectionStart, selectionEnd, expectedFocus); }
            finally { gate.Release(); }
        });
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(3), cancellation).ConfigureAwait(false); }
        catch (TimeoutException) { throw new UserFeedbackException("目标应用的选区读取超时，请稍后重试。"); }
        finally
        {
            if (!worker.IsCompleted) _ = worker.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static SelectionSnapshot Capture(nint foreground, CancellationToken cancellation,
        System.Windows.Point? selectionStart, System.Windows.Point? selectionEnd, string? expectedFocus)
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
            // PR1 manual capture does not depend on providers exposing a runtime ID.
            var focusId = "";
            if (selectionStart is not null || expectedFocus is not null)
            {
                var runtimeId = focused.GetRuntimeId();
                if (runtimeId is not { Length: > 0 })
                    throw new UserFeedbackException("自动模式无法确认焦点身份，请使用手动热键。");
                focusId = string.Join(",", runtimeId);
            }
            if (expectedFocus is not null && focusId != expectedFocus)
                throw new UserFeedbackException("焦点已改变，已忽略自动选区。");
            var walker = TreeWalker.RawViewWalker;
            var foregroundElement = AutomationElement.FromHandle(foreground);
            var ancestors = SelectionScope.Resolve(focused, walker.GetParent,
                element => element.Current.IsPassword,
                element => (nint)element.Current.NativeWindowHandle == foreground || Automation.Compare(element, foregroundElement),
                cancellation);
            IReadOnlyList<AutomationElement>? pointerAncestors = null;
            if (selectionStart is { } pointer)
            {
                var hit = AutomationElement.FromPoint(pointer);
                pointerAncestors = SelectionScope.Resolve(hit, walker.GetParent,
                    element => element.Current.IsPassword,
                    element => (nint)element.Current.NativeWindowHandle == foreground || Automation.Compare(element, foregroundElement),
                    cancellation);
                if (pointerAncestors.Any(element => IsMouseActionControl(element.Current.ControlType)))
                    throw new UserFeedbackException("此次鼠标操作不是文本选择。");
            }
            // The verified ancestor chain may contain the browser document beyond six levels.
            // GetSelection remains the only text source; never read DocumentRange.
            foreach (var candidate in ancestors)
            {
                cancellation.ThrowIfCancellationRequested();
                if (pointerAncestors is not null && !pointerAncestors.Any(element => Automation.Compare(element, candidate))) continue;
                if (!candidate.TryGetCurrentPattern(TextPattern.Pattern, out var value)) continue;
                var pattern = (TextPattern)value;
                if (pattern.SupportedTextSelection == SupportedTextSelection.None) continue;
                var ranges = pattern.GetSelection();
                if (ranges.Length > 128) throw new UserFeedbackException("选区分段过多，请缩小选区。");
                var parts = new List<string>();
                var length = 0;
                var gestureInSelection = selectionStart is null;
                foreach (var range in ranges)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (range.GetEnclosingElement().Current.IsPassword) throw new UserFeedbackException("密码控件不会被读取。");
                    if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0) continue;
                    if (selectionStart is { } start)
                    {
                        var rectangles = range.GetBoundingRectangles();
                        foreach (var rectangle in rectangles)
                        {
                            if (SelectionGeometry.Intersects(rectangle, start, selectionEnd ?? start)) gestureInSelection = true;
                        }
                    }
                    var part = range.GetText(10001);
                    length += part.Length;
                    if (length > 10000) throw new UserFeedbackException("选区超过 10,000 字符，请缩小选区。");
                    if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
                }
                if (Native.GetForegroundWindow() != foreground || !Automation.Compare(focused, AutomationElement.FocusedElement))
                    throw new UserFeedbackException("前台应用或焦点已改变，请重试。");
                cancellation.ThrowIfCancellationRequested();
                if (parts.Count > 0)
                {
                    if (!gestureInSelection) return new("", focusId, "选区与鼠标拖选位置不匹配，已忽略自动朗读。");
                    return new(string.Join(Environment.NewLine, parts), focusId);
                }
                if (selectionStart is not null) return new("", focusId);
                // An actual caret-only selection must not fall back to another ancestor.
                throw new UserFeedbackException("没有选中文字。");
            }
            throw new UserFeedbackException("此控件未提供可读取的 UIA 文本选区。");
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException)
        { throw new UserFeedbackException($"无法访问选区（{ex.GetType().Name}）。目标应用可能不支持 UIA 或权限不同。"); }
    }

    private static bool IsMouseActionControl(ControlType type) => type == ControlType.Button || type == ControlType.ScrollBar ||
        type == ControlType.Thumb || type == ControlType.Slider || type == ControlType.TitleBar || type == ControlType.Menu ||
        type == ControlType.MenuItem || type == ControlType.ToolBar ||
        type == ControlType.Header || type == ControlType.HeaderItem;
}
