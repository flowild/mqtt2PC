param(
    [switch]$DiagnoseOnly,
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'mqtt2PC\logs\signalrgb-window.log')
)

$ErrorActionPreference = 'Stop'
$logDirectory = Split-Path -Parent $LogPath
[void][System.IO.Directory]::CreateDirectory($logDirectory)
"$(Get-Date -Format o) Starting window check; session=$([System.Diagnostics.Process]::GetCurrentProcess().SessionId); diagnose=$DiagnoseOnly" |
    Set-Content -LiteralPath $LogPath

try {

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class SignalRgbWindow
{
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static IntPtr[] FindWindows(int[] processIds)
    {
        var ids = new HashSet<int>(processIds);
        var windows = new List<IntPtr>();
        EnumWindows(delegate(IntPtr window, IntPtr parameter) {
            uint id;
            GetWindowThreadProcessId(window, out id);
            if (ids.Contains((int)id) && IsWindowVisible(window)) {
                var title = new StringBuilder(1024);
                if (GetWindowText(window, title, title.Capacity) > 0)
                    windows.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
}
'@

# SignalRGB must be configured to close its window to the tray.
# Allow the URL handler to apply the effect before requesting a normal window close.
Start-Sleep -Seconds 1
$closedWindows = [System.Collections.Generic.HashSet[System.IntPtr]]::new()
$lastWindowCount = -1
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Milliseconds 250
    $processIds = @([System.Diagnostics.Process]::GetProcessesByName('SignalRgb') | ForEach-Object {
        try { $_.Id } finally { $_.Dispose() }
    })
    $windows = @([SignalRgbWindow]::FindWindows([int[]]$processIds))
    if ($windows.Count -ne $lastWindowCount) {
        "$(Get-Date -Format o) Processes=$($processIds -join ','); visible windows=$($windows.Count)" | Add-Content -LiteralPath $LogPath
        $lastWindowCount = $windows.Count
    }
    foreach ($window in $windows) {
        if (-not $DiagnoseOnly -and -not $closedWindows.Contains($window)) {
            if (-not [SignalRgbWindow]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
                $nativeError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                throw "Window close was blocked (Windows error $nativeError). Error 5 usually means SignalRGB runs elevated while mqtt2PC does not."
            }
            [void]$closedWindows.Add($window)
            "$(Get-Date -Format o) Close requested for window=$window" | Add-Content -LiteralPath $LogPath
        }
    }
    if ($DiagnoseOnly) { break }
}
if (-not $DiagnoseOnly -and $lastWindowCount -gt 0) {
    throw 'SignalRGB still has a visible window after the close request.'
}
"$(Get-Date -Format o) Finished; close requests=$($closedWindows.Count)" | Add-Content -LiteralPath $LogPath
}
catch {
    "$(Get-Date -Format o) ERROR: $($_.Exception.Message)" | Add-Content -LiteralPath $LogPath
    Write-Error $_
    exit 1
}
