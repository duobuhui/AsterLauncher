[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppExe,[switch]$InstalledFixture,[switch]$CheckDefaultDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'set-env.ps1')
$root=Split-Path -Parent $PSScriptRoot
$data=Join-Path $root ('.artifacts\v013-ui-'+$(if($InstalledFixture){'installed'}else{'data'}))
New-Item -ItemType Directory -Force -Path $data | Out-Null
$config=[ordered]@{schemaVersion=4;firstRunCompleted=$true;closeButtonBehavior='Exit';selectedGameId='endfield';selectedEndfieldChannel='Official';gameDownloadDirectory=(Join-Path $data 'default-games');endfieldInstallations=@()}
if($InstalledFixture){
    $game=Join-Path $data 'fixture-official'
    New-Item -ItemType Directory -Force -Path $game | Out-Null
    $entry=Join-Path $game 'Endfield.exe'
    [IO.File]::WriteAllText($entry,'isolated UI fixture; never executed as a game')
    $md5=(Get-FileHash -LiteralPath $entry -Algorithm MD5).Hash
    @"
[Game]
appcode=6LL0KJuqHBVz33WK
channel=1
sub_channel=1
version=1.0.0
entry=Endfield.exe
entry_md5=$md5
"@ | Set-Content -LiteralPath (Join-Path $game 'config.ini') -Encoding UTF8
    $config.endfieldInstallations=@(@{installationId=[Guid]::NewGuid().ToString();channel='Official';installRoot=$game;executablePath=$entry;installedVersion='1.0.0'})
}
$config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $data 'launcher.settings.json') -Encoding UTF8
$env:ASTERLAUNCHER_DATA_HOME=$data
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$process=Start-Process -FilePath ([IO.Path]::GetFullPath($AppExe)) -WorkingDirectory (Split-Path -Parent ([IO.Path]::GetFullPath($AppExe))) -PassThru
try {
    $window=$null
    for($i=0;$i -lt 60;$i++) {
        Start-Sleep -Milliseconds 500
        $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
        $window=[Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
        if($window){break}
    }
    if(-not $window){throw 'WinUI window did not appear.'}
    function Find([string]$name) {
        $c=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
        return $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$c)
    }
    function Invoke([string]$name) {
        $element=Find $name
        if(-not $element){throw "UI element absent: $name"}
        $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Start-Sleep -Seconds 5
    $all=$window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    $all | ForEach-Object {$_.Current.Name} | Where-Object {$_} | Set-Content -LiteralPath (Join-Path $data 'initial-ui.txt') -Encoding UTF8
    if($InstalledFixture){
        if(-not (Find '更新游戏')){throw 'Installed old version did not change primary button to update.'}
        & (Join-Path $PSScriptRoot 'capture-window.ps1') -TargetProcessId $process.Id -OutputPath (Join-Path $root '.artifacts\screenshots\v0.1.3-endfield-update-button.png')
    }
    if($CheckDefaultDirectory){
        Invoke '启动或维护当前游戏'
        $dialog=$null
        for($attempt=0;$attempt -lt 40;$attempt++){
            Start-Sleep -Milliseconds 500
            $dialog=Find '安装或更新当前渠道？'
            if($dialog){break}
        }
        if(-not $dialog){throw 'Official install plan did not open.'}
        $expected=Join-Path $config.gameDownloadDirectory 'AsterLauncher\Endfield\Official'
        if(-not (Test-Path -LiteralPath $expected -PathType Container)){throw 'Configured default installation directory was not created.'}
        Invoke '取消'
        Write-Output "Default root created and download cancelled: $expected"
    }
    Invoke '展开启动选项'
    Start-Sleep -Seconds 2
    & (Join-Path $PSScriptRoot 'capture-window.ps1') -TargetProcessId $process.Id -OutputPath (Join-Path $root '.artifacts\screenshots\v0.1.3-endfield-official.png')
    Invoke '选择终末地哔哩哔哩服'
    Start-Sleep -Seconds 5
    & (Join-Path $PSScriptRoot 'capture-window.ps1') -TargetProcessId $process.Id -OutputPath (Join-Path $root '.artifacts\screenshots\v0.1.3-endfield-bilibili.png')
    $all=$window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    $all | ForEach-Object {$_.Current.Name} | Where-Object {$_} | Set-Content -LiteralPath (Join-Path $data 'bilibili-ui.txt') -Encoding UTF8
    Write-Output "UI fixture PID=$($process.Id), data=$data"
} finally {
    if(-not $process.HasExited){Stop-Process -Id $process.Id -Force}
}
