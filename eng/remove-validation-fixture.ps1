[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$target=[IO.Path]::GetFullPath($Path).TrimEnd('\')
$allowed=@((Join-Path $root '.artifacts\update-tests'),(Join-Path $root '.artifacts\migration-tests'))
if(-not ($allowed | Where-Object {$target.StartsWith($_+'\',[StringComparison]::OrdinalIgnoreCase)})){throw 'Not an isolated validation fixture.'}
if(-not (Test-Path -LiteralPath $target)){return}
$ancestor=$target
while($true){
 if((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Fixture path traverses a filesystem link; preserve it.'}
 if($ancestor.Equals($root,[StringComparison]::OrdinalIgnoreCase)){break}
 $ancestor=Split-Path -Parent $ancestor
}
$pending=[Collections.Generic.Stack[string]]::new();$pending.Push($target)
while($pending.Count -gt 0){
 foreach($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force){
  if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Fixture contains a filesystem link; preserve it.'}
  if($entry.PSIsContainer){$pending.Push($entry.FullName)}
 }
}
Get-Process AsterLauncher -ErrorAction SilentlyContinue | ForEach-Object {
 if($_.Path -and $_.Path.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture process is still running.'}
}
Remove-Item -LiteralPath $target -Recurse -Force
$parent=Split-Path -Parent $target
if(-not(Get-ChildItem -LiteralPath $parent -Force | Select-Object -First 1)){Remove-Item -LiteralPath $parent}