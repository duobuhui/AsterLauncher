[CmdletBinding()]
param(
    [string]$PreviousPackage,
    [string]$PreviousVersion
)

. (Join-Path $PSScriptRoot 'set-env.ps1')
$root = Split-Path -Parent $PSScriptRoot
$version = (Select-Xml -Path (Join-Path $root 'Directory.Build.props') -XPath '//Version').Node.InnerText
$output = Join-Path $root ".artifacts\release\v$version"
$publish = Join-Path $output 'publish'
$payload = Join-Path $output 'payload'
$fullName = "AsterLauncher-v$version-win-x64.zip"
$fullPath = Join-Path $output $fullName
New-Item -ItemType Directory -Force -Path $output, $publish, $payload | Out-Null
Get-ChildItem -LiteralPath $output -Filter '*-delta.zip' -File -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

Push-Location $root
try {
    & dotnet publish .\src\AsterLauncher.App\AsterLauncher.App.csproj --configuration Release `
        --runtime win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableMsixTooling=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
} finally {
    Pop-Location
}

$target = Join-Path $payload 'AsterLauncher.exe'
Copy-Item -LiteralPath (Join-Path $publish 'AsterLauncher.exe') -Destination $target -Force
$targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $fullPath,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)
$manifest = [ordered]@{
    version = $version
    full = [ordered]@{
        asset = $fullName
        sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        targetSha256 = $targetHash
    }
}

if ($PreviousPackage) {
    if (-not $PreviousVersion -or $PreviousVersion -notmatch '^\d+\.\d+\.\d+-(?:beta(?:\.\d+)?)$') {
        throw 'PreviousVersion must be a beta semantic version.'
    }
    $previousRoot = Join-Path $output 'previous'
    New-Item -ItemType Directory -Force -Path $previousRoot | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $PreviousPackage).Path,
        $previousRoot)
    $previousExe = Join-Path $previousRoot 'AsterLauncher.exe'
    if (-not (Test-Path -LiteralPath $previousExe -PathType Leaf)) { throw 'Previous package has no AsterLauncher.exe.' }

    $deltaRoot = Join-Path $output 'delta'
    New-Item -ItemType Directory -Force -Path $deltaRoot | Out-Null
    $blockSize = 1048576
    $changed = [System.Collections.Generic.List[int]]::new()
    $old = [System.IO.File]::OpenRead($previousExe)
    $new = [System.IO.File]::OpenRead($target)
    try {
        $count = [int][math]::Ceiling($new.Length / $blockSize)
        for ($index = 0; $index -lt $count; $index++) {
            $length = [int][math]::Min($blockSize, $new.Length - ($index * $blockSize))
            $newBytes = New-Object byte[] $length
            if ($new.Read($newBytes, 0, $length) -ne $length) { throw 'Could not read new executable.' }
            $isSame = $old.Position + $length -le $old.Length
            if ($isSame) {
                $oldBytes = New-Object byte[] $length
                $isSame = $old.Read($oldBytes, 0, $length) -eq $length
                if ($isSame) {
                    $newHash = [System.Security.Cryptography.SHA256]::Create()
                    try {
                        $isSame = [Convert]::ToBase64String($newHash.ComputeHash($newBytes)) -eq
                            [Convert]::ToBase64String($newHash.ComputeHash($oldBytes))
                    } finally { $newHash.Dispose() }
                }
            } else {
                $old.Position = [math]::Min($old.Length, ([long]$index + 1) * $blockSize)
            }
            if (-not $isSame) {
                $changed.Add($index)
                [System.IO.File]::WriteAllBytes((Join-Path $deltaRoot ('block-{0:D6}.bin' -f $index)), $newBytes)
            }
        }
    } finally {
        $old.Dispose()
        $new.Dispose()
    }
    [ordered]@{
        baseSha256 = (Get-FileHash -LiteralPath $previousExe -Algorithm SHA256).Hash.ToLowerInvariant()
        targetSha256 = $targetHash
        targetLength = (Get-Item -LiteralPath $target).Length
        blockSize = $blockSize
        changedBlocks = @($changed.ToArray())
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $deltaRoot 'patch.json') -Encoding UTF8

    $patchName = "AsterLauncher-$PreviousVersion-to-$version-delta.zip"
    $patchPath = Join-Path $output $patchName
    if (Test-Path -LiteralPath $patchPath) { Remove-Item -LiteralPath $patchPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($deltaRoot, $patchPath,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    if ((Get-Item -LiteralPath $patchPath).Length -lt (Get-Item -LiteralPath $fullPath).Length) {
        $manifest.patch = [ordered]@{
            from = $PreviousVersion
            asset = $patchName
            sha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
            targetSha256 = $targetHash
        }
    } else {
        Remove-Item -LiteralPath $patchPath -Force
    }
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'aster-update.json') -Encoding UTF8
Write-Output "Release assets: $output"
Write-Output "Package root: AsterLauncher.exe"
