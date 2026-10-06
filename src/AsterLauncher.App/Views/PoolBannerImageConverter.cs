using Microsoft.UI.Xaml.Data;
using AsterLauncher.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AsterLauncher.App.Views;

public sealed class PoolBannerImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is EndfieldPoolAnalysis pool)
        {
            var remote = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<IResourceCatalogProvider>().FindImage(BuiltInGameIds.Endfield, "banner", pool.BannerResourceKey);
            if (remote is not null) return new BitmapImage(new Uri(remote));
            value = pool.BannerAssetFileName;
        }
        var fileName = value as string ?? "endfield-pool-card.svg";
        if(fileName.Contains('\u001F'))
        {
            var parts=fileName.Split('\u001F',2);
            var remote=((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<IResourceCatalogProvider>().FindImage(BuiltInGameIds.Endfield,"banner",parts[0]);
            if(remote is not null)return new BitmapImage(new Uri(remote));
            fileName=parts[1];
        }
        var uri = new Uri($"ms-appx:///Assets/Games/{fileName}");
        return fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? new SvgImageSource { UriSource = uri }
            : new BitmapImage(uri);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
