using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AsterLauncher.App.Services;

/// <summary>Confirms public web navigation on the owning UI thread without displaying or logging URL credentials.</summary>
public static class ExternalBrowserService
{
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> DialogGates = new();

    public static async Task<bool> OpenAsync(Uri uri, XamlRoot? root, string? description = null)
    {
        if (root is null) return false;
        var gate = DialogGates.GetValue(root, static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) return false;
        try
        {
            // Another feature may own a dialog on this root. Never queue an unexpected later browser launch.
            if (HasOpenDialog(root)) return false;
            if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
            {
                await ShowFailureAsync(root, "仅支持不含登录凭据的 http 或 https 网页链接。");
                return false;
            }

            // Keep the actual destination separate from its display. Query/fragment values never enter UI text.
            var host = uri.IdnHost + (uri.IsDefaultPort ? "" : ":" + uri.Port);
            var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            var details = new StackPanel { Spacing = 10, MaxWidth = 420 };
            details.Children.Add(new TextBlock
            {
                Text = "将在默认浏览器中打开以下网页。",
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(description))
            {
                details.Children.Add(new TextBlock
                {
                    Text = description,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            details.Children.Add(new TextBlock
            {
                Text = host,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            details.Children.Add(new ScrollViewer
            {
                MaxHeight = 112,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = "/" + path,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            });
            var continueStyle = new Style
            {
                TargetType = typeof(Button),
                BasedOn = (Style)Application.Current.Resources["ThemeSecondaryButtonStyle"]
            };
            var continueForeground = AppearanceBrushes.Get("GlassTextBrush");
            continueStyle.Setters.Add(new Setter(Control.ForegroundProperty, continueForeground));
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                RequestedTheme = RootTheme(root),
                Title = "即将跳转",
                Content = details,
                PrimaryButtonText = "继续打开",
                PrimaryButtonStyle = continueStyle,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            // DefaultButton.Close keeps the cancel action accented. The non-default action must
            // use this dialog's card theme for its normal, hover and pressed text as well.
            foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" })
                dialog.Resources[key] = continueForeground;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

            try
            {
                if (await Windows.System.Launcher.LaunchUriAsync(uri)) return true;
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException
                or ArgumentException or UnauthorizedAccessException)
            {
                // OS exceptions may contain the full URI. Do not forward them to logs or UI.
            }
            await ShowFailureAsync(root, "暂时无法打开默认浏览器，请检查系统的默认浏览器设置后重试。");
            return false;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            // The root may have closed, or a different feature may have opened a ContentDialog meanwhile.
            // There is no navigation unless the confirmation completed with the primary action.
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task ShowFailureAsync(XamlRoot root, string message)
    {
        if (HasOpenDialog(root)) return;
        await new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = RootTheme(root),
            Title = "未能打开网页",
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }

    private static ElementTheme RootTheme(XamlRoot root) =>
        root.Content is FrameworkElement element ? element.ActualTheme : ElementTheme.Default;

    private static bool HasOpenDialog(XamlRoot root) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => ContainsDialog(popup.Child));

    private static bool ContainsDialog(DependencyObject? element)
    {
        if (element is null) return false;
        if (element is ContentDialog) return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (ContainsDialog(VisualTreeHelper.GetChild(element, index))) return true;
        return false;
    }
}
