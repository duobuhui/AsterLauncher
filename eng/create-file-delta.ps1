[CmdletBinding()]
param([Parameter(Mandatory)][string]$BasePackage,[Parameter(Mandatory)][string]$TargetPackage,[Parameter(Mandatory)][string]$OutputZip)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'set-env.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
if(Test-Path -LiteralPath $OutputZip){throw 'Delta output exists.'}
$base=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($BasePackage));$target=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($TargetPackage))
try {
 function Read-Manifest($zip){$entry=$zip.GetEntry('release-files.json');if(-not $entry){throw 'Multi-file release required.'};$r=[IO.StreamReader]::new($entry.Open());try{return $r.ReadToEnd()|ConvertFrom-Json}finally{$r.Dispose()}}
 $old=Read-Manifest $base;$new=Read-Manifest $target
 $archive=[IO.Compression.ZipFile]::Open([IO.Path]::GetFullPath($OutputZip),[IO.Compression.ZipArchiveMode]::Create)
 $count=0
 try {
  $entry=$archive.CreateEntry('file-delta.json');$writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false))
  try{$writer.Write(([ordered]@{format=2;from=$old.version;target=$new}|ConvertTo-Json -Depth 8 -Compress))}finally{$writer.Dispose()}
  foreach($property in $new.files.PSObject.Properties){
   $key=$property.Name;$record=$property.Value;$prior=$old.files.PSObject.Properties[$key]
   if($prior -and $prior.Value.sha256 -eq $record.sha256 -and $prior.Value.length -eq $record.length){continue}
   $entry=$archive.CreateEntry($key,[IO.Compression.CompressionLevel]::Optimal);$input=$target.GetEntry($key).Open();$out=$entry.Open()
   try{$input.CopyTo($out)}finally{$input.Dispose();$out.Dispose()};$count++
  }
 }finally{$archive.Dispose()}
 Write-Output "Changed files: $count"
 Write-Output "Delta: $OutputZip"
}finally{$base.Dispose();$target.Dispose()}
