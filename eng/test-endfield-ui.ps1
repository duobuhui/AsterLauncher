[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppExe,[switch]$InstalledFixture,[switch]$CheckDefaultDirectory)
$ErrorActionPreference='Stop'
if($InstalledFixture -and $CheckDefaultDirectory){throw 'Run installed-update and default-directory fixtures separately.'}
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
$env:ASTERLAUNCHER_UI_TEST_EXPANDED='0'
$env:ASTERLAUNCHER_UI_TEST_CONTEXT='0'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$process=Start-Process -FilePath ([IO.Path]::GetFullPath($AppExe)) -WorkingDirectory (Split-Path -Parent ([IO.Path]::GetFullPath($AppExe))) -PassThru
try {
    function WaitForWindow {
        for($i=0;$i -lt 60;$i++) {
            Start-Sleep -Milliseconds 300
            $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
            $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)
            $result=$windows | Sort-Object -Descending -Property {$_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height} | Select-Object -First 1
            if($result){return $result}
        }
        throw 'WinUI window did not appear.'
    }
    $window=WaitForWindow
    function Find([string]$name) {
        $c=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
        $result=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$c)
        if($result){return $result}
        $own=[Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))
        foreach($popup in $own){
            $result=$popup.FindFirst([Windows.Automation.TreeScope]::Descendants,$c)
            if($result){return $result}
        }
        return $null
    }
    function Invoke([string]$name) {
        $element=Find $name
        if(-not $element){throw "UI element absent: $name"}
        $pattern=$null
        if($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)){$pattern.Invoke()}
        elseif($element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)){$pattern.Toggle()}
        else {throw "UI element has no action pattern: $name"}
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
    # The Debug hook opens the real ContextFlyout; this checks its actions rather than injecting mouse input.
    # Release builds ignore the hook and need a separate manual right-click check.
    if(-not $process.HasExited){
        [void]$process.CloseMainWindow()
        if(-not $process.WaitForExit(3000)){Stop-Process -Id $process.Id -Force}
    }
    $env:ASTERLAUNCHER_UI_TEST_EXPANDED='1'
    $env:ASTERLAUNCHER_UI_TEST_CONTEXT='1'
    $env:ASTERLAUNCHER_DATA_HOME=$data
    $process=Start-Process -FilePath ([IO.Path]::GetFullPath($AppExe)) -WorkingDirectory (Split-Path -Parent ([IO.Path]::GetFullPath($AppExe))) -PassThru
    $window=WaitForWindow
    Start-Sleep -Seconds 5
    if(-not (Find '收起启动选项')){throw 'Use a Debug build for the context-menu automation hook.'}
    & (Join-Path $PSScriptRoot 'capture-window.ps1') -TargetProcessId $process.Id -OutputPath (Join-Path $root '.artifacts\screenshots\v0.1.3-endfield-official.png')
    $servers=Find '服务器'
    if(-not $servers){throw 'The game context menu did not open. Use a Debug build for this automation.'}
    $servers.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    Invoke '哔哩哔哩服'
    Start-Sleep -Seconds 5
    & (Join-Path $PSScriptRoot 'capture-window.ps1') -TargetProcessId $process.Id -OutputPath (Join-Path $root '.artifacts\screenshots\v0.1.3-endfield-bilibili.png')
    $all=$window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    $all | ForEach-Object {$_.Current.Name} | Where-Object {$_} | Set-Content -LiteralPath (Join-Path $data 'bilibili-ui.txt') -Encoding UTF8
    $saved=Get-Content -LiteralPath (Join-Path $data 'launcher.settings.json') -Raw | ConvertFrom-Json
    if($saved.selectedEndfieldChannel -ne 'Bilibili'){throw 'Context-menu selection was not saved.'}
    $installation=$saved.endfieldInstallations | Where-Object {$_.channel -eq 'Bilibili'} | Select-Object -First 1
    if($saved.selectedProfileId -ne $installation.selectedLaunchProfileId){throw 'Launch profile did not follow the selected channel.'}
    if(-not (Find '选择启动方案或客户端')){throw 'Launch-profile selector absent.'}
    Write-Output "Channel menu and profile verified. UI fixture PID=$($process.Id), data=$data"
} finally {
    if(-not $process.HasExited){
        [void]$process.CloseMainWindow()
        if(-not $process.WaitForExit(3000)){Stop-Process -Id $process.Id -Force}
    }
}
