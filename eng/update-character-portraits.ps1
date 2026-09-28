$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$assetRoot = Join-Path $projectRoot 'src\AsterLauncher.App\Assets\Games\Gacha\Portraits'
$starRailRoot = Join-Path $assetRoot 'HonkaiStarRail'
$endfieldRoot = Join-Path $assetRoot 'Endfield'
New-Item -ItemType Directory -Path $starRailRoot, $endfieldRoot -Force | Out-Null
Add-Type -AssemblyName System.Drawing

function Save-OfficialPortrait {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string[]]$AllowedHosts
    )
    $uri = [Uri]$Url
    if ($uri.Scheme -ne 'https' -or $uri.Host -notin $AllowedHosts -or $uri.UserInfo) {
        throw "Portrait host is not an approved publisher CDN: $($uri.Host)"
    }
    if (-not (Test-Path -LiteralPath $Destination)) {
        $temporary = "$Destination.download"
        try {
            Invoke-WebRequest -Uri $uri -TimeoutSec 30 -OutFile $temporary
            $image = [System.Drawing.Image]::FromFile($temporary)
            try {
                if ($image.Width -lt 64 -or $image.Height -lt 64) {
                    throw "Portrait is too small: $($image.Width)x$($image.Height)"
                }
            }
            finally {
                $image.Dispose()
            }
            Move-Item -LiteralPath $temporary -Destination $Destination -Force
        }
        finally {
            if (Test-Path -LiteralPath $temporary) {
                Remove-Item -LiteralPath $temporary -Force
            }
        }
    }
}

$entries = [System.Collections.Generic.List[object]]::new()
$starRailPage = 'https://sr.mihoyo.com/character'
$starRailApi = 'https://act-api-takumi-static.mihoyo.com/content_v2_user/app/1963de8dc19e461c/getContentList?iChanId=253&iPageSize=999&iPage=1&sLangKey=zh-cn'
$starRail = Invoke-RestMethod -Uri $starRailApi -TimeoutSec 30
if ($starRail.retcode -ne 0 -or @($starRail.data.list).Count -lt 50) {
    throw 'The official Star Rail character listing did not return the expected records.'
}
foreach ($item in $starRail.data.list) {
    $details = $item.sExt | ConvertFrom-Json
    $url = $details.avatarPC[0].url
    if (-not $url) { continue }
    $relative = "HonkaiStarRail/$($item.iInfoId).png"
    $destination = Join-Path $assetRoot ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    Save-OfficialPortrait -Url $url -Destination $destination -AllowedHosts @(
        'fastcdn.mihoyo.com', 'webstatic.mihoyo.com'
    )
    $entries.Add([pscustomobject]@{
        GameId = 'honkai-star-rail'
        Name = [string]$item.sTitle
        File = $relative
        SourceUrl = $url
        SourcePage = $starRailPage
    })
}

$endfieldPage = 'https://endfield.hypergryph.com/operator'
$html = (Invoke-WebRequest -Uri $endfieldPage -TimeoutSec 30).Content
$pattern = 'data-key="([^"]+)" style="background-image:url\((https://[^)]+\.png)\)"[^>]*></div><div class="OperatorItem_contentBlock[^>]*data-rarity="(\d)"[^>]*><div class="OperatorItem_name[^>]*><span[^>]*>([^<]+)</span>'
$operators = [regex]::Matches($html, $pattern)
if ($operators.Count -lt 25) {
    throw 'The official Endfield operator listing did not return the expected records.'
}
foreach ($operator in $operators) {
    $key = $operator.Groups[1].Value
    if ($key -notmatch '^[a-z0-9]+$') { continue }
    $url = $operator.Groups[2].Value
    $name = [System.Net.WebUtility]::HtmlDecode($operator.Groups[4].Value)
    $relative = "Endfield/$key.png"
    $destination = Join-Path $assetRoot ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    Save-OfficialPortrait -Url $url -Destination $destination -AllowedHosts @('web.hycdn.cn')
    $entries.Add([pscustomobject]@{
        GameId = 'endfield'
        Name = $name
        File = $relative
        SourceUrl = $url
        SourcePage = $endfieldPage
    })
}

$manifestPath = Join-Path $assetRoot 'portrait-index.json'
$json = ConvertTo-Json -InputObject @($entries) -Depth 5
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))
Write-Output "Saved $($entries.Count) official portraits to $assetRoot."