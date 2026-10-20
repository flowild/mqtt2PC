param(
    [switch]$ValidateOnly,
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'mqtt2PC\logs\screens-off.log')
)

$ErrorActionPreference = 'Stop'
[void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $LogPath))
"$(Get-Date -Format o) Starting; PowerShell=$($PSVersionTable.PSVersion); session=$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)" |
    Set-Content -LiteralPath $LogPath

try {

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Mqtt2PcDisplayPower
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out UIntPtr result);
}
'@

$messageResult = [UIntPtr]::Zero
# Windows PowerShell 5.1 cannot directly cast an Int32 to UIntPtr.
$monitorPowerCommand = [UIntPtr]::new([uint32]0xf170)
$broadcastWindow = [IntPtr]0xffff
$powerOff = [IntPtr]2
if ($ValidateOnly) {
    if ($monitorPowerCommand.ToUInt64() -ne 61808 -or $broadcastWindow.ToInt64() -ne 65535 -or $powerOff.ToInt64() -ne 2) {
        throw 'Display command argument validation failed.'
    }
    'Validation passed; no display command sent.' | Add-Content -LiteralPath $LogPath
    Write-Output 'Validation passed; no display command sent.'
    exit 0
}
# Allow the input that launched the script to settle before switching displays off.
Start-Sleep -Seconds 1
# HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, power off (2).
# Skip unresponsive windows; timeout is per receiving window.
$sendResult = [Mqtt2PcDisplayPower]::SendMessageTimeout(
    $broadcastWindow, 0x0112, $monitorPowerCommand, $powerOff,
    0x0002, 250, [ref]$messageResult)
if ($sendResult -eq [IntPtr]::Zero) {
    "$(Get-Date -Format o) Broadcast was not fully acknowledged; Windows error=$([Runtime.InteropServices.Marshal]::GetLastWin32Error())" | Add-Content -LiteralPath $LogPath
    Write-Warning 'One or more windows did not acknowledge the display-off request. Check the displays directly.'
}
else {
    "$(Get-Date -Format o) Display-off request sent; actual monitor state is not verified." | Add-Content -LiteralPath $LogPath
}
}
catch {
    "$(Get-Date -Format o) ERROR: $($_.Exception.Message)" | Add-Content -LiteralPath $LogPath
    Write-Error $_
    exit 1
}
