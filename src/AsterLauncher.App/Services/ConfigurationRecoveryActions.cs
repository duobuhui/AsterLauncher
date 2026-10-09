using System.Diagnostics;
using AsterLauncher.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Services;

internal static class ConfigurationRecoveryActions
{
    internal static async Task<bool> ConfirmAndRestoreAsync(JsonConfigurationStore store, XamlRoot root, ElementTheme theme)
    {
        var preview = await store.ReadBackupAsync();
        if (preview is null)
        {
            await new ContentDialog
            {
                Title = "没有可用的配置备份",
                Content = "旧版已经覆盖的配置无法由程序备份还原。时长可以尝试从本地日志恢复；路径和隐藏列表仍可重新设置。",
                CloseButtonText = "知道了", XamlRoot = root, RequestedTheme = theme
            }.ShowAsync();
            return false;
        }
        var time = TimeSpan.FromSeconds(Math.Max(0, preview.TotalPlaySeconds));
        var message = $"备份时间：{preview.LastWrittenAt.ToLocalTime():yyyy/MM/dd HH:mm}\n"
            + $"游戏 {preview.GameCount} 款 · 隐藏 {preview.HiddenGameCount} 款\n"
            + $"完成会话 {preview.PlaySessionCount} 次 · 累计 {(long)time.TotalHours} 小时 {time.Minutes} 分钟\n\n"
            + "恢复将替换当前的路径、隐藏列表、启动方案和时长，再重新打开启动器。当前配置会保留为一份恢复前副本；抽卡档案和游戏文件不改动。";
        var dialog = new ContentDialog
        {
            Title = "恢复上一次有效配置",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "恢复并重启", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, XamlRoot = root, RequestedTheme = theme
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        await store.RestoreBackupAsync(preview);
        return true;
    }

    internal static async Task RestartAsync(XamlRoot root, ElementTheme theme)
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定启动器路径。");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.Environment["ASTERLAUNCHER_DATA_HOME"] = LauncherDataPaths.ResolveDataDirectory();
            using var process = Process.Start(start) ?? throw new InvalidOperationException("未能重新打开启动器。");
        }
        catch
        {
            // Restoration has already committed. Keep the old window modal until exit
            // so its stale in-memory configuration cannot be edited or restored again.
            try
            {
                await new ContentDialog
                {
                    Title = "配置已恢复，请重新打开启动器",
                    Content = "配置已经恢复，但未能自动重新打开启动器。已恢复的配置和现有备份会保留；关闭后，请从原来的入口重新打开。",
                    CloseButtonText = "关闭启动器", DefaultButton = ContentDialogButton.Close,
                    XamlRoot = root, RequestedTheme = theme
                }.ShowAsync();
            }
            catch
            {
                // A closing window may no longer host a dialog; it must still exit.
            }
        }

        Application.Current.Exit();
    }
}