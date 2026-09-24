[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoRestore
)

. (Join-Path $PSScriptRoot 'set-env.ps1')
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$DotNet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'

Push-Location $ProjectRoot
try {
    if (-not $NoRestore) {
        & $DotNet restore tests\AsterLauncher.Core.Tests\AsterLauncher.Core.Tests.csproj
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    & $DotNet test tests\AsterLauncher.Core.Tests\AsterLauncher.Core.Tests.csproj --configuration $Configuration --no-restore
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
