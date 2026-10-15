param([Parameter(Mandatory = $true)][string]$DataRoot)

# Observe only explicitly requested processes. Never launch or stop a game.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$observerLock = $null
$active = @{}

function Assert-PlainPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Playtime storage cannot cross a filesystem link.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Read-Record([string]$Path) {
    Assert-PlainPath $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::Read -bor [IO.FileShare]::Delete))
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt 65536) { throw 'Invalid record length.' }
        $reader = [IO.StreamReader]::new($stream, $utf8, $false, 1024, $true)
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Read-Instant($Value) {
    if ($Value -isnot [string] -or
        $Value -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$') {
        throw 'Invalid timestamp.'
    }
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($Value, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None, [ref]$parsed)) { throw 'Invalid timestamp.' }
    return $parsed.ToUniversalTime()
}

function Get-Identity($Record, [string]$FileName, [bool]$Request) {
    if ($null -eq $Record -or $Record.schemaVersion -ne 1 -or $Record.sessionId -isnot [string] -or
        $Record.sessionId -cnotmatch '^[a-f0-9]{64}$' -or $Record.sessionId -cne $FileName -or
        $Record.gameId -isnot [string] -or [string]::IsNullOrWhiteSpace($Record.gameId) -or
        $Record.gameId.Length -gt 256 -or $Record.gameId -match '[\p{Cc}]' -or
        $Record.executablePath -isnot [string] -or $Record.executablePath.Length -gt 32768 -or
        $Record.executablePath -match '[\p{Cc}]' -or
        $Record.executablePath -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))' -or
        $Record.isComplete -isnot [bool]) {
        throw 'Invalid session identity.'
    }
    $processIdValue = 0
    if (-not [int]::TryParse([string]$Record.processId, [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$processIdValue) -or $processIdValue -le 0) {
        throw 'Invalid process identity.'
    }
    $started = Read-Instant $Record.startedAt
    $observed = Read-Instant $Record.observedUntil
    if ($started -lt [DateTimeOffset]::FromUnixTimeSeconds(0) -or $observed -lt $started -or
        $observed -gt [DateTimeOffset]::UtcNow -or ($Request -and ($Record.isComplete -or $observed -ne $started))) {
        throw 'Invalid observed interval.'
    }
    $gameId = $Record.gameId.Trim()
    $executable = [IO.Path]::GetFullPath($Record.executablePath).TrimEnd([char[]]@('\', '/'))
    if (-not [string]::Equals([IO.Path]::GetExtension($executable), '.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A game executable is required.'
    }
    $identity = $gameId.ToLowerInvariant() + [char]10 + $executable.ToLowerInvariant() + [char]10 +
        $processIdValue.ToString([Globalization.CultureInfo]::InvariantCulture) + [char]10 +
        $started.UtcDateTime.Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $expected = ([BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($identity)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($expected -cne $Record.sessionId) { throw 'Invalid session identity.' }
    return [pscustomobject]@{
        SessionId = $Record.sessionId; GameId = $gameId; ExecutablePath = $executable
        ProcessId = $processIdValue; StartedAt = $started; ObservedUntil = $observed; IsComplete = $Record.isComplete
    }
}

function Write-ObservationCore($Identity, [DateTimeOffset]$Until, [bool]$Complete) {
    if ($Until -lt $Identity.StartedAt) { throw 'Invalid observed interval.' }
    $path = [IO.Path]::Combine($receiptsRoot, $Identity.SessionId + '.json')
    if ([IO.File]::Exists($path)) {
        $previous = Get-Identity (Read-Record $path) $Identity.SessionId $false
        if ($previous.IsComplete -or $previous.ObservedUntil -gt $Until) { return }
    }
    $record = [ordered]@{
        schemaVersion = 1; sessionId = $Identity.SessionId; gameId = $Identity.GameId
        executablePath = $Identity.ExecutablePath; processId = $Identity.ProcessId
        startedAt = $Identity.StartedAt.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        observedUntil = $Until.ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        isComplete = $Complete
    }
    $temporary = [IO.Path]::Combine($receiptsRoot, $Identity.SessionId + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Assert-PlainPath $receiptsRoot
        $bytes = $utf8.GetBytes(($record | ConvertTo-Json -Compress))
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        Assert-PlainPath $path
        if ([IO.File]::Exists($path)) { [IO.File]::Replace($temporary, $path, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $path) }
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Write-Observation($Identity, [DateTimeOffset]$Until, [bool]$Complete) {
    $path = [IO.Path]::Combine($playtimeRoot, 'receipts.write.lock')
    Assert-PlainPath $path
    $lease = [IO.FileStream]::new($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
    try { Write-ObservationCore $Identity $Until $Complete }
    finally { $lease.Dispose() }
}

function Save-Observation($Identity, [DateTimeOffset]$Until, [bool]$Complete) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try { Write-Observation $Identity $Until $Complete; return }
        catch {
            $exception = $_.Exception
            $ioFailure = $exception -is [IO.IOException] -or $exception.InnerException -is [IO.IOException]
            if (-not $ioFailure -or $attempt -eq 2) { throw }
            Start-Sleep -Milliseconds 50
        }
    }
}

function Save-SessionObservation($Session, [DateTimeOffset]$Until, [bool]$Complete) {
    try {
        Save-Observation $Session.Identity $Until $Complete
        $Session.SaveFailedAt = [DateTimeOffset]::MinValue
        return $true
    }
    catch {
        $failureTime = [DateTimeOffset]::UtcNow
        if ($Session.SaveFailedAt -eq [DateTimeOffset]::MinValue) { $Session.SaveFailedAt = $failureTime }
        if (($failureTime - $Session.SaveFailedAt).TotalSeconds -ge 15) { throw }
        # Keep the real handle across retries, including after the process has already exited.
        return $false
    }
}

function Retire-Request([string]$Path, [string]$Suffix) {
    try {
        Assert-PlainPath $Path
        if ($Suffix -eq '.done') {
            [IO.File]::Delete($Path)
            return
        }
        $destination = $Path + $Suffix
        Assert-PlainPath $destination
        if (-not [IO.File]::Exists($destination)) { [IO.File]::Move($Path, $destination) }
        else { [IO.File]::Delete($Path) }
        $retired = @([IO.Directory]::EnumerateFiles($requestsRoot, '*.json.*', [IO.SearchOption]::TopDirectoryOnly) |
            Where-Object { [IO.Path]::GetFileName($_) -cmatch '^[a-f0-9]{64}\.json\.(invalid|unavailable)$' } |
            Sort-Object { [IO.File]::GetLastWriteTimeUtc($_) } -Descending)
        foreach ($oldPath in ($retired | Select-Object -Skip 32)) {
            Assert-PlainPath $oldPath
            [IO.File]::Delete($oldPath)
        }
    }
    catch { } # A racing replacement must not stop other sessions.
}

try {
    if ($DataRoot -notmatch '^[A-Za-z]:[\\/]') { throw 'An absolute local data directory is required.' }
    $data = [IO.Path]::GetFullPath($DataRoot).TrimEnd([char[]]@('\', '/'))
    $playtimeRoot = [IO.Path]::Combine($data, 'playtime')
    $requestsRoot = [IO.Path]::Combine($playtimeRoot, 'requests')
    $receiptsRoot = [IO.Path]::Combine($playtimeRoot, 'receipts')
    $temporaryRoot = [IO.Path]::Combine($playtimeRoot, 'temp')
    foreach ($directory in @($playtimeRoot, $requestsRoot, $receiptsRoot, $temporaryRoot)) {
        Assert-PlainPath $directory
        [void][IO.Directory]::CreateDirectory($directory)
    }
    $env:TEMP = $temporaryRoot
    $env:TMP = $temporaryRoot
    $env:PSModuleAnalysisCachePath = [IO.Path]::Combine($temporaryRoot, 'ModuleAnalysisCache')
    Set-Location -LiteralPath $temporaryRoot
    [Environment]::CurrentDirectory = $temporaryRoot
    $lockPath = [IO.Path]::Combine($playtimeRoot, 'observer.lock')
    Assert-PlainPath $lockPath
    try {
        $observerLock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
    }
    catch [IO.IOException] { exit 0 } # Another worker owns this data root.

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public sealed class PlaytimeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public PlaytimeProcessHandle() : base(true) { }
    protected override bool ReleaseHandle() { return PlaytimeNative.CloseHandle(handle); }
}
public static class PlaytimeNative
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern PlaytimeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(PlaytimeProcessHandle process, uint flags,
        StringBuilder imageName, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(PlaytimeProcessHandle process, out long created,
        out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(PlaytimeProcessHandle handle, uint milliseconds);
}
'@

    $idleSince = [DateTimeOffset]::UtcNow
    while ($true) {
        $scanned = 0
        foreach ($path in [IO.Directory]::EnumerateFiles($requestsRoot, '*.json', [IO.SearchOption]::TopDirectoryOnly)) {
            if ($active.Count -ge 64 -or $scanned -ge 128) { break }
            $scanned++
            $sessionId = [IO.Path]::GetFileNameWithoutExtension($path)
            if ($active.ContainsKey($sessionId)) {
                try {
                    $session = $active[$sessionId]
                    # A new request from a reopened UI needs an acknowledgement now, not at the next heartbeat.
                    if ([PlaytimeNative]::WaitForSingleObject($session.Handle, 0) -eq 258 -and
                        (Save-SessionObservation $session ([DateTimeOffset]::UtcNow) $false)) {
                        $session.LastFlush = [DateTimeOffset]::UtcNow
                        Retire-Request $path '.done'
                    }
                }
                catch { } # The normal active-session loop below retries or retains the last confirmed receipt.
                continue
            }
            $processHandle = $null
            try {
                $identity = Get-Identity (Read-Record $path) $sessionId $true
                $receiptPath = [IO.Path]::Combine($receiptsRoot, $identity.SessionId + '.json')
                if ([IO.File]::Exists($receiptPath)) {
                    $previous = Get-Identity (Read-Record $receiptPath) $identity.SessionId $false
                    if ($previous.IsComplete) { Retire-Request $path '.done'; continue }
                }
                # PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE; no write or terminate rights.
                $processHandle = [PlaytimeNative]::OpenProcess(0x00101000, $false, $identity.ProcessId)
                if ($processHandle.IsInvalid) { Retire-Request $path '.unavailable'; continue }
                $image = [Text.StringBuilder]::new(32768)
                $imageLength = $image.Capacity
                $created = [long]0; $exited = [long]0; $kernel = [long]0; $user = [long]0
                if (-not [PlaytimeNative]::QueryFullProcessImageName($processHandle, 0, $image, [ref]$imageLength) -or
                    -not [PlaytimeNative]::GetProcessTimes($processHandle, [ref]$created, [ref]$exited, [ref]$kernel, [ref]$user) -or
                    [DateTimeOffset]::FromFileTime($created).UtcDateTime.Ticks -ne $identity.StartedAt.UtcDateTime.Ticks -or
                    -not [string]::Equals([IO.Path]::GetFullPath($image.ToString()).TrimEnd([char[]]@('\', '/')),
                        $identity.ExecutablePath, [StringComparison]::OrdinalIgnoreCase)) {
                    Retire-Request $path '.unavailable'
                    continue
                }
                $active[$identity.SessionId] = [pscustomobject]@{
                    Identity = $identity; Handle = $processHandle; RequestPath = $path; LastFlush = [DateTimeOffset]::MinValue
                    SaveFailedAt = [DateTimeOffset]::MinValue
                }
                $processHandle = $null # Ownership moves to the active session.
                $idleSince = [DateTimeOffset]::UtcNow
            }
            catch { Retire-Request $path '.invalid' }
            finally { if ($null -ne $processHandle) { $processHandle.Dispose() } }
        }

        $now = [DateTimeOffset]::UtcNow
        foreach ($sessionId in @($active.Keys)) {
            $session = $active[$sessionId]
            try {
                $waitResult = [PlaytimeNative]::WaitForSingleObject($session.Handle, 0)
                if ($waitResult -eq 0) {
                    $created = [long]0; $exited = [long]0; $kernel = [long]0; $user = [long]0
                    if (-not [PlaytimeNative]::GetProcessTimes($session.Handle, [ref]$created, [ref]$exited, [ref]$kernel, [ref]$user)) {
                        throw 'Process exit time is unavailable.'
                    }
                    if (-not (Save-SessionObservation $session ([DateTimeOffset]::FromFileTime($exited)) $true)) { continue }
                    Retire-Request $session.RequestPath '.done'
                    $session.Handle.Dispose()
                    $active.Remove($sessionId)
                }
                elseif ($waitResult -eq 258) {
                    if (($now - $session.LastFlush).TotalSeconds -ge 15) {
                        if (-not (Save-SessionObservation $session $now $false)) { continue }
                        $session.LastFlush = $now
                        Retire-Request $session.RequestPath '.done'
                    }
                }
                else { throw 'Process observation is unavailable.' }
            }
            catch {
                # Preserve the last durable observation; never guess a missing end time.
                Retire-Request $session.RequestPath '.unavailable'
                $session.Handle.Dispose()
                $active.Remove($sessionId)
            }
        }
        if ($active.Count -gt 0) { $idleSince = $now }
        elseif (($now - $idleSince).TotalSeconds -ge 5) { break }
        Start-Sleep -Milliseconds 1000
    }
    exit 0
}
catch {
    # Do not persist command lines, process details, or arbitrary exception messages.
    exit 1
}
finally {
    foreach ($session in $active.Values) { $session.Handle.Dispose() }
    if ($null -ne $observerLock) { $observerLock.Dispose() }
}
