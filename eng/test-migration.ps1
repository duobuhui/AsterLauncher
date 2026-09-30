[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$MigratorExe)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'set-env.ps1')
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$tool = [IO.Path]::GetFullPath($MigratorExe)
if (-not $tool.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw '迁移器必须是工作区内的已构建 EXE。' }
$testRoot = Join-Path $root ('.artifacts\migration-tests\automated-' + [Guid]::NewGuid().ToString('N'))
$old = Join-Path $testRoot 'old'
$payload = Join-Path $testRoot 'payload'
New-Item -ItemType Directory -Path $old, $payload -Force | Out-Null
$oldExe = Join-Path $old 'AsterLauncher.exe'
[IO.File]::WriteAllText($oldExe, 'old-launcher-content')
New-Item -ItemType Directory -Path (Join-Path $old 'Data') | Out-Null
[IO.File]::WriteAllText((Join-Path $old 'Data\keep.txt'), 'keep-user-data')
[IO.File]::WriteAllText((Join-Path $old 'asterlauncher.data-location.json'),
    ('{"directory":"' + ((Join-Path $old 'Data').Replace('\','\\')) + '"}'))
$oldHash = (Get-FileHash -LiteralPath $oldExe -Algorithm SHA256).Hash
foreach ($name in @('App','MigrationTools')) { New-Item -ItemType Directory -Path (Join-Path $payload $name) | Out-Null }
[IO.File]::WriteAllText((Join-Path $payload 'AsterLauncher.exe'), 'new-root-shim')
[IO.File]::WriteAllText((Join-Path $payload 'App\AsterLauncher.exe'), 'new-app')
[IO.File]::WriteAllText((Join-Path $payload 'MigrationTools\AsterLauncher.Migrator.dll'), 'new-migrator')
$files = [ordered]@{}
Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($payload.Length + 1).Replace('\','/')
    $files[$relative] = [ordered]@{length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
[ordered]@{format=1;version='0.1.3-beta';files=$files} | ConvertTo-Json -Depth 6 -Compress |
    Set-Content -LiteralPath (Join-Path $payload 'release-files.json') -Encoding UTF8
$zip = Join-Path $testRoot 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip)
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
function Invoke-Migration([string[]]$arguments) {
    $process = Start-Process -FilePath $tool -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    return $process.ExitCode
}
if ((Invoke-Migration @('--migrate',$old,'--package',$zip,'--sha256',('0'*64),'--no-launch')) -eq 0) { throw '坏 ZIP 哈希未被拒绝。' }
if ((Get-FileHash -LiteralPath $oldExe -Algorithm SHA256).Hash -ne $oldHash) { throw '失败时修改了旧版 EXE。' }
$unsafe = Join-Path $testRoot 'unsafe.zip'
$archive = [IO.Compression.ZipFile]::Open($unsafe, [IO.Compression.ZipArchiveMode]::Create)
try { [void]$archive.CreateEntry('../escape.txt') } finally { $archive.Dispose() }
$unsafeSha = (Get-FileHash -LiteralPath $unsafe -Algorithm SHA256).Hash
if ((Invoke-Migration @('--migrate',$old,'--package',$unsafe,'--sha256',$unsafeSha,'--no-launch')) -eq 0 -or
    (Test-Path -LiteralPath (Join-Path $testRoot 'escape.txt'))) { throw '越界 ZIP 路径未被拒绝。' }
$corruptPayload = Join-Path $testRoot 'corrupt-payload'
Copy-Item -LiteralPath $payload -Destination $corruptPayload -Recurse
[IO.File]::WriteAllText((Join-Path $corruptPayload 'App\AsterLauncher.exe'), 'altered-app')
$corruptZip = Join-Path $testRoot 'corrupt.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($corruptPayload, $corruptZip)
$corruptSha = (Get-FileHash -LiteralPath $corruptZip -Algorithm SHA256).Hash
if ((Invoke-Migration @('--migrate',$old,'--package',$corruptZip,'--sha256',$corruptSha,'--no-launch')) -eq 0 -or
    (Get-FileHash -LiteralPath $oldExe -Algorithm SHA256).Hash -ne $oldHash) {
    throw '内部文件哈希不符时未保护旧版。'
}if ((Invoke-Migration @('--migrate',$old,'--package',$zip,'--sha256',$sha,'--no-launch')) -ne 0) {
    throw (Get-Content -LiteralPath (Join-Path $old 'migration-error.log') -Raw)
}
if ((Get-Content -LiteralPath $oldExe -Raw) -ne 'new-root-shim' -or
    (Get-Content -LiteralPath (Join-Path $old 'Data\keep.txt') -Raw) -ne 'keep-user-data' -or
    (Get-ChildItem -LiteralPath (Join-Path $old 'MigrationBackup') -Filter 'AsterLauncher-*.exe').Count -ne 1) {
    throw '迁移结果、备份或数据不符合预期。'
}
$receipt = Get-ChildItem -LiteralPath (Join-Path $old 'MigrationBackup') -Filter 'migration-*.json' | Select-Object -First 1
Copy-Item -LiteralPath $receipt.FullName -Destination (Join-Path $old '.aster-migration.json')
if ((Invoke-Migration @('--recover',$old)) -ne 0) {
    throw (Get-Content -LiteralPath (Join-Path $old 'migration-error.log') -Raw)
}
if ((Get-FileHash -LiteralPath $oldExe -Algorithm SHA256).Hash -ne $oldHash -or
    (Test-Path -LiteralPath (Join-Path $old 'App')) -or
    (Test-Path -LiteralPath (Join-Path $old 'MigrationTools')) -or
    (Get-Content -LiteralPath (Join-Path $old 'Data\keep.txt') -Raw) -ne 'keep-user-data') {
    throw '恢复后旧版 EXE、App、工具或数据不符合预期。'
}
Write-Output "PASS: bad hash, traversal, inner hash, migration, backup, data preservation, recovery ($testRoot)"
