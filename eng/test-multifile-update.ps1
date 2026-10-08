[CmdletBinding()]
param([Parameter(Mandatory)][string]$Package,[string]$BasePackage,[string]$DeltaPackage,[string]$OlderPackage)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'set-env.ps1')
$workspace=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $workspace ('.artifacts\update-tests\'+[Guid]::NewGuid().ToString('N'))
$install=Join-Path $fixture 'installed';$stage=Join-Path $fixture 'full-stage'
New-Item -ItemType Directory -Force -Path $fixture,$stage | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($Package),$install)
$data=Join-Path $install 'Data';New-Item -ItemType Directory -Path $data | Out-Null
'kept settings and archives' | Set-Content -LiteralPath (Join-Path $data 'fixture-user-data.txt') -Encoding UTF8
$dataHash=(Get-FileHash -LiteralPath (Join-Path $data 'fixture-user-data.txt')).Hash
$manifest=Get-Content -LiteralPath (Join-Path $install 'release-files.json') -Raw | ConvertFrom-Json
$shimHash=$manifest.files.'AsterLauncher.exe'.sha256
$copied=Join-Path $stage 'package.zip';Copy-Item -LiteralPath $Package -Destination $copied
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $workspace 'src\AsterLauncher.App\Services\ApplyUpdate.ps1') -Package $copied -TargetExe (Join-Path $install 'AsterLauncher.exe') -Mode MultiFull -ProcessId 99999999 -ExpectedHash $shimHash -NoLaunch
if($LASTEXITCODE -ne 0){throw ('Full updater failed: '+(Get-Content (Join-Path $stage 'update.log') -Raw))}
if((Get-FileHash (Join-Path $data 'fixture-user-data.txt')).Hash -ne $dataHash){throw 'Full update changed user data.'}
if(@(Get-ChildItem -LiteralPath (Join-Path $install 'MigrationBackup') -Filter 'migration-*.json').Count -ne 1){throw 'Full update did not keep exactly one rollback.'}
$target=Join-Path $fixture 'target-fixture.zip';Copy-Item -LiteralPath $Package -Destination $target
$zip=[IO.Compression.ZipFile]::Open($target,[IO.Compression.ZipArchiveMode]::Update)
try{
 $bytes=[Text.Encoding]::UTF8.GetBytes('future changed file fixture')
 $entry=$zip.CreateEntry('App/fixture-maintenance.txt');$stream=$entry.Open();try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
 $sha=[Security.Cryptography.SHA256]::Create();try{$hash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
 $manifest.files | Add-Member -NotePropertyName 'App/fixture-maintenance.txt' -NotePropertyValue ([pscustomobject]@{length=$bytes.Length;sha256=$hash})
 $zip.GetEntry('release-files.json').Delete();$entry=$zip.CreateEntry('release-files.json');$writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false));try{$writer.Write(($manifest|ConvertTo-Json -Depth 8 -Compress))}finally{$writer.Dispose()}
}finally{$zip.Dispose()}
$deltaStage=Join-Path $fixture 'delta-stage';New-Item -ItemType Directory -Path $deltaStage | Out-Null
$delta=Join-Path $deltaStage 'package.zip'
& (Join-Path $PSScriptRoot 'create-file-delta.ps1') -BasePackage $Package -TargetPackage $target -OutputZip $delta
if((Get-Item $delta).Length -ge (Get-Item $Package).Length){throw 'File delta did not reduce transfer size.'}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $workspace 'src\AsterLauncher.App\Services\ApplyUpdate.ps1') -Package $delta -TargetExe (Join-Path $install 'AsterLauncher.exe') -Mode MultiPatch -ProcessId 99999999 -ExpectedHash $shimHash -NoLaunch
if($LASTEXITCODE -ne 0){throw ('File delta updater failed: '+(Get-Content (Join-Path $deltaStage 'update.log') -Raw))}
if((Get-Content (Join-Path $install 'App\fixture-maintenance.txt') -Raw) -ne 'future changed file fixture'){throw 'Delta result mismatch.'}
if((Get-FileHash (Join-Path $data 'fixture-user-data.txt')).Hash -ne $dataHash){throw 'Delta update changed user data.'}
if(@(Get-ChildItem -LiteralPath (Join-Path $install 'MigrationBackup') -Filter 'migration-*.json').Count -ne 1){throw 'Consecutive full/delta updates accumulated rollback backups.'}
Write-Output "Full and file delta updates passed; Data preserved. Delta $((Get-Item $delta).Length) bytes / full $((Get-Item $Package).Length) bytes. Isolated copies removed after success."

if ($BasePackage -or $DeltaPackage) {
    if (-not $BasePackage -or -not $DeltaPackage) { throw 'Both base and delta packages are required.' }
    $baseInstall = Join-Path $fixture 'base-installed'
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($BasePackage), $baseInstall)
    $userData = Join-Path $baseInstall 'Data'
    New-Item -ItemType Directory -Force -Path $userData | Out-Null
    $sentinel = Join-Path $userData 'user-settings-and-archives.txt'
    [IO.File]::WriteAllText($sentinel, 'preserve base installation data')
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
    $location = Join-Path $baseInstall 'asterlauncher.data-location.json'
    [IO.File]::WriteAllText($location, (@{directory=$userData} | ConvertTo-Json))
    $locationHash = (Get-FileHash -LiteralPath $location -Algorithm SHA256).Hash
    $actualStage = Join-Path $baseInstall (".aster-update-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $actualStage | Out-Null
    $actualDelta = Join-Path $actualStage 'package.zip'
    Copy-Item -LiteralPath $DeltaPackage -Destination $actualDelta
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $workspace 'src\AsterLauncher.App\Services\ApplyUpdate.ps1') -Package $actualDelta -TargetExe (Join-Path $baseInstall 'AsterLauncher.exe') -Mode MultiPatch -ProcessId 99999999 -ExpectedHash $shimHash -NoLaunch
    if ($LASTEXITCODE -ne 0) { throw ('Release delta failed: ' + (Get-Content (Join-Path $actualStage 'update.log') -Raw)) }
    if (Test-Path -LiteralPath $actualStage) { throw "Successful update left its GUID staging directory." }
    # 0.1.3 invokes its old migrator. Exercise the new entry's first-start recovery.
    $sync = Start-Process -FilePath (Join-Path $baseInstall 'AsterLauncher.exe') -ArgumentList @('--sync-release',('"'+$baseInstall+'"')) -WindowStyle Hidden -Wait -PassThru
    if ($sync.ExitCode -ne 0) { throw 'New entry failed to synchronize legacy migration manifest.' }
    $targetZip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Package))
    try {
        $reader = [IO.StreamReader]::new($targetZip.GetEntry('release-files.json').Open())
        try { $expected = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $targetZip.Dispose() }
    foreach ($property in $expected.files.PSObject.Properties) {
        $path = Join-Path $baseInstall $property.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne $property.Value.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $property.Value.sha256) {
            throw "Release delta target mismatch: $($property.Name)"
        }
    }
    $updated = Get-Content -LiteralPath (Join-Path $baseInstall 'release-files.json') -Raw | ConvertFrom-Json
    if ($updated.version -ne $expected.version -or
        (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $sentinelHash -or
        (Get-FileHash -LiteralPath $location -Algorithm SHA256).Hash -ne $locationHash) {
        throw 'Release delta version or preserved data differs.'
    }
    # First-start repair must reject damaged output rather than blessing it with a new version.
    $manifestPath = Join-Path $baseInstall 'release-files.json'
    $updated.version = 'stale-manifest'
    $updated | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $staleHash = (Get-FileHash -LiteralPath $manifestPath).Hash
    $probeFile = Join-Path $baseInstall 'App\AsterLauncher.deps.json'
    $original = [IO.File]::ReadAllBytes($probeFile)
    $damaged = [byte[]]$original.Clone()
    $damaged[0] = $damaged[0] -bxor 1
    [IO.File]::WriteAllBytes($probeFile, $damaged)
    try {
        $sync = Start-Process -FilePath (Join-Path $baseInstall 'AsterLauncher.exe') -ArgumentList @('--sync-release',('"'+$baseInstall+'"')) -WindowStyle Hidden -Wait -PassThru
        if ($sync.ExitCode -eq 0 -or (Get-FileHash -LiteralPath $manifestPath).Hash -ne $staleHash) { throw 'Damaged upgrade was accepted by manifest recovery.' }
    } finally { [IO.File]::WriteAllBytes($probeFile, $original) }
    $sync = Start-Process -FilePath (Join-Path $baseInstall 'AsterLauncher.exe') -ArgumentList @('--sync-release',('"'+$baseInstall+'"')) -WindowStyle Hidden -Wait -PassThru
    if ($sync.ExitCode -ne 0) { throw 'Verified output failed manifest recovery.' }
    $updated = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($updated.version -ne $expected.version) { throw 'Recovered manifest version differs.' }
    Write-Output "Actual release delta passed: $($updated.version), all target file hashes match; Data and location preserved."
}
$retentionInstall = $install
if ($BasePackage) { $retentionInstall = $baseInstall }
& (Join-Path $PSScriptRoot 'test-update-retention.ps1') -Package $Package -InstallRoot $retentionInstall -LegacyPackage $BasePackage

if ($OlderPackage) {
    # A nonmatching patch baseline upgrades directly with the latest complete package.
    $olderInstall = Join-Path $fixture 'older-installed'
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($OlderPackage), $olderInstall)
    $oldVersion = (Get-Content -LiteralPath (Join-Path $olderInstall 'release-files.json') -Raw | ConvertFrom-Json).version
    $olderData = Join-Path $olderInstall 'Data'
    New-Item -ItemType Directory -Force -Path $olderData | Out-Null
    $olderSentinel = Join-Path $olderData 'cross-version-data.txt'
    [IO.File]::WriteAllText($olderSentinel, 'preserve data across skipped launcher releases')
    $olderDataHash = (Get-FileHash -LiteralPath $olderSentinel).Hash
    $olderStage = Join-Path $olderInstall ('.aster-update-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $olderStage | Out-Null
    $olderFull = Join-Path $olderStage 'package.zip'
    Copy-Item -LiteralPath $Package -Destination $olderFull
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $workspace 'src\AsterLauncher.App\Services\ApplyUpdate.ps1') -Package $olderFull -TargetExe (Join-Path $olderInstall 'AsterLauncher.exe') -Mode MultiFull -ProcessId 99999999 -ExpectedHash $shimHash -NoLaunch
    if ($LASTEXITCODE -ne 0) { throw ('Cross-version full update failed: ' + (Get-Content (Join-Path $olderStage 'update.log') -Raw)) }
    if (Test-Path -LiteralPath $olderStage) { throw 'Cross-version full update left its GUID staging directory.' }
    $sync = Start-Process -FilePath (Join-Path $olderInstall 'AsterLauncher.exe') -ArgumentList @('--sync-release', ('"' + $olderInstall + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($sync.ExitCode -ne 0) { throw 'Cross-version new entry synchronization failed.' }
    $targetZip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Package))
    try {
        $reader = [IO.StreamReader]::new($targetZip.GetEntry('release-files.json').Open())
        try { $expected = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $targetZip.Dispose() }
    foreach ($property in $expected.files.PSObject.Properties) {
        $path = Join-Path $olderInstall $property.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne $property.Value.length -or
            (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $property.Value.sha256) {
            throw "Cross-version target mismatch: $($property.Name)"
        }
    }
    $updated = Get-Content -LiteralPath (Join-Path $olderInstall 'release-files.json') -Raw | ConvertFrom-Json
    if ($updated.version -ne $expected.version -or
        (Get-FileHash -LiteralPath $olderSentinel).Hash -ne $olderDataHash -or
        @(Get-ChildItem -LiteralPath (Join-Path $olderInstall 'MigrationBackup') -Filter 'migration-*.json').Count -ne 1) {
        throw 'Cross-version update did not preserve version, Data or single rollback.'
    }
    Write-Output "PASS: direct full update $oldVersion -> $($updated.version); all target hashes, Data and one rollback verified."
}
& (Join-Path $PSScriptRoot "remove-validation-fixture.ps1") -Path $fixture
