[CmdletBinding()]
param()

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $ProjectRoot '.sdk\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $ProjectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $ProjectRoot '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $ProjectRoot '.nuget\http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $ProjectRoot '.nuget\plugins-cache'
$env:TEMP = Join-Path $ProjectRoot '.tmp'
$env:TMP = $env:TEMP
$env:ASTERLAUNCHER_DATA_HOME = Join-Path $ProjectRoot '.appdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'

@(
    $env:DOTNET_ROOT,
    $env:DOTNET_CLI_HOME,
    $env:NUGET_PACKAGES,
    $env:NUGET_HTTP_CACHE_PATH,
    $env:NUGET_PLUGINS_CACHE_PATH,
    $env:TEMP,
    $env:ASTERLAUNCHER_DATA_HOME
) |
    ForEach-Object { New-Item -ItemType Directory -Force -Path $_ | Out-Null }

$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
