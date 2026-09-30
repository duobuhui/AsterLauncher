[CmdletBinding()]
param([Parameter(Mandatory)][string]$NodeSource)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'set-env.ps1')
$root=Split-Path -Parent $PSScriptRoot
$node=Join-Path $root '.artifacts\vendor\node-v24.19.0\node.exe'
$expected='3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237'
if ((Get-FileHash -LiteralPath $NodeSource -Algorithm SHA256).Hash -ne $expected) { throw 'Node source is not official v24.19.0 win-x64/node.exe.' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $node) | Out-Null
if ([IO.Path]::GetFullPath($NodeSource) -ne $node) { Copy-Item -LiteralPath $NodeSource -Destination $node }
$source=Join-Path $root 'third_party\7zip-26.03\win-x64\7za.exe'
$seven=Join-Path $root '.artifacts\vendor\7zip-26.03\extra\x64\7za.exe'
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne 'edbee35370e14030e4c785cf88200f42dc651c1eb4217c1e3963c38a12f099b0') { throw '7za binary checksum mismatch.' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $seven) | Out-Null
Copy-Item -LiteralPath $source -Destination $seven -Force
Write-Output 'Verified portable runtimes are ready inside E-drive workspace. No system installation was performed.'
