using Microsoft.UI.Xaml.Data;

namespace AsterLauncher.App.Views;

public sealed class SectionHeaderWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double width ? Math.Max(0, width - 54) : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
