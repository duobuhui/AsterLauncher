using AsterLauncher.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Views;

public sealed partial class LogsPage : Page
{
    public LogsPage(LogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
    }

    public LogViewModel ViewModel { get; }

    private void Page_OnLoaded(object sender, RoutedEventArgs e) => ViewModel.Attach(DispatcherQueue);

    private void Refresh_OnClick(object sender, RoutedEventArgs e) => ViewModel.Refresh();
}
