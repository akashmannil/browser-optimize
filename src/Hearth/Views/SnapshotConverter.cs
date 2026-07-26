using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Hearth.Views;

/// <summary>
/// Loads a tab snapshot for display in the Big Picture wall.
///
/// Paths arrive with a "?t=ticks" suffix so bindings invalidate when a tab is
/// re-captured -- the file path itself is stable and would never signal a change.
/// The suffix is stripped before touching the filesystem.
///
/// Decoding is capped at 480px wide: the wall shows dozens of thumbnails at a few
/// hundred pixels each, and decoding full-resolution page captures for all of
/// them would spend more memory on the tab overview than the tabs themselves.
/// </summary>
public sealed class SnapshotConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string raw || raw.Length == 0) return null;

        var path = raw.Split('?')[0];
        if (!File.Exists(path)) return null;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            // OnLoad releases the file handle immediately; without it a later
            // capture to the same path fails with a sharing violation.
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 480;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // A capture in flight can leave the file briefly unreadable. The
            // card falls back to its host-name placeholder, which is fine.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
