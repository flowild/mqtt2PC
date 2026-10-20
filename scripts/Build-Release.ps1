[CmdletBinding()]
param([switch]$NoRestore)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$releaseDirectory = Join-Path $workspaceRoot 'artifacts\stage4'
$portableDirectory = Join-Path $releaseDirectory 'portable'
$appProject = Join-Path $workspaceRoot 'src\Mqtt2Pc\Mqtt2Pc.csproj'
$installerProject = Join-Path $workspaceRoot 'installer\mqtt2PC.Installer.wixproj'

Push-Location -LiteralPath $workspaceRoot
try {
    if (-not $NoRestore) {
        & dotnet restore $appProject --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Application restore failed.' }
        & dotnet restore $installerProject
        if ($LASTEXITCODE -ne 0) { throw 'Installer restore failed.' }
    }
    & dotnet publish $appProject --configuration Release --no-restore --output $portableDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'README.md') -Destination (Join-Path $portableDirectory 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $portableDirectory 'THIRD-PARTY-NOTICES.txt') -Force
    & dotnet build $installerProject --configuration Release --no-restore --target:Rebuild "-p:PublishDirectory=$portableDirectory" "-p:OutputPath=$releaseDirectory\"
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    $zipPath = Join-Path $releaseDirectory 'mqtt2PC-0.4.0-portable.zip'
    Compress-Archive -Path (Join-Path $portableDirectory '*') -DestinationPath $zipPath -Force
    $hashPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
    $artifacts = @((Join-Path $releaseDirectory 'mqtt2PC-0.4.0-x64.msi'), $zipPath)
    $lines = foreach ($artifact in $artifacts) {
        $digest = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
        '{0}  {1}' -f $digest.Hash.ToLowerInvariant(), (Split-Path -Leaf $artifact)
    }
    $lines | Set-Content -LiteralPath $hashPath -Encoding ASCII
    Write-Output "Release files: $releaseDirectory"
}
finally { Pop-Location }
