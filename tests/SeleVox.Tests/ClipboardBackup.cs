using System.Windows;
using System.Windows.Media.Imaging;

namespace SeleVox.Tests;

internal static class ClipboardBackup
{
    // GetDataObject is a delayed OLE source. Materialize before replacing the clipboard;
    // restoring that live source after replacement can recursively open the clipboard.
    internal static DataObject? Capture()
    {
        var source = Clipboard.GetDataObject();
        if (source is null) return null;
        var copy = new DataObject();
        foreach (var format in source.GetFormats(false))
        {
            var value = source.GetData(format, false);
            if (value is MemoryStream bytes) value = new MemoryStream(bytes.ToArray(), false);
            else if (value is BitmapSource bitmap) { var clone = bitmap.Clone(); clone.Freeze(); value = clone; }
            else if (value is Array array) value = array.Clone();
            if (value is not null) copy.SetData(format, value, false);
        }
        return copy;
    }
}
