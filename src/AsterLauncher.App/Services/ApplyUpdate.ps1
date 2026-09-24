param(
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$TargetExe,
    [Parameter(Mandatory=$true)][ValidateSet('Full','Patch')][string]$Mode,
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$ExpectedHash
)

$ErrorActionPreference = 'Stop'
$stage = Split-Path -Parent $Package
$candidate = Join-Path $stage 'AsterLauncher.new.exe'
$backup = "$TargetExe.old"
$log = Join-Path $stage 'update.log'

try {
    for ($attempt = 0; $attempt -lt 180; $attempt++) {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        throw 'Launcher did not exit within 90 seconds.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unpacked = Join-Path $stage 'unpacked'
    New-Item -ItemType Directory -Force -Path $unpacked | Out-Null
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Package)
    try {
        foreach ($entry in $archive.Entries) {
            if ($Mode -eq 'Full') {
                if ($entry.FullName -ne 'AsterLauncher.exe') { throw 'Unexpected full-package entry.' }
            } elseif ($entry.FullName -ne 'patch.json' -and
                $entry.FullName -notmatch '^block-[0-9]{6}\.bin$') {
                throw 'Unexpected patch-package entry.'
            }
            if ($entry.Length -gt 1073741824) { throw 'Update entry is too large.' }
        }
    } finally {
        $archive.Dispose()
    }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Package, $unpacked)

    if ($Mode -eq 'Full') {
        $source = Join-Path $unpacked 'AsterLauncher.exe'
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Full package has no launcher.' }
        [System.IO.File]::Copy($source, $candidate, $true)
    } else {
        $plan = Get-Content -LiteralPath (Join-Path $unpacked 'patch.json') -Raw | ConvertFrom-Json
        if ((Get-FileHash -LiteralPath $TargetExe -Algorithm SHA256).Hash -ne $plan.baseSha256) {
            throw 'Installed version does not match patch base.'
        }
        if ($plan.targetSha256 -ne $ExpectedHash -or $plan.blockSize -ne 1048576 -or
            $plan.targetLength -le 0 -or $plan.targetLength -gt 1073741824) {
            throw 'Patch manifest is invalid.'
        }
        $changed = @{}
        foreach ($index in $plan.changedBlocks) {
            if ($index -lt 0 -or $index -ge [math]::Ceiling($plan.targetLength / $plan.blockSize) -or
                $changed.ContainsKey([int]$index)) { throw 'Patch block index is invalid.' }
            $changed[[int]$index] = $true
        }
        $old = [System.IO.File]::OpenRead($TargetExe)
        $new = [System.IO.File]::Create($candidate)
        try {
            $count = [int][math]::Ceiling($plan.targetLength / $plan.blockSize)
            for ($index = 0; $index -lt $count; $index++) {
                $length = [int][math]::Min($plan.blockSize, $plan.targetLength - ($index * $plan.blockSize))
                if ($changed.ContainsKey($index)) {
                    $block = Join-Path $unpacked ('block-{0:D6}.bin' -f $index)
                    if ((Get-Item -LiteralPath $block).Length -ne $length) { throw 'Patch block length is invalid.' }
                    $bytes = [System.IO.File]::ReadAllBytes($block)
                } else {
                    $old.Position = [long]$index * $plan.blockSize
                    $bytes = New-Object byte[] $length
                    if ($old.Read($bytes, 0, $length) -ne $length) { throw 'Patch base is too short.' }
                }
                $new.Write($bytes, 0, $length)
            }
        } finally {
            $old.Dispose()
            $new.Dispose()
        }
    }

    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $ExpectedHash) {
        throw 'Target executable hash does not match.'
    }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    [System.IO.File]::Move($TargetExe, $backup)
    try {
        [System.IO.File]::Move($candidate, $TargetExe)
    } catch {
        [System.IO.File]::Move($backup, $TargetExe)
        throw
    }
    Start-Process -FilePath $TargetExe -WorkingDirectory (Split-Path -Parent $TargetExe)
    Remove-Item -LiteralPath $backup -Force
    'Update completed.' | Set-Content -LiteralPath $log -Encoding UTF8
} catch {
    $_.Exception.Message | Set-Content -LiteralPath $log -Encoding UTF8
    exit 1
}
