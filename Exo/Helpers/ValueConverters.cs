using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Exo.Helpers;

public sealed partial class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return false;
    }
}

public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible (for hiding content while loading).</summary>
public sealed partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Non-empty string → Visible; empty/null → Collapsed.</summary>
public sealed partial class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var s = value as string;
        return string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Coming-soon cards render slightly dimmed (kept high enough that B&W marks stay readable).</summary>
public sealed partial class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is true) return 0.72;
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// true → 1 opacity, false → 0. Keeps layout space (unlike Collapsed) so status/progress
/// can update without shifting buttons and lists.
/// </summary>
public sealed partial class BoolToShowOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 1.0 : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>true (coming soon) → "Coming soon", false → "Ready".</summary>
public sealed partial class ComingSoonLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? "Coming soon" : "Ready";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Resolves bundled asset paths (e.g. Assets/Logos/discord.png) to BitmapImage.</summary>
public sealed partial class AssetPathToImageSourceConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapImage> ImageCache =
        new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string relative || string.IsNullOrWhiteSpace(relative))
            return null;

        return Resolve(relative);
    }

    public static BitmapImage? Resolve(string relative)
    {
        try
        {
            var appDirectory = Path.GetFullPath(PathHelper.AppDirectory);
            var full = Path.GetFullPath(Path.Combine(
                appDirectory,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            var appPrefix = appDirectory + Path.DirectorySeparatorChar;
            if (!full.StartsWith(appPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return null;

            return ImageCache.GetOrAdd(full, static path =>
            {
                // Decode at 2× display size (home logos are 64 logical px) so GPU
                // downscales from a sharp buffer on 100–200% DPI instead of soft
                // bilinear from full 256 or blurry upscale from a tiny decode.
                var image = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = 128
                };
                image.UriSource = new Uri(path, UriKind.Absolute);
                return image;
            });
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
