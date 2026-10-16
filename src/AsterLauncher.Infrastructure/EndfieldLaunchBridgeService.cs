using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AsterLauncher.Core;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AsterLauncher.Infrastructure;

public sealed record EndfieldBridgeLaunchResult(int GamePid, int ParentPid, bool HasExited, string? ErrorCode = null,
    int? NativeError = null, string? NativeStage = null);

public sealed class EndfieldBridgeLaunchException : InvalidOperationException
{
    public string Code { get; }
    public int? NativeError { get; }
    public string? NativeStage { get; }
    public bool RequiresElevation => Code == "official_process_creation_failed"
        && NativeStage == "create_process" && NativeError == 740;
    internal EndfieldBridgeLaunchException(string code, string message, int? nativeError = null, string? nativeStage = null) : base(message)
    {
        Code = code;
        NativeError = nativeError;
        NativeStage = nativeStage;
    }
}

/// <summary>Starts the signed-parent keeper and receives a bounded acknowledgement. It never terminates processes.</summary>
public sealed class EndfieldLaunchBridgeService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> KeeperCodes = new(StringComparer.Ordinal)
    {
        "arguments_invalid", "game_job_isolation_unavailable", "game_process_creation_failed",
        "official_file_changed", "official_process_creation_failed", "parent_job_unavailable",
        "publisher_untrusted", "signature_untrusted", "signer_changed", "request_invalid",
        "native_operation_failed", "result_unavailable"
    };
    private readonly EndfieldLauncherTrust _trust;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly TimeSpan _initialTimeout;

    public EndfieldLaunchBridgeService() : this(new EndfieldLauncherTrust(), Process.Start, TimeSpan.FromSeconds(15)) { }
    internal EndfieldLaunchBridgeService(EndfieldLauncherTrust trust, Func<ProcessStartInfo, Process?> start, TimeSpan initialTimeout)
    {
        _trust = trust;
        _start = start;
        _initialTimeout = initialTimeout;
    }

    public Task<EndfieldBridgeLaunchResult> LaunchAsync(string gameExecutable, string officialExecutable,
        string? workingDirectory, IReadOnlyList<string> arguments, string dataRoot, string helperScript,
        CancellationToken cancellationToken = default) =>
        LaunchRouteAsync(gameExecutable, officialExecutable, workingDirectory, arguments, dataRoot, helperScript, false, cancellationToken);

    /// <summary>Explicit opt-in route. The caller must obtain consent before requesting the Windows UAC prompt.</summary>
    public Task<EndfieldBridgeLaunchResult> LaunchElevatedAsync(string gameExecutable, string officialExecutable,
        string? workingDirectory, IReadOnlyList<string> arguments, string dataRoot, string helperScript,
        CancellationToken cancellationToken = default) =>
        LaunchRouteAsync(gameExecutable, officialExecutable, workingDirectory, arguments, dataRoot, helperScript, true, cancellationToken);

    private async Task<EndfieldBridgeLaunchResult> LaunchRouteAsync(string gameExecutable, string officialExecutable,
        string? workingDirectory, IReadOnlyList<string> arguments, string dataRoot, string helperScript,
        bool elevated, CancellationToken cancellationToken)
    {
        try
        {
            return await LaunchCoreAsync(gameExecutable, officialExecutable, workingDirectory, arguments,
                dataRoot, helperScript, elevated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw Failure("实验性启动暂时无法确认，请检查游戏状态后再操作；不会自动重新启动。");
        }
    }

    private async Task<EndfieldBridgeLaunchResult> LaunchCoreAsync(string gameExecutable, string officialExecutable,
        string? workingDirectory, IReadOnlyList<string> arguments, string dataRoot, string helperScript,
        bool elevated, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var game = RequireLocalPath(gameExecutable, ".exe", true);
        var official = RequireLocalPath(officialExecutable, ".exe", true);
        var script = RequireLocalPath(helperScript, ".ps1", true);
        var data = RequireLocalPath(dataRoot, null, false);
        var working = RequireLocalPath(string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(game)! : workingDirectory, null, false);
        if (!Directory.Exists(working) || !Path.GetFileName(game).Equals("Endfield.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(official).Equals("Games.exe", StringComparison.OrdinalIgnoreCase))
            throw Failure("请选择终末地游戏入口和已验证的官方 Games.exe 组件。");
        if (arguments is null || arguments.Count > 256 || arguments.Any(value => value is null || value.Length > 8192 || value.Contains('\0')))
            throw Failure("启动参数过长或包含无法使用的内容。");

        var copiedArguments = arguments.ToArray();
        EnsureSafeDirectory(data);
        var launchRoot = Path.Combine(data, "launch");
        var temporary = Path.Combine(launchRoot, "temp");
        EnsureSafeDirectory(temporary);
        CleanCompletedSessions(launchRoot);
        var requestId = Guid.NewGuid().ToString("N");
        var requestDirectory = Path.Combine(launchRoot, requestId);

        var resultPath = Path.Combine(requestDirectory, "result.json");

        await using var pinned = new FileStream(official, FileMode.Open, FileAccess.Read, FileShare.Read);
        var trust = await _trust.VerifyAsync(official, cancellationToken).ConfigureAwait(false);
        if (!trust.CanUseAsBridgeParent || string.IsNullOrEmpty(trust.CertificateSha256))
            throw Failure("无法验证官方 Games.exe 的鹰角数字签名，请改用官方启动器接管。", "signature_untrusted");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(pinned, cancellationToken).ConfigureAwait(false));
        var request = new BridgeRequest(1, requestId, data, game, official, working, copiedArguments, hash, trust.CertificateSha256);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (bytes.Length > 32768) throw Failure("启动参数总长度超过实验性启动桥的限制。", "arguments_invalid");
        EnsureSafeDirectory(requestDirectory);


        // A random name and a same-user ACL are both required: the client PID is also checked
        // before any potentially sensitive game arguments are sent to an elevated helper.
        var pipeName = "AsterLauncher.EndfieldBridge." + Guid.NewGuid().ToString("N");
        using var pipe = elevated ? CreateRequestPipe(pipeName) : null;
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = temporary, UseShellExecute = elevated, CreateNoWindow = !elevated
        };
        var helperArguments = new List<string>
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script
        };
        if (elevated)
        {
            info.Verb = "runas";
            // ShellExecute cannot redirect stdin or replace the environment. The helper sets its
            // private E:-side temporary environment before reading the pipe or compiling native code.
            helperArguments.AddRange(["-RequestPipe", pipeName, "-IpcTemporaryDirectory", temporary]);
            info.Arguments = ArgumentTokenizer.Join(helperArguments);
        }
        else
        {
            info.RedirectStandardInput = true;
            info.StandardInputEncoding = new UTF8Encoding(false);
            foreach (var argument in helperArguments) info.ArgumentList.Add(argument);
            info.Environment["TEMP"] = temporary;
            info.Environment["TMP"] = temporary;
            info.Environment["PSModuleAnalysisCachePath"] = Path.Combine(temporary, "PowerShellModuleAnalysisCache");
        }

        Process? keeper = null;
        var started = false;
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? initialWait = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            keeper = _start(info) ?? throw Failure("实验性启动桥未能启动，请改用官方启动器接管。");
            started = true;
            // ShellExecute returns after the user's UAC decision. That human response time must
            // not consume the initial connection/acknowledgement timeout.
            timeout = new CancellationTokenSource(_initialTimeout);
            initialWait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            if (pipe is not null)
            {
                await pipe.WaitForConnectionAsync(initialWait.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != keeper.Id)
                {
                    TryRequestCancellation(requestDirectory);
                    throw Failure("实验性启动桥连接身份不匹配；未发送游戏参数，也不会重试。", "ipc_peer_mismatch");
                }
                await pipe.WriteAsync(bytes, initialWait.Token).AsTask().WaitAsync(initialWait.Token).ConfigureAwait(false);
                await pipe.FlushAsync(initialWait.Token).ConfigureAwait(false);
                // EOF is part of the bounded JSON protocol. It is sent only after client identity is checked.
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                // The ordinary route retains bounded UTF-8 stdin, with no game arguments on the command line.
                await keeper.StandardInput.BaseStream.WriteAsync(bytes, initialWait.Token).AsTask().WaitAsync(initialWait.Token).ConfigureAwait(false);
                await keeper.StandardInput.BaseStream.FlushAsync(initialWait.Token).ConfigureAwait(false);
                keeper.StandardInput.Close();
            }
            while (true)
            {
                initialWait.Token.ThrowIfCancellationRequested();
                EnsureSafeExistingDirectory(requestDirectory);
                var result = await TryReadResultAsync(resultPath, requestId, initialWait.Token).ConfigureAwait(false);
                if (result is not null) return result;
                if (keeper.HasExited)
                {
                    // The keeper may publish a final completed acknowledgement between the first read and exit check.
                    result = await TryReadResultAsync(resultPath, requestId, initialWait.Token).ConfigureAwait(false);
                    if (result is not null) return result;
                    throw Failure("实验性启动桥已退出，尚未确认游戏启动；请检查后再操作。", "keeper_exited");
                }
                await Task.Delay(100, initialWait.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (started)
        {
            TryRequestCancellation(requestDirectory);
            throw Failure(cancellationToken.IsCancellationRequested
                ? "已取消等待启动结果。游戏可能仍在启动，请先确认状态，避免重复启动。"
                : "尚未确认游戏启动结果。游戏可能仍在启动，请先确认状态，避免重复启动。", cancellationToken.IsCancellationRequested ? "wait_cancelled" : "wait_timeout");
        }
        catch (System.ComponentModel.Win32Exception exception) when (elevated && exception.NativeErrorCode == 1223)
        {
            throw Failure("已取消 Windows 管理员授权，未重试启动。", "elevation_cancelled");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (started) TryRequestCancellation(requestDirectory);
            throw Failure(started ? "启动结果暂时无法确认，请先检查游戏状态，避免重复启动。"
                : "实验性启动桥未能启动，请改用官方启动器接管。");
        }
        finally
        {
            initialWait?.Dispose();
            timeout?.Dispose();
            // Dispose only our controller-side handles. No process or process tree is killed on timeout/cancellation.
            keeper?.Dispose();
        }
    }

    internal static NamedPipeServerStream CreateRequestPipe(string pipeName)
    {
        const string prefix = "AsterLauncher.EndfieldBridge.";
        if (!pipeName.StartsWith(prefix, StringComparison.Ordinal) || pipeName.Length != prefix.Length + 32
            || pipeName[prefix.Length..].Any(value => !(value is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Failure("实验性启动通信名称无效。", "request_invalid");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw Failure("无法确认当前 Windows 用户，未请求管理员授权。", "ipc_identity_unavailable");
        // CurrentUserOnly uses the token Owner SID on Windows, which can differ between a
        // user's ordinary and elevated tokens. Grant the actual User SID across that boundary,
        // without granting Administrators, Everyone, or inherited access to other accounts.
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security, HandleInheritability.None);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    private static void TryRequestCancellation(string requestDirectory)
    {
        try
        {
            EnsureSafeExistingDirectory(requestDirectory);
            var path = Path.Combine(requestDirectory, "cancel.request");
            EnsureSafeAncestors(path);
            // Stop only an uncommitted future creation. A running game and keeper are never terminated.
            using var signal = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private static async Task<EndfieldBridgeLaunchResult?> TryReadResultAsync(string path, string requestId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Failure("启动结果文件不符合安全要求。");
        byte[] bytes;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is <= 0 or > 4096) throw Failure("启动结果格式无效，未重新尝试启动。");
            bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("requestId").GetString() != requestId)
                throw Failure("启动结果不匹配，未重新尝试启动。");
            var status = root.GetProperty("status").GetString();
            var rawCode = root.TryGetProperty("errorCode", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
            var fixedCode = rawCode is not null && KeeperCodes.Contains(rawCode) ? rawCode : null;
            var nativeError = ReadNativeError(root);
            var nativeStage = ReadNativeStage(root);
            if (status == "failed")
                throw Failure(nativeError == 740 && nativeStage == "create_process" && fixedCode == "official_process_creation_failed"
                    ? "官方启动组件需要 Windows 管理员授权。"
                    : "实验性启动未完成，请改用官方启动器接管；不会自动重试。", fixedCode ?? "bridge_failed", nativeError, nativeStage);
            if (status is not ("started" or "completed")) throw Failure("启动结果状态无效，未重新尝试启动。");
            var gamePid = root.GetProperty("gamePid").GetInt32();
            var parentPid = root.GetProperty("parentPid").GetInt32();
            if (gamePid <= 0 || parentPid <= 0 || gamePid == parentPid) throw Failure("启动结果进程标识无效，未重新尝试启动。");
            return new(gamePid, parentPid, status == "completed", fixedCode, nativeError, nativeStage);
        }
        catch (Exception exception) when (exception is not EndfieldBridgeLaunchException && exception is (JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException))
        {
            throw Failure("启动结果格式无效，未重新尝试启动。");
        }
    }

    private static int? ReadNativeError(JsonElement root) =>
        root.TryGetProperty("nativeError", out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var error) && error is >= 0 and <= 65535 ? error : null;

    private static string? ReadNativeStage(JsonElement root) =>
        root.TryGetProperty("nativeStage", out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is "initialize_attribute_list" or "update_attribute_list" or "create_process" ? value.GetString() : null;

    internal static void CleanCompletedSessions(string launchRoot)
    {
        try
        {
            EnsureSafeExistingDirectory(launchRoot);
            foreach (var directory in Directory.EnumerateDirectories(launchRoot).Take(128))
            {
                var id = Path.GetFileName(directory);
                if (id.Length != 32 || id.Any(value => !(value is >= '0' and <= '9' or >= 'a' and <= 'f'))) continue;
                try
                {
                    EnsureSafeExistingDirectory(directory);
                    var entries = Directory.EnumerateFileSystemEntries(directory).Take(5).ToArray();
                    if (entries.Any(entry => Directory.Exists(entry) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0
                        || Path.GetFileName(entry) is not ("result.json" or "cancel.request" or "active.lock"))) continue;
                    var resultPath = Path.Combine(directory, "result.json");
                    using (var result = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    {
                        if (result.Length is <= 0 or > 4096) continue;
                        var bytes = new byte[(int)result.Length];
                        result.ReadExactly(bytes);
                        using var document = JsonDocument.Parse(bytes);
                        var root = document.RootElement;
                        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("requestId").GetString() != id
                            || root.GetProperty("status").GetString() != "completed") continue;
                    }
                    // A live worker holds this lease. Even completed receipts are kept while that worker is finishing.
                    using (var lease = new FileStream(Path.Combine(directory, "active.lock"), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
                    {
                        File.Delete(resultPath);
                        File.Delete(Path.Combine(directory, "cancel.request"));
                    }
                    Directory.Delete(directory, false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                    or JsonException or KeyNotFoundException or FormatException or OverflowException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private static string RequireLocalPath(string? value, string? extension, bool requireFile)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw Failure("启动路径无效。");
        try
        {
            if (!Path.IsPathFullyQualified(value)) throw Failure("启动路径必须是本地绝对路径。");
            var path = Path.GetFullPath(value);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':' || path[2..].Contains(':')
                || extension is not null && !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)
                || requireFile && !File.Exists(path)) throw Failure("启动路径无效或文件不存在。");
            EnsureSafeAncestors(path);
            return path;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure("启动路径无效。");
        }
    }

    private static void EnsureSafeDirectory(string path)
    {
        EnsureSafeAncestors(path);
        Directory.CreateDirectory(path);
        EnsureSafeExistingDirectory(path);
    }
    private static void EnsureSafeExistingDirectory(string path)
    {
        EnsureSafeAncestors(path);
        if (!Directory.Exists(path)) throw Failure("实验性启动数据目录不可用。");
    }
    private static void EnsureSafeAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Failure("实验性启动路径不能经过重解析点。");
    }
    private static EndfieldBridgeLaunchException Failure(string message, string code = "bridge_unconfirmed", int? nativeError = null, string? nativeStage = null) =>
        new(code, message, nativeError, nativeStage);
    private sealed record BridgeRequest(int SchemaVersion, string RequestId, string DataRoot, string GameExecutable,
        string OfficialExecutable, string WorkingDirectory, IReadOnlyList<string> Arguments, string ExpectedFileSha256, string ExpectedSignerThumbprint);
}
