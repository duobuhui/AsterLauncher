using System.Text.Json;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AsterLauncher.App.Views;

/// <summary>Resolves only the publisher portraits bundled with this release.</summary>
public sealed class PortraitImageConverter : IValueConverter
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Paths = new(ReadPaths);
    private static readonly Dictionary<string, BitmapImage> Images = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string name || parameter is not string gameId)
        {
            return null!;
        }

        var key = gameId + "\u001F" + name;
        if (!Paths.Value.TryGetValue(key, out var relativePath))
        {
            return null!;
        }

        if (!Images.TryGetValue(relativePath, out var image))
        {
            var fullPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Games", "Gacha",
                "Portraits", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                return null!;
            }

            image = new BitmapImage(new Uri(Path.GetFullPath(fullPath)));
            Images[relativePath] = image;
        }

        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static IReadOnlyDictionary<string, string> ReadPaths()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "Games", "Gacha", "Portraits");
        var indexPath = Path.Combine(root, "portrait-index.json");
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(indexPath))
        {
            return paths;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var gameId = entry.GetProperty("GameId").GetString();
                var name = entry.GetProperty("Name").GetString();
                var file = entry.GetProperty("File").GetString();
                if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(file) || file.Contains("..", StringComparison.Ordinal))
                {
                    continue;
                }

                var key = gameId + "\u001F" + name;
                paths.TryAdd(key, file);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException
            or InvalidOperationException or KeyNotFoundException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return paths;
    }
}