[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$releaseDirectory = Join-Path $workspaceRoot 'artifacts\stage4'
$msiPath = Join-Path $releaseDirectory 'mqtt2PC-0.4.0-x64.msi'
$zipPath = Join-Path $releaseDirectory 'mqtt2PC-0.4.0-portable.zip'

# Open the MSI database read-only. No product is installed, repaired or removed.
if (-not ('Mqtt2PcMsiReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class Mqtt2PcMsiReader
{
    public static List<Dictionary<string, string>> Read(string path, string sql, string[] columns)
    {
        uint database = 0, view = 0;
        try
        {
            Check(MsiOpenDatabase(path, IntPtr.Zero, out database));
            Check(MsiDatabaseOpenView(database, sql, out view));
            Check(MsiViewExecute(view, 0));
            var rows = new List<Dictionary<string, string>>();
            while (true)
            {
                uint record;
                uint result = MsiViewFetch(view, out record);
                if (result == 259) break;
                Check(result);
                try
                {
                    var row = new Dictionary<string, string>();
                    for (uint index = 1; index <= columns.Length; index++)
                    {
                        var value = new StringBuilder(4096);
                        uint size = (uint)value.Capacity;
                        result = MsiRecordGetString(record, index, value, ref size);
                        if (result == 234)
                        {
                            value = new StringBuilder((int)size + 1);
                            size = (uint)value.Capacity;
                            result = MsiRecordGetString(record, index, value, ref size);
                        }
                        Check(result);
                        row.Add(columns[index - 1], value.ToString());
                    }
                    rows.Add(row);
                }
                finally { MsiCloseHandle(record); }
            }
            return rows;
        }
        finally
        {
            if (view != 0) MsiCloseHandle(view);
            if (database != 0) MsiCloseHandle(database);
        }
    }
    private static void Check(uint error) { if (error != 0) throw new Win32Exception((int)error); }
    [DllImport("msi.dll", EntryPoint="MsiOpenDatabaseW", CharSet=CharSet.Unicode)]
    private static extern uint MsiOpenDatabase(string path, IntPtr mode, out uint database);
    [DllImport("msi.dll", EntryPoint="MsiDatabaseOpenViewW", CharSet=CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenView(uint database, string sql, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", EntryPoint="MsiRecordGetStringW", CharSet=CharSet.Unicode)]
    private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder value, ref uint size);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
}
'@
}

function Assert-Release([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Release check failed: $Message" }
}

Assert-Release (Test-Path -LiteralPath $msiPath) 'MSI is missing; run Build-Release.ps1 first.'
Assert-Release (Test-Path -LiteralPath $zipPath) 'Portable archive is missing.'
$properties = @{}
foreach ($row in [Mqtt2PcMsiReader]::Read($msiPath, 'SELECT `Property`, `Value` FROM `Property`', @('Property', 'Value'))) {
    $properties[$row['Property']] = $row['Value']
}
Assert-Release ($properties['ProductName'] -eq 'mqtt2PC') 'Product name.'
Assert-Release ($properties['ProductVersion'] -eq '0.4.0') 'Product version.'
Assert-Release ([string]$properties['ALLUSERS'] -eq '') 'Package must install per user.'
Assert-Release ($properties['UpgradeCode'] -eq '{39B17F25-B91C-4FC4-9714-F44009C66018}') 'Upgrade family.'

$directories = @{}
foreach ($row in [Mqtt2PcMsiReader]::Read($msiPath, 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`', @('Id', 'Parent', 'Name'))) {
    $directories[$row['Id']] = $row
}
Assert-Release ($directories['INSTALLFOLDER']['Parent'] -eq 'AppPrograms') 'Install directory parent.'
Assert-Release ($directories['AppPrograms']['Parent'] -eq 'LocalAppDataFolder') 'Install must use LocalAppData.'
Assert-Release ($directories['AppMenuFolder']['Parent'] -eq 'ProgramMenuFolder') 'Start menu directory.'

$files = @([Mqtt2PcMsiReader]::Read($msiPath, 'SELECT `FileName`, `FileSize` FROM `File`', @('Name', 'Size')))
$names = @($files | ForEach-Object { $_['Name'].Split('|')[-1] })
$expected = @('mqtt2PC.exe', 'mqtt2PC.dll', 'mqtt2PC.deps.json', 'mqtt2PC.runtimeconfig.json', 'Mqtt2Pc.Core.dll', 'MQTTnet.dll', 'README.md', 'THIRD-PARTY-NOTICES.txt')
Assert-Release ($files.Count -eq $expected.Count) 'Unexpected MSI payload files.'
foreach ($name in $expected) { Assert-Release ($names -contains $name) "Payload file: $name" }
Assert-Release (-not ($files | Where-Object { [long]$_['Size'] -le 0 })) 'Payload file size.'

$registry = @([Mqtt2PcMsiReader]::Read($msiPath, 'SELECT `Root`, `Key` FROM `Registry`', @('Root', 'Key')))
foreach ($row in $registry) {
    Assert-Release ($row['Root'] -eq '1') 'Installer registry entries must use HKCU.'
    Assert-Release ($row['Key'] -eq 'Software\mqtt2PC\Installer') 'Installer must not modify unrelated registry keys.'
}
$shortcuts = @([Mqtt2PcMsiReader]::Read($msiPath, 'SELECT `Target`, `WkDir` FROM `Shortcut`', @('Target', 'WorkingDirectory')))
Assert-Release ($shortcuts.Count -eq 1 -and $shortcuts[0]['Target'] -eq '[INSTALLFOLDER]mqtt2PC.exe') 'Start menu shortcut target.'
Assert-Release ($shortcuts[0]['WorkingDirectory'] -eq 'INSTALLFOLDER') 'Shortcut working directory.'

$removals = @([Mqtt2PcMsiReader]::Read($msiPath, 'SELECT `FileName`, `DirProperty`, `InstallMode` FROM `RemoveFile`', @('Name', 'Directory', 'Mode')))
$startupRemoval = @($removals | Where-Object { $_['Name'] -eq 'mqtt2PC.lnk' })
Assert-Release ($startupRemoval.Count -eq 1 -and $startupRemoval[0]['Directory'] -eq 'STARTUP_CLEANUP_DIR' -and $startupRemoval[0]['Mode'] -eq '2') 'Owned startup shortcut cleanup on uninstall.'
$sequence = @([Mqtt2PcMsiReader]::Read($msiPath, 'SELECT `Action`, `Condition` FROM `InstallExecuteSequence`', @('Action', 'Condition')))
Assert-Release ([bool]($sequence | Where-Object { $_['Action'] -eq 'SetSTARTUP_CLEANUP_DIR' -and $_['Condition'] -match 'NOT UPGRADINGPRODUCTCODE' })) 'Startup preference must survive major upgrades.'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($name in $expected) { Assert-Release ($archive.Entries.FullName -contains $name) "Portable file: $name" }
}
finally { $archive.Dispose() }
foreach ($line in Get-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt')) {
    $parts = $line -split '  ', 2
    $artifact = Join-Path $releaseDirectory $parts[1]
    Assert-Release ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant() -eq $parts[0]) "SHA256: $($parts[1])"
}
Write-Output 'Release checks passed: MSI scope, payload, shortcuts, startup cleanup, upgrade rule, portable archive and hashes.'
