using AsterLauncher.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Services;

public sealed class DialogLaunchDecisionService : ILaunchDecisionService
{
    private DispatcherQueue? _dispatcherQueue;
    private XamlRoot? _xamlRoot;

    public void Attach(XamlRoot xamlRoot, DispatcherQueue dispatcherQueue)
    {
        _xamlRoot = xamlRoot;
        _dispatcherQueue = dispatcherQueue;
    }

    public Task<bool> ShouldContinueAsync(
        LaunchStep failedStep,
        ProcessLaunchResult failure,
        CancellationToken cancellationToken = default)
    {
        if (_xamlRoot is null || _dispatcherQueue is null)
        {
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        void ShowDialog()
        {
            _ = ShowDialogAsync();
        }

        async Task ShowDialogAsync()
        {
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = _xamlRoot,
                    Title = $"“{failedStep.Name}”启动失败",
                    Content = failure.Message,
                    PrimaryButtonText = "继续方案",
                    CloseButtonText = "中止",
                    DefaultButton = ContentDialogButton.Close
                };
                completion.TrySetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            ShowDialog();
        }
        else if (!_dispatcherQueue.TryEnqueue(ShowDialog))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }
}
