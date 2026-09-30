[CmdletBinding()]
param([string]$PreviousPackage,[string]$PreviousVersion)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'set-env.ps1')
$root=Split-Path -Parent $PSScriptRoot
$version=(Select-Xml -Path (Join-Path $root 'Directory.Build.props') -XPath '//Version').Node.InnerText
$output=Join-Path $root ".artifacts\release\v$version"
$app=Join-Path $output 'publish'; $tools=Join-Path $output 'migration-publish'
New-Item -ItemType Directory -Force -Path $output | Out-Null
Push-Location $root
try {
    & dotnet publish .\src\AsterLauncher.App\AsterLauncher.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -p:EnableMsixTooling=true -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    & dotnet publish .\src\AsterLauncher.Migrator\AsterLauncher.Migrator.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -o $tools
    if ($LASTEXITCODE -ne 0) { throw 'Migration tool publish failed.' }
} finally { Pop-Location }
$name="AsterLauncher-v$version-win-x64.zip"; $full=Join-Path $output $name
& (Join-Path $PSScriptRoot 'create-migration-package.ps1') -AppPublish $app -MigratorPublish $tools -OutputZip $full -Version $version
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($full)
try {
    $reader=[IO.StreamReader]::new($zip.GetEntry('release-files.json').Open())
    try { $target=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
} finally { $zip.Dispose() }
$entryHash=$target.files.'AsterLauncher.exe'.sha256
$manifest=[ordered]@{format=2;version=$version;full=[ordered]@{asset=$name;sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant();targetSha256=$entryHash}}
if ($PreviousPackage) {
    if (-not $PreviousVersion) { throw 'PreviousVersion is required.' }
    $previous=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PreviousPackage))
    $new=[IO.Compression.ZipFile]::OpenRead($full)
    $patchName="AsterLauncher-$PreviousVersion-to-$version-files-delta.zip"
    $patchPath=Join-Path $output $patchName
    if (Test-Path -LiteralPath $patchPath) { throw 'Delta asset exists; review it before rebuilding.' }
    try {
        $oldEntry=$previous.GetEntry('release-files.json')
        if ($null -eq $oldEntry) { throw 'A single-exe package needs manual migration, not a file delta.' }
        $reader=[IO.StreamReader]::new($oldEntry.Open())
        try { $base=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($base.version -ne $PreviousVersion) { throw 'Previous package version mismatch.' }
        $patch=[IO.Compression.ZipFile]::Open($patchPath,[IO.Compression.ZipArchiveMode]::Create)
        try {
            $plan=$patch.CreateEntry('file-delta.json');$writer=[IO.StreamWriter]::new($plan.Open(),[Text.UTF8Encoding]::new($false))
            try { $writer.Write(([ordered]@{format=2;from=$PreviousVersion;target=$target} | ConvertTo-Json -Depth 8 -Compress)) } finally { $writer.Dispose() }
            foreach ($property in $target.files.PSObject.Properties) {
                $key=$property.Name; $record=$property.Value
                $prior=$base.files.PSObject.Properties[$key]
                if ($null -ne $prior -and $prior.Value.sha256 -eq $record.sha256 -and $prior.Value.length -eq $record.length) { continue }
                $source=$new.GetEntry($key);$entry=$patch.CreateEntry($key,[IO.Compression.CompressionLevel]::Optimal)
                $input=$source.Open();$out=$entry.Open()
                try { $input.CopyTo($out) } finally { $input.Dispose();$out.Dispose() }
            }
        } finally { $patch.Dispose() }
    } finally { $previous.Dispose();$new.Dispose() }
    if ((Get-Item -LiteralPath $patchPath).Length -lt (Get-Item -LiteralPath $full).Length) {
        $patchHash=(Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest.patch=[ordered]@{from=$PreviousVersion;asset=$patchName;sha256=$patchHash;targetSha256=$entryHash}
        Set-Content -LiteralPath ($patchPath+'.sha256') -Value $patchHash -Encoding ASCII
    } else { Remove-Item -LiteralPath $patchPath -Force }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'aster-update-v2.json') -Encoding UTF8
Write-Output "Release assets: $output"
Write-Output 'Root: AsterLauncher.exe, App/, MigrationTools/, release-files.json. Data is excluded.'
