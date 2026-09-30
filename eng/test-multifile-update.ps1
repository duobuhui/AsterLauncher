[CmdletBinding()]
param([Parameter(Mandatory)][string]$Package)
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
Write-Output "Full and file delta updates passed; Data preserved. Delta $((Get-Item $delta).Length) bytes / full $((Get-Item $Package).Length) bytes. Evidence: $fixture"
