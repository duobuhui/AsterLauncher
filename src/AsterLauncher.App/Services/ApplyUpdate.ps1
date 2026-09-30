param(
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$TargetExe,
    [Parameter(Mandatory=$true)][ValidateSet('MultiFull','MultiPatch')][string]$Mode,
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$ExpectedHash,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetFullPath((Split-Path -Parent $Package))
$root = [IO.Path]::GetFullPath((Split-Path -Parent $TargetExe))
$log = Join-Path $stage 'update.log'
function Resolve-Payload([string]$base, [string]$relative) {
    if ($relative -notmatch '^(App/|MigrationTools/|AsterLauncher\.exe$|release-files\.json$)' -or
        [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw 'Invalid release path.' }
    foreach ($part in $relative.Replace('\','/').Split('/')) {
        if (-not $part -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe release path.' }
    }
    $full = [IO.Path]::GetFullPath((Join-Path $base $relative.Replace('/','\')))
    if (-not $full.StartsWith($base.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release path escaped root.' }
    $cursor = $full
    while ($cursor.Length -gt 3) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Release path crosses a filesystem link.'
        }
        $cursor = Split-Path -Parent $cursor
    }
    return $full
}
function Hash-Matches([string]$path, $record) {
    return (Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Item -LiteralPath $path).Length -eq $record.length -and
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $record.sha256
}
try {
    if ([IO.Path]::GetPathRoot($root) -ne [IO.Path]::GetPathRoot($stage)) { throw 'Update staging must be on the install volume.' }
    for ($attempt=0; $attempt -lt 180; $attempt++) {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { throw 'Launcher did not exit within 90 seconds.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $prepared = $Package
    if ($Mode -eq 'MultiPatch') {
        $payload = Join-Path $stage 'reconstructed'
        if (Test-Path -LiteralPath $payload) { throw 'Reconstruction stage already exists.' }
        New-Item -ItemType Directory -Path $payload | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($Package)
        try {
            $plans = @($zip.Entries | Where-Object FullName -eq 'file-delta.json')
            if ($plans.Count -ne 1 -or $plans[0].Length -gt 16777216) { throw 'File delta manifest missing or too large.' }
            $reader = [IO.StreamReader]::new($plans[0].Open())
            try { $plan = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($plan.format -ne 2 -or $plan.target.format -ne 1) { throw 'Unsupported file delta.' }
            $currentVersion = (Get-Item -LiteralPath (Join-Path $root 'App\AsterLauncher.exe')).VersionInfo.ProductVersion.Split('+')[0]
            if ($currentVersion -ne $plan.from) { throw 'Installed version does not match delta base; use the full package.' }
            $entries = @{}
            foreach ($entry in $zip.Entries) {
                if ($entry.FullName -eq 'file-delta.json') { continue }
                [void](Resolve-Payload $payload $entry.FullName)
                if ($entries.ContainsKey($entry.FullName)) { throw 'Duplicate delta entry.' }
                $entries[$entry.FullName] = $entry
            }
            $total = 0L
            $records = @($plan.target.files.PSObject.Properties)
            if ($records.Count -eq 0 -or $records.Count -gt 10000) { throw 'Invalid target file manifest.' }
            foreach ($property in $records) {
                $name=$property.Name; $record=$property.Value
                if ($record.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $record.length -lt 0 -or $record.length -gt 1073741824) {
                    throw 'Invalid target content identity.'
                }
                $total += $record.length
            }
            if ($total -gt 4GB -or ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($root))).AvailableFreeSpace -lt ($total * 2 + 64MB)) { throw 'Insufficient update staging space.' }
            foreach ($property in $records) {
                $name=$property.Name; $record=$property.Value
                $target=Resolve-Payload $payload $name
                [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
                if ($entries.ContainsKey($name)) {
                    $entry=$entries[$name]
                    if ($entry.Length -ne $record.length) { throw 'Delta entry length mismatch.' }
                    $input=$entry.Open(); $output=[IO.File]::Create($target)
                    try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
                    $entries.Remove($name)
                } else {
                    $source=Resolve-Payload $root $name
                    if (-not (Hash-Matches $source $record)) { throw 'Unchanged file differs from target; use the full package.' }
                    [IO.File]::Copy($source,$target)
                }
                if (-not (Hash-Matches $target $record)) { throw 'Reconstructed release hash mismatch.' }
            }
            if ($entries.Count -ne 0) { throw 'Delta contains files outside target manifest.' }
            $plan.target | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath (Join-Path $payload 'release-files.json') -Encoding UTF8
        } finally { $zip.Dispose() }
        $prepared=Join-Path $stage 'complete.zip'
        $rebuilt=[IO.Compression.ZipFile]::Open($prepared,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
                $name=$file.FullName.Substring($payload.Length+1).Replace('\','/')
                [void](Resolve-Payload $payload $name)
                $entry=$rebuilt.CreateEntry($name,[IO.Compression.CompressionLevel]::Fastest)
                $input=[IO.File]::OpenRead($file.FullName);$output=$entry.Open()
                try {$input.CopyTo($output)} finally {$input.Dispose();$output.Dispose()}
            }
        } finally {$rebuilt.Dispose()}
    }
    $archive=[IO.Compression.ZipFile]::OpenRead($prepared)
    try {
        $entry=$archive.GetEntry('AsterLauncher.exe')
        if ($null -eq $entry) { throw 'Release has no entry point.' }
        $stream=$entry.Open(); $sha=[Security.Cryptography.SHA256]::Create()
        try { $actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($actual -ne $ExpectedHash) { throw 'Target entry hash mismatch.' }
    } finally { $archive.Dispose() }
    # Copy the current independent maintenance runtime out of directories about to be replaced.
    $toolSource=Resolve-Payload $root 'MigrationTools/AsterLauncher.Migrator.exe'
    $toolStage=Join-Path $stage 'ApplyTools'
    Copy-Item -LiteralPath (Split-Path -Parent $toolSource) -Destination $toolStage -Recurse
    $zipHash=(Get-FileHash -LiteralPath $prepared -Algorithm SHA256).Hash
    $apply=Join-Path $toolStage 'AsterLauncher.Migrator.exe'
    $arguments=@('--migrate', ('"'+$root+'"'), '--package', ('"'+$prepared+'"'), '--sha256', $zipHash, '--no-launch')
    $child=Start-Process -FilePath $apply -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru -WorkingDirectory $toolStage
    if ($child.ExitCode -ne 0) { throw 'Migration commit failed; original install or recovery journal preserved.' }
    if (-not $NoLaunch) { Start-Process -FilePath $TargetExe -WorkingDirectory $root -WindowStyle Hidden }
    'Multi-file update completed. Previous application is in MigrationBackup; Data is unchanged.' | Set-Content -LiteralPath $log -Encoding UTF8
} catch {
    $_.Exception.Message | Set-Content -LiteralPath $log -Encoding UTF8
    exit 1
}
