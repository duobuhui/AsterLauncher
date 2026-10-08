[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$InstallRoot,
    [string]$LegacyPackage
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'set-env.ps1')
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$install = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$allowed = Join-Path $workspace '.artifacts\update-tests'
if (-not $install.StartsWith($allowed + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Backup retention checks require an isolated update-tests installation.'
}
$fixture = Split-Path -Parent $install
$backup = Join-Path $install 'MigrationBackup'
$entry = Join-Path $install 'AsterLauncher.exe'
$journal = Join-Path $install '.aster-migration.json'
$pointer = Join-Path $install 'asterlauncher.data-location.json'
$packageHash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant()
$entryHash = (Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestHash = (Get-FileHash -LiteralPath (Join-Path $install 'release-files.json') -Algorithm SHA256).Hash.ToLowerInvariant()
if (Test-Path -LiteralPath $journal) { throw 'Fixture has an unexpected pending migration.' }
$originalPointer = if (Test-Path -LiteralPath $pointer) { [IO.File]::ReadAllBytes($pointer) } else { $null }
$originalDataHome = $env:ASTERLAUNCHER_DATA_HOME
function Get-Receipts {
    return @(Get-ChildItem -LiteralPath $backup -Filter 'migration-*.json' -File)
}
function Invoke-Sync {
    $process = Start-Process -FilePath $entry -ArgumentList @('--sync-release', ('"' + $install + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Backup cleanup entry failed.' }
}
function Assert-FixturePath([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture operation escaped its isolated directory.'
    }
}
function Add-HistoricalBackup {
    $id = '20000101000000-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $app = Join-Path $backup ('App-' + $id)
    $tools = Join-Path $backup ('MigrationTools-' + $id)
    $oldEntry = Join-Path $backup ('AsterLauncher-' + $id + '.exe')
    New-Item -ItemType Directory -Path $app, $tools | Out-Null
    $files = [ordered]@{}
    foreach ($item in @(
        @{name='AsterLauncher.exe';path=$oldEntry;content='historical shim'},
        @{name='App/AsterLauncher.exe';path=(Join-Path $app 'AsterLauncher.exe');content='historical app'},
        @{name='MigrationTools/AsterLauncher.Migrator.dll';path=(Join-Path $tools 'AsterLauncher.Migrator.dll');content='historical update helper'}
    )) {
        [IO.File]::WriteAllText($item.path, $item.content)
        $files[$item.name] = @{length=(Get-Item -LiteralPath $item.path).Length;sha256=(Get-FileHash -LiteralPath $item.path -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    $oldManifest = Join-Path $backup ('release-files-' + $id + '.json')
    [IO.File]::WriteAllText($oldManifest, (@{format=1;version='0.1.0-beta';files=$files} | ConvertTo-Json -Depth 8))
    $record = [ordered]@{
        Id=$id;OldExeBackup=$oldEntry;OldAppBackup=$app;OldToolsBackup=$tools
        Stage=(Join-Path $install ('.aster-migration-' + $id))
        ShimSha256=$entryHash;PackageSha256=$packageHash;HadApp=$true;HadTools=$true
        ManifestSha256=$manifestHash;HadManifest=$true
    }
    $receipt = Join-Path $backup ('migration-' + $id + '.json')
    [IO.File]::WriteAllText($receipt, ($record | ConvertTo-Json -Depth 8))
    [IO.File]::SetLastWriteTimeUtc($receipt, [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc))
    return [pscustomobject]@{Id=$id;App=$app;Tools=$tools;Exe=$oldEntry;Manifest=$oldManifest;Receipt=$receipt;Record=$record}
}
function Assert-BackupExists($group, [string]$reason) {
    foreach ($path in @($group.App, $group.Tools, $group.Exe, $group.Manifest, $group.Receipt)) {
        if (-not (Test-Path -LiteralPath $path)) { throw ($reason + ': ' + $path) }
    }
}
function Assert-BackupRemoved($group, [string]$reason) {
    foreach ($path in @($group.App, $group.Tools, $group.Exe, $group.Manifest, $group.Receipt)) {
        if (Test-Path -LiteralPath $path) { throw ($reason + ': ' + $path) }
    }
}
function Restore-Pointer {
    if ($null -ne $originalPointer) { [IO.File]::WriteAllBytes($pointer, $originalPointer) }
    elseif (Test-Path -LiteralPath $pointer) { Remove-Item -LiteralPath $pointer }
}
$initial = @(Get-Receipts)
if ($initial.Count -ne 1) { throw "Expected one completed rollback before retention tests, found $($initial.Count)." }
$latest = $initial[0].FullName
$latestHash = (Get-FileHash -LiteralPath $latest).Hash
$unknown = Join-Path $backup 'user-kept-file.txt'
[IO.File]::WriteAllText($unknown, 'user-owned backup notes')
$unknownHash = (Get-FileHash -LiteralPath $unknown).Hash
$data = Join-Path $install 'Data\retention-sentinel.txt'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $data) | Out-Null
[IO.File]::WriteAllText($data, 'preserve configuration and archives')
$dataHash = (Get-FileHash -LiteralPath $data).Hash

# A pending recovery journal suspends cleanup, even when several complete old groups exist.
$oldOne = Add-HistoricalBackup
$oldTwo = Add-HistoricalBackup
[IO.File]::WriteAllText($journal, '{"pending":"recovery fixture"}')
try {
    Invoke-Sync
    Assert-BackupExists $oldOne 'Pending journal lost its recovery backup'
    Assert-BackupExists $oldTwo 'Pending journal lost its recovery backup'
    if (@(Get-Receipts).Count -ne 3) { throw 'Pending journal did not suspend backup cleanup.' }
} finally { Remove-Item -LiteralPath $journal }
Invoke-Sync
Assert-BackupRemoved $oldOne 'Historical completed backup was not pruned'
Assert-BackupRemoved $oldTwo 'Historical completed backup was not pruned'

# An invalid location pointer disables cleanup rather than guessing where user data lives.
$invalidPointerGroup = Add-HistoricalBackup
try {
    [IO.File]::WriteAllText($pointer, '{"unexpected":"missing directory"}')
    Invoke-Sync
    Assert-BackupExists $invalidPointerGroup 'Invalid data pointer did not suspend cleanup'
} finally { Restore-Pointer }
Invoke-Sync
Assert-BackupRemoved $invalidPointerGroup 'Complete backup remained after repairing the data pointer'

# A moved Data directory may overlap a recognizable backup; it must take priority.
$dataGroup = Add-HistoricalBackup
$dataFile = Join-Path $dataGroup.App 'AsterLauncher.exe'
$dataFileHash = (Get-FileHash -LiteralPath $dataFile).Hash
try {
    [IO.File]::WriteAllText($pointer, (@{directory=$dataGroup.App} | ConvertTo-Json))
    Invoke-Sync
    Assert-BackupExists $dataGroup 'Configured data directory was removed'
    if ((Get-FileHash -LiteralPath $dataFile).Hash -ne $dataFileHash) { throw 'Configured data content changed.' }
} finally { Restore-Pointer }
Invoke-Sync
Assert-BackupRemoved $dataGroup 'Previously protected complete backup was not pruned after restoring Data'

# Protect the development override even when a location pointer also exists.
$environmentGroup = Add-HistoricalBackup
try {
    $env:ASTERLAUNCHER_DATA_HOME = $environmentGroup.App
    Invoke-Sync
    Assert-BackupExists $environmentGroup 'Environment data directory was removed'
} finally { $env:ASTERLAUNCHER_DATA_HOME = $originalDataHome }
Invoke-Sync
Assert-BackupRemoved $environmentGroup 'Environment backup remained after removing its data reference'

# A Data junction can alias a backup without sharing its textual path.
$aliasGroup = Add-HistoricalBackup
$dataAlias = Join-Path $fixture ('retention-data-alias-' + [Guid]::NewGuid().ToString('N'))
Assert-FixturePath $dataAlias
try {
    New-Item -ItemType Junction -Path $dataAlias -Target $aliasGroup.App | Out-Null
    [IO.File]::WriteAllText($pointer, (@{directory=$dataAlias} | ConvertTo-Json))
    Invoke-Sync
    Assert-BackupExists $aliasGroup 'Data junction alias was removed'
} finally {
    Restore-Pointer
    if (Test-Path -LiteralPath $dataAlias) {
        if (-not ((Get-Item -LiteralPath $dataAlias -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Expected owned Data junction before unlinking.' }
        [IO.Directory]::Delete($dataAlias)
    }
}
Invoke-Sync
Assert-BackupRemoved $aliasGroup 'Backup remained after removing the Data junction alias'

# Unknown content inside a recognized directory is never guessed to be disposable.
$unknownGroup = Add-HistoricalBackup
$unknownChild = Join-Path $unknownGroup.App 'user-file.txt'
[IO.File]::WriteAllText($unknownChild, 'not listed by the release')
Invoke-Sync
Assert-BackupExists $unknownGroup 'Unknown file inside a backup was removed'
Remove-Item -LiteralPath $unknownChild
Invoke-Sync
Assert-BackupRemoved $unknownGroup 'Complete backup remained after removing the unknown test file'

# Matching names and file lengths do not authorize traversal through a directory junction.
$linkGroup = Add-HistoricalBackup
$linkTarget = Join-Path $fixture ('retention-link-target-' + [Guid]::NewGuid().ToString('N'))
Assert-FixturePath $linkGroup.App
Assert-FixturePath $linkTarget
Move-Item -LiteralPath $linkGroup.App -Destination $linkTarget
try {
    New-Item -ItemType Junction -Path $linkGroup.App -Target $linkTarget | Out-Null
    Invoke-Sync
    Assert-BackupExists $linkGroup 'Linked backup directory was removed'
    if (-not (Test-Path -LiteralPath (Join-Path $linkTarget 'AsterLauncher.exe'))) { throw 'Cleanup followed a backup junction.' }
} finally {
    if (Test-Path -LiteralPath $linkGroup.App) {
        $linkItem = Get-Item -LiteralPath $linkGroup.App -Force
        if (-not ($linkItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Expected the owned junction before unlinking.' }
        [IO.Directory]::Delete($linkGroup.App)
    }
    Move-Item -LiteralPath $linkTarget -Destination $linkGroup.App
}
Invoke-Sync
Assert-BackupRemoved $linkGroup 'Unlinked completed backup was not pruned'

# A forged receipt must not permit deletions outside the fixed backup group.
$forged = Add-HistoricalBackup
$forged.Record.OldAppBackup = Join-Path $fixture 'unrelated-data'
[IO.File]::WriteAllText($forged.Receipt, ($forged.Record | ConvertTo-Json -Depth 8))
Invoke-Sync
Assert-BackupExists $forged 'Invalid receipt authorized cleanup'
$forged.Record.OldAppBackup = $forged.App
[IO.File]::WriteAllText($forged.Receipt, ($forged.Record | ConvertTo-Json -Depth 8))
[IO.File]::SetLastWriteTimeUtc($forged.Receipt, [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc))
Invoke-Sync
Assert-BackupRemoved $forged 'Repaired test receipt remained'

# Failed validation must leave the current rollback intact.
$failure = Start-Process -FilePath $entry -ArgumentList @('--migrate', ('"' + $install + '"'), '--package', ('"' + [IO.Path]::GetFullPath($Package) + '"'), '--sha256', ('0' * 64), '--no-launch') -WindowStyle Hidden -Wait -PassThru
if ($failure.ExitCode -eq 0 -or @(Get-Receipts).Count -ne 1 -or
    (Get-FileHash -LiteralPath $latest).Hash -ne $latestHash -or
    (Get-FileHash -LiteralPath $entry).Hash.ToLowerInvariant() -ne $entryHash -or
    (Get-FileHash -LiteralPath $unknown).Hash -ne $unknownHash -or
    (Get-FileHash -LiteralPath $data).Hash -ne $dataHash) {
    throw 'Failure or retention changed the latest rollback, installation, unknown file, or Data.'
}
if ($null -ne $originalPointer -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($pointer)) -ne [Convert]::ToBase64String($originalPointer)) {
    throw 'Retention changed the original data location pointer.'
}
Write-Output 'PASS: one rollback; historical groups pruned; Data, unknown files, links, invalid receipts, pending journal and failed updates protected.'

if ($LegacyPackage) {
    # Run the public previous helper itself, including its pre-retention behavior before 0.2.2.
    $legacyRoot = Join-Path $fixture 'legacy-helper-retention'
    $legacyToolRoot = Join-Path $legacyRoot 'helper'
    $legacyInstall = Join-Path $legacyRoot 'installed'
    $tinyPayload = Join-Path $legacyRoot 'tiny-payload'
    New-Item -ItemType Directory -Force -Path $legacyToolRoot, $legacyInstall, $tinyPayload | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($LegacyPackage))
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('release-files.json').Open())
        try { $legacyVersion = ($reader.ReadToEnd() | ConvertFrom-Json).version } finally { $reader.Dispose() }
        $legacyExpectedCount = if ([Version]($legacyVersion.Split('-')[0]) -lt [Version]'0.2.2') { 3 } else { 1 }
        foreach ($item in $zip.Entries) {
            if (-not $item.FullName.StartsWith('MigrationTools/', [StringComparison]::Ordinal) -or $item.FullName.EndsWith('/')) { continue }
            $relative = $item.FullName.Substring('MigrationTools/'.Length).Replace('/', '\')
            $destination = [IO.Path]::GetFullPath((Join-Path $legacyToolRoot $relative))
            if (-not $destination.StartsWith($legacyToolRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe legacy helper archive path.' }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($item, $destination)
        }
    } finally { $zip.Dispose() }
    $legacyTool = Join-Path $legacyToolRoot 'AsterLauncher.Migrator.exe'
    $tinyFiles = [ordered]@{}
    foreach ($name in @('AsterLauncher.exe', 'App/AsterLauncher.exe', 'MigrationTools/AsterLauncher.Migrator.dll')) {
        $path = Join-Path $tinyPayload $name
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        [IO.File]::WriteAllText($path, 'small legacy retention fixture: ' + $name)
        $tinyFiles[$name] = @{length=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}
    }
    [IO.File]::WriteAllText((Join-Path $tinyPayload 'release-files.json'), (@{format=1;version='0.1.0-beta';files=$tinyFiles} | ConvertTo-Json -Depth 8))
    $tinyPackage = Join-Path $legacyRoot 'tiny.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($tinyPayload, $tinyPackage)
    $tinyHash = (Get-FileHash -LiteralPath $tinyPackage).Hash
    Copy-Item -Path (Join-Path $tinyPayload '*') -Destination $legacyInstall -Recurse
    New-Item -ItemType Directory -Path (Join-Path $legacyInstall 'Data') | Out-Null
    $legacyData = Join-Path $legacyInstall 'Data\sentinel.txt'
    [IO.File]::WriteAllText($legacyData, 'legacy user data')
    $legacyDataHash = (Get-FileHash -LiteralPath $legacyData).Hash
    foreach ($migration in @(
        @{package=$tinyPackage;sha=$tinyHash},
        @{package=$tinyPackage;sha=$tinyHash},
        @{package=[IO.Path]::GetFullPath($Package);sha=$packageHash}
    )) {
        $process = Start-Process -FilePath $legacyTool -ArgumentList @('--migrate', ('"' + $legacyInstall + '"'), '--package', ('"' + $migration.package + '"'), '--sha256', $migration.sha, '--no-launch') -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw 'Public old helper failed its retained-backup upgrade fixture.' }
    }
    $legacyBackup = Join-Path $legacyInstall 'MigrationBackup'
    if (@(Get-ChildItem -LiteralPath $legacyBackup -Filter 'migration-*.json').Count -ne $legacyExpectedCount) { throw 'Public previous helper rollback count differs from its release behavior.' }
    $process = Start-Process -FilePath (Join-Path $legacyInstall 'AsterLauncher.exe') -ArgumentList @('--sync-release', ('"' + $legacyInstall + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or
        @(Get-ChildItem -LiteralPath $legacyBackup -Filter 'migration-*.json').Count -ne 1 -or
        (Get-FileHash -LiteralPath $legacyData).Hash -ne $legacyDataHash) {
        throw 'New bootstrap did not safely prune backups left by the public old helper.'
    }
    Write-Output "PASS: actual public $legacyVersion helper left $legacyExpectedCount rollback groups; new bootstrap kept one and preserved Data."
}