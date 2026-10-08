using System.Diagnostics;

namespace AsterLauncher.App.Services;

public static class EmulatorLaunchService
{
    public static bool TryLaunch(string? executable, out string message)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)
            || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executable))
        {
            message = "模拟器 EXE 不存在或已移动，请重新选择。";
            return false;
        }
        try
        {
            // Start only the executable selected by the user. No shell command, game arguments or ownership tracking.
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            });
            message = "已请求打开模拟器，请在模拟器中进入明日方舟。";
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            message = "无法打开模拟器，请检查所选 EXE 和访问权限。";
            return false;
        }
    }
}
