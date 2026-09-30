[CmdletBinding()]
param([Parameter(Mandatory)][string]$FullPackage,[Parameter(Mandatory)][string]$OutputZip)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'set-env.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
if(Test-Path -LiteralPath $OutputZip){throw 'Migration tools ZIP exists.'}
$source=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($FullPackage));$out=[IO.Compression.ZipFile]::Open([IO.Path]::GetFullPath($OutputZip),[IO.Compression.ZipArchiveMode]::Create)
try{
 foreach($entry in $source.Entries){
  if($entry.FullName -eq 'AsterLauncher.exe'){$name='AsterLauncher.Migrator.exe'}
  elseif($entry.FullName.StartsWith('MigrationTools/')){$name=$entry.FullName}
  else{continue}
  $target=$out.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal);$a=$entry.Open();$b=$target.Open();try{$a.CopyTo($b)}finally{$a.Dispose();$b.Dispose()}
 }
}finally{$source.Dispose();$out.Dispose()}
(Get-FileHash -LiteralPath $OutputZip -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath ($OutputZip+'.sha256') -Encoding ASCII
Write-Output "Migration tools: $OutputZip"
