using SeleVox.Core;

namespace SeleVox.App;

internal static class SelectionScope
{
    // Web accessibility trees can include many DOM ancestors between focus and HWND.
    // Walk only that single chain; never visit siblings, descendants or other windows.
    internal const int MaximumAncestors = 64;

    internal static IReadOnlyList<T> Resolve<T>(T focused, Func<T, T?> parent,
        Func<T, bool> isPassword, Func<T, bool> isForeground, CancellationToken cancellation) where T : class
    {
        var ancestors = new List<T>();
        T? element = focused;
        while (element is not null && ancestors.Count < MaximumAncestors)
        {
            cancellation.ThrowIfCancellationRequested();
            // Validate the entire scope before any caller reads selection text.
            if (isPassword(element)) throw new UserFeedbackException("密码控件不会被读取。");
            ancestors.Add(element);
            if (isForeground(element)) return ancestors;
            if (ancestors.Count == MaximumAncestors) break;
            element = parent(element);
        }
        throw new UserFeedbackException($"无法确认选区属于当前前台窗口（已检查 {ancestors.Count} 层祖先）。");
    }
}
