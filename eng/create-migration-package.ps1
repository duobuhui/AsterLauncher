[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AppPublish,
    [Parameter(Mandatory=$true)][string]$MigratorPublish,
    [Parameter(Mandatory=$true)][string]$OutputZip,
    [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$appSource = [IO.Path]::GetFullPath($AppPublish)
$toolSource = [IO.Path]::GetFullPath($MigratorPublish)
$zipPath = [IO.Path]::GetFullPath($OutputZip)
foreach ($path in @($appSource, $toolSource, $zipPath)) {
    if (-not $path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "此项目的打包输入与输出必须位于 E 盘工作区：$path"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $appSource 'AsterLauncher.exe') -PathType Leaf)) { throw '应用发布目录缺少 AsterLauncher.exe。' }
if (-not (Test-Path -LiteralPath (Join-Path $toolSource 'AsterLauncher.Migrator.exe') -PathType Leaf)) { throw '迁移器发布目录缺少 AsterLauncher.Migrator.exe。' }
$actualVersion = (Get-Item -LiteralPath (Join-Path $appSource 'AsterLauncher.exe')).VersionInfo.ProductVersion.Split('+')[0]
if ($Version -ne $actualVersion) { throw "版本不匹配：应用为 $actualVersion，打包参数为 $Version。" }
if (Test-Path -LiteralPath $zipPath) { throw "输出包已存在，不覆盖：$zipPath" }
$nonce = [Guid]::NewGuid().ToString('N')
$payload = Join-Path $root ".artifacts\migration-payload-$nonce"
New-Item -ItemType Directory -Path $payload | Out-Null
try {
    Copy-Item -LiteralPath $appSource -Destination (Join-Path $payload 'App') -Recurse
    Copy-Item -LiteralPath $toolSource -Destination (Join-Path $payload 'MigrationTools') -Recurse
    $sourceHost = Join-Path $toolSource 'AsterLauncher.Migrator.exe'
    $bytes = [IO.File]::ReadAllBytes($sourceHost)
    $needle = [Text.Encoding]::UTF8.GetBytes('AsterLauncher.Migrator.dll')
    $replacement = [Text.Encoding]::UTF8.GetBytes('MigrationTools\AsterLauncher.Migrator.dll')
    $offset = -1
    for ($i=0; $i -le $bytes.Length-$needle.Length; $i++) {
        if ($bytes[$i] -ne $needle[0]) { continue }
        $match = $true
        for ($j=1; $j -lt $needle.Length; $j++) { if ($bytes[$i+$j] -ne $needle[$j]) { $match=$false; break } }
        if ($match) { if ($offset -ge 0) { throw 'apphost DLL 标记不唯一。' }; $offset=$i }
    }
    if ($offset -lt 0) { throw '找不到 apphost DLL 标记。' }
    for ($i=$needle.Length; $i -le $replacement.Length; $i++) {
        if ($bytes[$offset+$i] -ne 0) { throw 'apphost 标记空间不足。' }
    }
    [Array]::Copy($replacement, 0, $bytes, $offset, $replacement.Length)
    [IO.File]::WriteAllBytes((Join-Path $payload 'AsterLauncher.exe'), $bytes)
    $links = @(Get-ChildItem -LiteralPath $payload -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($links.Count -gt 0) { throw "发布内容包含文件系统链接：$($links[0].FullName)" }
    $files = [ordered]@{}
    Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($payload.Length+1).Replace('\','/')
        $files[$relative] = [ordered]@{ length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    # The companion is carried forward even by 0.1.3's migrator.
    $companion = Join-Path $payload 'App\install-manifest.json'
    [ordered]@{ format=1; version=$Version; files=$files } | ConvertTo-Json -Depth 6 -Compress |
        Set-Content -LiteralPath $companion -Encoding UTF8
    $files['App/install-manifest.json'] = [ordered]@{length=(Get-Item -LiteralPath $companion).Length;sha256=(Get-FileHash -LiteralPath $companion -Algorithm SHA256).Hash.ToLowerInvariant()}
    [ordered]@{ format=1; version=$Version; files=$files } | ConvertTo-Json -Depth 6 -Compress |
        Set-Content -LiteralPath (Join-Path $payload 'release-files.json') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $parent = Split-Path -Parent $zipPath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($zipPath + '.sha256') -Value $hash -Encoding ASCII
    Write-Output "ZIP: $zipPath"
    Write-Output "SHA-256: $hash"
} finally {
    $resolved = [IO.Path]::GetFullPath($payload)
    $safe = [IO.Path]::GetFullPath((Join-Path $root '.artifacts')).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($safe, [StringComparison]::OrdinalIgnoreCase)) { throw '暂存路径越界，未清理。' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
