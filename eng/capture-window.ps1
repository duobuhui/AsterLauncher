[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$TargetProcessId,
    [Parameter(Mandatory)]
    [string]$OutputPath
)

. (Join-Path $PSScriptRoot 'set-env.ps1')
Add-Type -AssemblyName System.Drawing
$source = @"
using System;
using System.Runtime.InteropServices;
public static class AsterWindowCapture {
    public delegate bool EnumWindowsProc(IntPtr handle, IntPtr state);
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr hdc, uint flags);
}
"@
Add-Type -TypeDefinition $source
$process = Get-Process -Id $TargetProcessId -ErrorAction Stop
$script:window = [IntPtr]::Zero
$script:area = 0
[AsterWindowCapture]::EnumWindows({
    param($handle, $state)
    $owner = [uint32]0
    [void][AsterWindowCapture]::GetWindowThreadProcessId($handle, [ref]$owner)
    if ($owner -eq [uint32]$TargetProcessId -and [AsterWindowCapture]::IsWindowVisible($handle)) {
        $rect = New-Object AsterWindowCapture+Rect
        [void][AsterWindowCapture]::GetWindowRect($handle, [ref]$rect)
        $area = ($rect.Right - $rect.Left) * ($rect.Bottom - $rect.Top)
        if ($area -gt $script:area) {
            $script:area = $area
            $script:window = $handle
        }
    }
    return $true
}, [IntPtr]::Zero) | Out-Null
if ($script:area -lt 100000) { throw "No main window for PID $TargetProcessId" }
$rect = New-Object AsterWindowCapture+Rect
if (-not [AsterWindowCapture]::GetWindowRect($script:window, [ref]$rect)) {
    throw 'GetWindowRect failed'
}
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
try {
    if (-not [AsterWindowCapture]::PrintWindow($script:window, $hdc, 2)) {
        throw 'PrintWindow failed'
    }
}
finally {
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
}
try {
    $destination = [System.IO.Path]::GetFullPath($OutputPath)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
    $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output "Captured $width x $height from $($process.Path) to $destination"
}
finally {
    $bitmap.Dispose()
}
