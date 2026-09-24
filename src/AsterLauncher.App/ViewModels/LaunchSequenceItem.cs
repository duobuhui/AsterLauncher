using AsterLauncher.Core;
using Microsoft.UI.Xaml;

namespace AsterLauncher.App.ViewModels;

public sealed record LaunchSequenceItem(int Number, string Title, string Stage, string Detail, LaunchStep? Step)
{
    public Visibility RemoveVisibility => Step is null ? Visibility.Collapsed : Visibility.Visible;
}
