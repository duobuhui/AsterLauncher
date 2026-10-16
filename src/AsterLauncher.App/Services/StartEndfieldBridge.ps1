param([string]$RequestPipe, [string]$IpcTemporaryDirectory)
# Internal worker: one bounded JSON request on stdin, never a saved command line.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$lease = $null
$session = $null
$resultPath = $null
$requestId = $null
$nativeReady = $false
$compileRoot = $null
$compileOwned = $false
$canPublish = $false

function Assert-PlainPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) -and
            (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'Linked paths are not supported.'
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Get-LocalPath($Value, [bool]$Directory) {
    if ($Value -isnot [string] -or $Value.Length -gt 32768 -or
        $Value -notmatch '^[A-Za-z]:[\\/]' -or $Value.Substring(2).Contains(':') -or $Value -match '[\p{Cc}]') { throw 'Invalid local path.' }
    $path = [IO.Path]::GetFullPath($Value)
    if ($path.Length -le 3) { throw 'A non-root local path is required.' }
    $path = $path.TrimEnd([char[]]@('\', '/'))
    Assert-PlainPath $path
    if ($Directory -and -not [IO.Directory]::Exists($path)) { throw 'Directory is unavailable.' }
    if (-not $Directory -and -not [IO.File]::Exists($path)) { throw 'File is unavailable.' }
    return $path
}

# Compiler scratch is disposable; a game's temporary identity must survive repeated launches.
# Derive it from the installed EXE path, never a request GUID, game version, or login data.
function Set-GameTemporaryEnvironment([string]$DataRoot, [string]$GameExecutable) {
    $verifiedData = Get-LocalPath $DataRoot $true
    $verifiedGame = Get-LocalPath $GameExecutable $false
    $identity = $verifiedGame.ToUpperInvariant()
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($hash.ComputeHash($utf8.GetBytes($identity))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
    $runtime = [IO.Path]::Combine($verifiedData, 'launch', 'runtime')
    $installation = [IO.Path]::Combine($runtime, $key)
    $temporary = [IO.Path]::Combine($installation, 'temp')
    foreach ($directory in @($runtime, $installation, $temporary)) {
        Assert-PlainPath $directory
        [void][IO.Directory]::CreateDirectory($directory)
        Assert-PlainPath $directory
    }
    # Only the helper's original compiler scratch is cleaned. Never inspect or package
    # runtime contents: SDK-owned state may live here and belongs to the game.
    $env:TEMP = $temporary
    $env:TMP = $temporary
    return $temporary
}

function Write-Result([string]$Status, [string]$ErrorCode, [int]$GamePid, [int]$ParentPid,
    [Nullable[int]]$NativeError = $null, $NativeStage = $null) {
    if ($null -eq $resultPath -or -not $canPublish) { return }
    if ($null -ne $NativeError -and ($NativeError -lt 0 -or $NativeError -gt 65535)) { $NativeError = $null }
    if ($NativeStage -notin @('initialize_attribute_list', 'update_attribute_list', 'create_process')) { $NativeStage = $null }
    $record = [ordered]@{
        schemaVersion = 1; requestId = $requestId; status = $Status; errorCode = $ErrorCode
        gamePid = $GamePid; parentPid = $ParentPid; nativeError = $NativeError; nativeStage = $NativeStage
    }
    $temporary = $resultPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Assert-PlainPath ([IO.Path]::GetDirectoryName($resultPath))
        Assert-PlainPath $resultPath
        $bytes = $utf8.GetBytes(($record | ConvertTo-Json -Compress))
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        if ([IO.File]::Exists($resultPath)) {
            [IO.File]::Replace($temporary, $resultPath, [System.Management.Automation.Language.NullString]::Value)
        }
        else { [IO.File]::Move($temporary, $resultPath) }
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

# Independently testable native API. The production entry never permits an unsigned-file bypass.
$nativeSource = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public sealed class EndfieldBridgeFailure : Exception
{
    public string Code { get; private set; }
    public int? NativeError { get; private set; }
    public string NativeStage { get; private set; }
    public EndfieldBridgeFailure(string code) : this(code, null, null) { }
    public EndfieldBridgeFailure(string code, int? nativeError, string nativeStage) : base(code)
    {
        Code = code;
        if (nativeError.HasValue && nativeError.Value >= 0 && nativeError.Value <= 65535) NativeError = nativeError;
        if (nativeStage == "initialize_attribute_list" || nativeStage == "update_attribute_list" || nativeStage == "create_process")
            NativeStage = nativeStage;
    }
}

public sealed class EndfieldBridgeSession : IDisposable
{
    private IntPtr parent, game, job;
    private bool disposed;
    public int ParentPid { get; private set; }
    public int GamePid { get; private set; }
    public string IsolationError { get; private set; }
    internal EndfieldBridgeSession(IntPtr parentHandle, int parentPid, IntPtr gameHandle,
        int gamePid, IntPtr cleanupJob, string isolationError)
    {
        parent = parentHandle; ParentPid = parentPid; game = gameHandle; GamePid = gamePid;
        job = cleanupJob; IsolationError = isolationError;
    }
    public void WaitForGameExit()
    {
        if (game != IntPtr.Zero) EndfieldBridgeNative.WaitForGame(game);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // A failed acknowledgement or GUI cancellation must not remove the official parent early.
        try { WaitForGameExit(); }
        finally
        {
            EndfieldBridgeNative.ReleaseOwnedParent(parent); parent = IntPtr.Zero;
            if (game != IntPtr.Zero) { EndfieldBridgeNative.CloseHandle(game); game = IntPtr.Zero; }
            if (job != IntPtr.Zero) { EndfieldBridgeNative.CloseHandle(job); job = IntPtr.Zero; }
        }
    }
}

public static class EndfieldBridgeNative
{
    private const uint CREATE_SUSPENDED = 0x00000004, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000, EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint QUERY_AND_SYNCHRONIZE = 0x00101000, INFINITE = 0xffffffff;
    private static readonly IntPtr ParentAttribute = new IntPtr(0x00020000);
    private static readonly IntPtr JobListAttribute = new IntPtr(0x0002000d);
    private static readonly Guid VerifyAction = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFO
    {
        public int cb; public IntPtr reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reservedSize; public IntPtr reservedBytes;
        public IntPtr standardInput, standardOutput, standardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX
    {
        public STARTUPINFO startupInfo; public IntPtr attributeList;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION
    {
        public IntPtr process, thread; public int processId, threadId;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long perProcessUserTimeLimit, perJobUserTimeLimit; public uint limitFlags;
        public UIntPtr minimumWorkingSetSize, maximumWorkingSetSize; public uint activeProcessLimit;
        public UIntPtr affinity; public uint priorityClass, schedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS
    {
        public ulong readOperationCount, writeOperationCount, otherOperationCount;
        public ulong readTransferCount, writeTransferCount, otherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION basicLimitInformation; public IO_COUNTERS ioInfo;
        public UIntPtr processMemoryLimit, jobMemoryLimit, peakProcessMemoryUsed, peakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WINTRUST_FILE_INFO
    {
        public uint size; [MarshalAs(UnmanagedType.LPWStr)] public string path;
        public IntPtr file, knownSubject;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WINTRUST_DATA
    {
        public uint size; public IntPtr policyCallbackData, sipClientData;
        public uint uiChoice, revocationChecks, unionChoice; public IntPtr fileInfo;
        public uint stateAction; public IntPtr stateData, urlReference;
        public uint providerFlags, uiContext; public IntPtr signatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROVIDER_CERT
    {
        public uint size; public IntPtr certificate;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CERT_CONTEXT
    {
        public uint encodingType; public IntPtr encoded; public uint encodedSize;
        public IntPtr certInfo, certStore;
    }

    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION information, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
        IntPtr value, UIntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags,
        IntPtr environment, string workingDirectory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
        IntPtr targetProcess, out IntPtr targetHandle, uint access, bool inherit, uint options);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WINTRUST_DATA data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index, bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CertGetNameStringW(IntPtr certificate, uint type, uint flags,
        [MarshalAs(UnmanagedType.LPStr)] string parameter, StringBuilder buffer, uint count);

    public static void VerifyOfficialFile(FileStream heldFile, string path, string expectedHash, string expectedSigner)
    {
        using (SHA256 hash = SHA256.Create())
        {
            heldFile.Position = 0;
            if (!String.Equals(Hex(hash.ComputeHash(heldFile)), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new EndfieldBridgeFailure("official_file_changed");
        }
        WINTRUST_FILE_INFO file = new WINTRUST_FILE_INFO();
        file.size = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
        file.path = path; file.file = heldFile.SafeFileHandle.DangerousGetHandle();
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
        WINTRUST_DATA data = new WINTRUST_DATA();
        data.size = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)); data.uiChoice = 2;
        data.unionChoice = 1; data.fileInfo = filePointer; data.stateAction = 1;
        data.providerFlags = 0x00001000 | 0x00002000;
        Guid action = VerifyAction;
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0)
                throw new EndfieldBridgeFailure("signature_untrusted");
            IntPtr provider = WTHelperProvDataFromStateData(data.stateData);
            IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            IntPtr providerCert = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (providerCert == IntPtr.Zero) throw new EndfieldBridgeFailure("signature_untrusted");
            PROVIDER_CERT certificate = (PROVIDER_CERT)Marshal.PtrToStructure(providerCert, typeof(PROVIDER_CERT));
            if (certificate.certificate == IntPtr.Zero) throw new EndfieldBridgeFailure("signature_untrusted");
            CERT_CONTEXT context = (CERT_CONTEXT)Marshal.PtrToStructure(certificate.certificate, typeof(CERT_CONTEXT));
            if (context.encoded == IntPtr.Zero || context.encodedSize == 0 || context.encodedSize > 1048576)
                throw new EndfieldBridgeFailure("signature_untrusted");
            byte[] encoded = new byte[(int)context.encodedSize];
            Marshal.Copy(context.encoded, encoded, 0, encoded.Length);
            string leafHash;
            using (SHA256 hash = SHA256.Create()) leafHash = Hex(hash.ComputeHash(encoded));
            if (!String.Equals(leafHash, expectedSigner, StringComparison.OrdinalIgnoreCase))
                throw new EndfieldBridgeFailure("signer_changed");
            uint count = CertGetNameStringW(certificate.certificate, 3, 0, "2.5.4.10", null, 0);
            if (count < 2 || count > 1024) throw new EndfieldBridgeFailure("publisher_untrusted");
            StringBuilder organization = new StringBuilder((int)count);
            if (CertGetNameStringW(certificate.certificate, 3, 0, "2.5.4.10", organization, count) != count)
                throw new EndfieldBridgeFailure("publisher_untrusted");
            string value = organization.ToString();
            string chinese = "\u4e0a\u6d77\u9e70\u89d2\u7f51\u7edc\u79d1\u6280\u6709\u9650\u516c\u53f8";
            if (!String.Equals(value, "Shanghai Hypergryph Network Technology Co., Ltd.", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(value, chinese, StringComparison.Ordinal))
                throw new EndfieldBridgeFailure("publisher_untrusted");
        }
        finally
        {
            data.stateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure(filePointer, typeof(WINTRUST_FILE_INFO));
            Marshal.FreeHGlobal(filePointer);
        }
    }

    public static EndfieldBridgeSession StartOwned(string official, string game, string workingDirectory, string[] arguments)
    {
        IntPtr job = IntPtr.Zero;
        PROCESS_INFORMATION parent = new PROCESS_INFORMATION(), child = new PROCESS_INFORMATION();
        bool transferred = false;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new EndfieldBridgeFailure("parent_job_unavailable");
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.basicLimitInformation.limitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
                throw new EndfieldBridgeFailure("parent_job_unavailable");
            // JOB_LIST assignment during creation closes the pre-assignment orphan window.
            parent = CreateWithAttribute(official, new string[0], Path.GetDirectoryName(official),
                CREATE_SUSPENDED | CREATE_NO_WINDOW, JobListAttribute, job, "official_process_creation_failed");
            if (parent.thread != IntPtr.Zero) { CloseHandle(parent.thread); parent.thread = IntPtr.Zero; }
            child = CreateWithAttribute(game, arguments, workingDirectory, CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW,
                ParentAttribute, parent.process, "game_process_creation_failed");
            if (child.thread != IntPtr.Zero) { CloseHandle(child.thread); child.thread = IntPtr.Zero; }
            bool isMember;
            string isolationError = null;
            if (!IsProcessInJob(child.process, job, out isMember) || isMember)
            {
                // Unexpected hosts must not cause cleanup to kill a game. Disarm the Job and
                // always wait for the game's natural exit before closing any remaining Job handle.
                limits.basicLimitInformation.limitFlags = JOB_OBJECT_LIMIT_BREAKAWAY_OK;
                SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                isolationError = "game_job_isolation_unavailable";
            }
            // Never transfer an all-access game handle to the lifetime keeper. A resource-starved
            // host may delay the acknowledgement, but cannot make us terminate the game or
            // remove its official parent. Retry using the original object until duplication works
            // or the game has already exited naturally.
            IntPtr observed;
            while (!DuplicateHandle(GetCurrentProcess(), child.process, GetCurrentProcess(), out observed,
                QUERY_AND_SYNCHRONIZE, false, 0))
            {
                if (WaitForSingleObject(child.process, 100) == 0)
                {
                    CloseHandle(child.process); child.process = IntPtr.Zero;
                    break;
                }
            }
            if (child.process != IntPtr.Zero)
            {
                CloseHandle(child.process); child.process = observed;
            }
            EndfieldBridgeSession result = new EndfieldBridgeSession(parent.process, parent.processId,
                child.process, child.processId, job, isolationError);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
            {
                if (child.process != IntPtr.Zero) WaitForGame(child.process);
                ReleaseOwnedParent(parent.process);
                if (child.thread != IntPtr.Zero) CloseHandle(child.thread);
                if (child.process != IntPtr.Zero) CloseHandle(child.process);
                if (parent.thread != IntPtr.Zero) CloseHandle(parent.thread);
                if (job != IntPtr.Zero) CloseHandle(job);
            }
        }
    }

    private static PROCESS_INFORMATION CreateWithAttribute(string executable, string[] arguments,
        string workingDirectory, uint flags, IntPtr attribute, IntPtr attributedHandle, string failure)
    {
        UIntPtr size = UIntPtr.Zero;
        bool querySucceeded = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        int queryError = querySucceeded ? 0 : Marshal.GetLastWin32Error();
        // The initial size query is expected to fail with ERROR_INSUFFICIENT_BUFFER.
        if ((!querySucceeded && queryError != 122) || size == UIntPtr.Zero || size.ToUInt64() > 1048576)
            throw new EndfieldBridgeFailure(failure, queryError, "initialize_attribute_list");
        IntPtr list = Marshal.AllocHGlobal(checked((int)size.ToUInt64()));
        IntPtr value = Marshal.AllocHGlobal(IntPtr.Size);
        bool initialized = false;
        try
        {
            initialized = InitializeProcThreadAttributeList(list, 1, 0, ref size);
            if (!initialized)
            {
                int nativeError = Marshal.GetLastWin32Error();
                throw new EndfieldBridgeFailure(failure, nativeError, "initialize_attribute_list");
            }
            Marshal.WriteIntPtr(value, attributedHandle);
            if (!UpdateProcThreadAttribute(list, 0, attribute, value, new UIntPtr((uint)IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
            {
                int nativeError = Marshal.GetLastWin32Error();
                throw new EndfieldBridgeFailure(failure, nativeError, "update_attribute_list");
            }
            STARTUPINFOEX startup = new STARTUPINFOEX();
            startup.startupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX)); startup.attributeList = list;
            PROCESS_INFORMATION result;
            StringBuilder command = new StringBuilder(BuildCommandLine(executable, arguments));
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                flags | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero, workingDirectory, ref startup, out result))
            {
                int nativeError = Marshal.GetLastWin32Error();
                throw new EndfieldBridgeFailure(failure, nativeError, "create_process");
            }
            return result;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(value); Marshal.FreeHGlobal(list);
        }
    }

    public static string BuildCommandLine(string executable, string[] arguments)
    {
        StringBuilder result = new StringBuilder(Quote(executable));
        foreach (string argument in arguments) { result.Append(' '); result.Append(Quote(argument)); }
        if (result.Length > 32766) throw new EndfieldBridgeFailure("arguments_invalid");
        return result.ToString();
    }

    private static string Quote(string value)
    {
        if (value == null || value.IndexOf('\0') >= 0) throw new EndfieldBridgeFailure("arguments_invalid");
        StringBuilder result = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"') { result.Append('\\', backslashes * 2 + 1); result.Append('"'); }
            else { result.Append('\\', backslashes); result.Append(ch); }
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2); result.Append('"');
        return result.ToString();
    }

    private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }

    internal static void WaitForGame(IntPtr game)
    {
        // Wait on the original kernel object, never a potentially reused PID.
        while (WaitForSingleObject(game, INFINITE) == 0xffffffff) System.Threading.Thread.Sleep(1000);
    }

    internal static void ReleaseOwnedParent(IntPtr parent)
    {
        if (parent == IntPtr.Zero) return;
        // The sole termination call only receives our newly created suspended parent's handle.
        // No name lookup, PID reopening, existing-launcher adoption, or process-tree kill.
        if (WaitForSingleObject(parent, 0) == 258) { TerminateProcess(parent, 0); WaitForSingleObject(parent, 5000); }
        CloseHandle(parent);
    }
}
'@

try {
    $inputStream = $null
    $memory = [IO.MemoryStream]::new()
    try {
        if (-not [string]::IsNullOrEmpty($RequestPipe) -or -not [string]::IsNullOrEmpty($IpcTemporaryDirectory)) {
            if ($RequestPipe -cnotmatch '^AsterLauncher\.EndfieldBridge\.[a-f0-9]{32}$' -or
                [string]::IsNullOrEmpty($IpcTemporaryDirectory)) { throw 'Invalid request channel.' }
            $ipcTemporary = Get-LocalPath $IpcTemporaryDirectory $true
            $env:TEMP = $ipcTemporary
            $env:TMP = $ipcTemporary
            $env:PSModuleAnalysisCachePath = [IO.Path]::Combine($ipcTemporary, 'ModuleAnalysisCache')
            Set-Location -LiteralPath $ipcTemporary
            [Environment]::CurrentDirectory = $ipcTemporary
            $inputStream = [IO.Pipes.NamedPipeClientStream]::new('.', $RequestPipe, [IO.Pipes.PipeDirection]::In)
            $inputStream.Connect(10000)
        }
        else { $inputStream = [Console]::OpenStandardInput() }
        $buffer = [byte[]]::new(1024)
        while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $count -gt 32768) { throw 'Request is too large.' }
            $memory.Write($buffer, 0, $count)
        }
        $request = $utf8.GetString($memory.ToArray()) | ConvertFrom-Json
    }
    finally { $memory.Dispose(); if ($null -ne $inputStream) { $inputStream.Dispose() } }
    if ($null -eq $request -or $request.schemaVersion -ne 1 -or
        $request.requestId -isnot [string] -or $request.requestId -cnotmatch '^[a-f0-9]{32}$') { throw 'Invalid request.' }
    $requestId = $request.requestId
    $data = Get-LocalPath $request.dataRoot $true
    $launchRoot = [IO.Path]::Combine($data, 'launch')
    $temporaryRoot = [IO.Path]::Combine($launchRoot, 'temp')
    $sessionRoot = [IO.Path]::Combine($launchRoot, $requestId)
    foreach ($directory in @($launchRoot, $temporaryRoot, $sessionRoot)) {
        Assert-PlainPath $directory
        [void][IO.Directory]::CreateDirectory($directory)
    }
    $env:TEMP = $temporaryRoot
    $env:TMP = $temporaryRoot
    $env:PSModuleAnalysisCachePath = [IO.Path]::Combine($temporaryRoot, 'ModuleAnalysisCache')
    Set-Location -LiteralPath $temporaryRoot
    [Environment]::CurrentDirectory = $temporaryRoot
    $resultPath = [IO.Path]::Combine($sessionRoot, 'result.json')
    $lockPath = [IO.Path]::Combine($sessionRoot, 'active.lock')
    Assert-PlainPath $lockPath
    $lease = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
    if ([IO.File]::Exists($resultPath)) { throw 'Request was already used.' }
    $canPublish = $true
    $official = Get-LocalPath $request.officialExecutable $false
    $game = Get-LocalPath $request.gameExecutable $false
    $working = Get-LocalPath $request.workingDirectory $true
    if (-not [string]::Equals([IO.Path]::GetFileName($official), 'Games.exe', [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFileName($game), 'Endfield.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid executable identity.'
    }
    if ($request.expectedFileSha256 -isnot [string] -or $request.expectedFileSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        $request.expectedSignerThumbprint -isnot [string] -or $request.expectedSignerThumbprint -notmatch '^[a-fA-F0-9]{64}$' -or
        $request.arguments -isnot [Array] -or $request.arguments.Count -gt 256) { throw 'Invalid request.' }
    foreach ($argument in $request.arguments) {
        if ($argument -isnot [string] -or $argument.Length -gt 32766 -or $argument.Contains([char]0)) { throw 'Invalid argument.' }
    }
    $compileRoot = [IO.Path]::Combine($temporaryRoot, 'compile-' + $requestId)
    Assert-PlainPath $compileRoot
    if ([IO.Directory]::Exists($compileRoot) -or [IO.File]::Exists($compileRoot)) { throw 'Compilation directory was already used.' }
    [void][IO.Directory]::CreateDirectory($compileRoot)
    $compileOwned = $true
    $env:TEMP = $compileRoot
    $env:TMP = $compileRoot
    Add-Type -TypeDefinition $nativeSource -ErrorAction Stop
    $nativeReady = $true
    $heldOfficial = [IO.FileStream]::new($official, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        [EndfieldBridgeNative]::VerifyOfficialFile($heldOfficial, $official,
            $request.expectedFileSha256, $request.expectedSignerThumbprint)
        $cancelPath = [IO.Path]::Combine($sessionRoot, 'cancel.request')
        Assert-PlainPath $cancelPath
        if ([IO.File]::Exists($cancelPath)) { throw [EndfieldBridgeFailure]::new('request_cancelled') }
        $null = Set-GameTemporaryEnvironment $data $game
        $session = [EndfieldBridgeNative]::StartOwned($official, $game, $working, [string[]]$request.arguments)
    }
    finally { $heldOfficial.Dispose() }
    $gamePidValue = $session.GamePid
    $parentPidValue = $session.ParentPid
    Write-Result 'started' $(if ($session.IsolationError) { $session.IsolationError } else { '' }) $gamePidValue $parentPidValue
    $session.WaitForGameExit()
    $session.Dispose()
    $session = $null
    Write-Result 'completed' '' $gamePidValue $parentPidValue
    exit 0
}
catch {
    # Never persist input, paths, command lines, or arbitrary exception text.
    $code = 'request_invalid'
    $nativeError = $null
    $nativeStage = $null
    $exception = $_.Exception
    while ($null -ne $exception) {
        if ($exception.GetType().Name -eq 'EndfieldBridgeFailure') {
            $code = $exception.Code; $nativeError = $exception.NativeError; $nativeStage = $exception.NativeStage; break
        }
        $exception = $exception.InnerException
    }
    if ($nativeReady -and $code -eq 'request_invalid') { $code = 'native_operation_failed' }
    if ($null -ne $session) {
        $gamePidValue = $session.GamePid
        $parentPidValue = $session.ParentPid
        try { Write-Result 'started' 'result_unavailable' $gamePidValue $parentPidValue } catch { }
        $session.Dispose()
        $session = $null
        try { Write-Result 'completed' 'result_unavailable' $gamePidValue $parentPidValue } catch { }
    }
    else { try { Write-Result 'failed' $code 0 0 $nativeError $nativeStage } catch { } }
    exit 1
}
finally {
    if ($null -ne $session) { $session.Dispose() }
    if ($null -ne $lease) { $lease.Dispose() }
    if ($compileOwned -and $null -ne $compileRoot) {
        try {
            # This private compiler directory was newly created by this exact request. No user
            # directory, shared cache, or other session is removed; links/unknown subfolders stop cleanup.
            Assert-PlainPath $compileRoot
            $expectedCompile = [IO.Path]::Combine($data, 'launch', 'temp', 'compile-' + $requestId)
            if (-not [string]::Equals($compileRoot, $expectedCompile, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid cleanup scope.' }
            if ([IO.Directory]::GetDirectories($compileRoot).Length -ne 0) { throw 'Unknown compiler subdirectory.' }
            foreach ($file in [IO.Directory]::EnumerateFiles($compileRoot)) { Assert-PlainPath $file }
            [IO.Directory]::Delete($compileRoot, $true)
        }
        catch { } # An externally held compiler file must not affect a game or delete another scope.
    }
}
