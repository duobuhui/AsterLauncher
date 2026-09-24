[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'set-env.ps1')
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$DotNet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'

Push-Location $ProjectRoot
try {
    & $DotNet run --project src\AsterLauncher.App\AsterLauncher.App.csproj --configuration Debug --property:Platform=x64
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
