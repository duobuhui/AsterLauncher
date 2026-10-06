using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AsterLauncher.App.Services;

/// <summary>Keep code-created controls on the same card theme as XAML controls.</summary>
public static class AppearanceBrushes
{
    private static readonly Dictionary<string,Brush> Copies = [];
    private static ElementTheme _theme = ElementTheme.Dark;
    public static Brush Get(string key)
    {
        var dictionary = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[_theme.ToString()];
        if(!dictionary.ContainsKey(key))return (Brush)Application.Current.Resources[key];
        var source=(Brush)dictionary[key];
        if(!Copies.TryGetValue(key,out var copy))
        {
            copy=source switch { AcrylicBrush => new AcrylicBrush(),SolidColorBrush => new SolidColorBrush(),_ => source };
            Copies[key]=copy;
        }
        Copy(source,copy);return copy;
    }
    public static void Refresh(ElementTheme theme)
    {
        _theme=theme==ElementTheme.Light?ElementTheme.Light:ElementTheme.Dark;
        foreach(var key in Copies.Keys.ToArray())Get(key);
    }
    private static void Copy(Brush source,Brush target)
    {
        if(source is SolidColorBrush s && target is SolidColorBrush t)t.Color=s.Color;
        if(source is AcrylicBrush a && target is AcrylicBrush b)
        {
            b.TintColor=a.TintColor;b.TintOpacity=a.TintOpacity;b.TintLuminosityOpacity=a.TintLuminosityOpacity;b.FallbackColor=a.FallbackColor;
        }
    }
}
