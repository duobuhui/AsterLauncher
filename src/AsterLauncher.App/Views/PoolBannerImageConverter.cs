using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AsterLauncher.App.Views;

public sealed class PoolBannerImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var fileName = value as string ?? "endfield-pool-card.svg";
        var uri = new Uri($"ms-appx:///Assets/Games/{fileName}");
        return fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? new SvgImageSource { UriSource = uri }
            : new BitmapImage(uri);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
