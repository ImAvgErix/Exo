using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace OptiHub.Helpers;

public sealed class InverseBoolConverter : IValueConverter
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

public sealed class BoolToVisibilityConverter : IValueConverter
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
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Coming-soon cards render slightly dimmed.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is true) return 0.55;
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Resolves bundled asset paths (e.g. Assets/Logos/discord.png) to BitmapImage.</summary>
public sealed class AssetPathToImageSourceConverter : IValueConverter
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
                var image = new BitmapImage { DecodePixelWidth = 64 };
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
