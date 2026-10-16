using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Services;

public sealed class DialogLaunchDecisionService : ILaunchDecisionService, IEndfieldBridgeElevationConsent
{
    private DispatcherQueue? _dispatcherQueue;
    private XamlRoot? _xamlRoot;

    public void Attach(XamlRoot xamlRoot, DispatcherQueue dispatcherQueue)
    {
        _xamlRoot = xamlRoot;
        _dispatcherQueue = dispatcherQueue;
    }

    public async Task<bool> ConfirmAsync(CancellationToken cancellationToken = default)
    {
        var root = _xamlRoot;
        var dispatcher = _dispatcherQueue;
        if (root is null || dispatcher is null) return false;
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ContentDialog? active = null;
        using var registration = cancellationToken.Register(() =>
        {
            completion.TrySetCanceled(cancellationToken);
            dispatcher.TryEnqueue(() => active?.Hide());
        });
        async void Show()
        {
            if (completion.Task.IsCompleted) return;
            try
            {
                active = new ContentDialog
                {
                    XamlRoot = root,
                    RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                    Title = "官方组件需要管理员权限",
                    Content = new TextBlock
                    {
                        Text = "官方启动组件要求管理员权限，普通权限无法继续。\n\n选择“授权本次启动”后，Windows 会弹出当前账号的管理员授权提示。本次辅助进程和游戏将使用管理员权限，Aster 界面保持普通权限。\n\n仅对本次生效；取消后不会启动游戏，可改用推荐的官方启动器方式。",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 480, FontSize = 14
                    },
                    PrimaryButtonText = "授权本次启动", CloseButtonText = "取消",
                    PrimaryButtonStyle = Application.Current.Resources["ThemePrimaryButtonStyle"] as Style,
                    CloseButtonStyle = Application.Current.Resources["SecondaryButtonStyle"] as Style,
                    DefaultButton = ContentDialogButton.Close
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(active, "EndfieldBridgeElevationConsent");
                completion.TrySetResult(await active.ShowAsync() == ContentDialogResult.Primary);
            }
            catch (Exception) { completion.TrySetResult(false); }
            finally { active = null; }
        }
        if (dispatcher.HasThreadAccess) Show();
        else if (!dispatcher.TryEnqueue(Show)) completion.TrySetResult(false);
        return await completion.Task;
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
