using System.Windows.Input;

namespace SeleVox.App;

internal static class OcrSelectionCursor
{
    // Embedded in the App assembly so Portable and Setup use the same resource.
    // The caller owns a successfully loaded cursor; never dispose the shared fallback.
    internal static Cursor Load(Func<Stream?>? openResource = null)
    {
        try
        {
            using var stream = openResource is null
                ? typeof(OcrSelectionCursor).Assembly.GetManifestResourceStream("SeleVox.App.OcrSelection.cur")
                : openResource();
            return stream is null ? Cursors.Cross : new Cursor(stream, scaleWithDpi: true);
        }
        catch (Exception error) when (error is IOException or ArgumentException or
            System.Runtime.InteropServices.ExternalException or NotSupportedException or
            UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Cursor decoration must never prevent a screen selection.
            return Cursors.Cross;
        }
    }
}
