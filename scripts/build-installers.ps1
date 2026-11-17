param(
    [string]$Version = '1.0.0',

    [string]$SigningPfxPath,

    [string]$SigningPfxPassword,

    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

& (Join-Path $PSScriptRoot 'publish.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not [string]::IsNullOrWhiteSpace($SigningPfxPath)) {
    if ([string]::IsNullOrWhiteSpace($SigningPfxPassword)) {
        throw 'SigningPfxPassword is required when SigningPfxPath is supplied.'
    }

    $signingTargets = @(
        (Join-Path $root 'artifacts\publish\hub\RemoteDeskHub.Desktop.exe'),
        (Join-Path $root 'artifacts\publish\hub\RemoteDeskHub.HubHost.exe'),
        (Join-Path $root 'artifacts\publish\agent\RemoteDeskHub.Agent.exe')
    )

    & (Join-Path $PSScriptRoot 'sign-artifacts.ps1') `
        -PfxPath $SigningPfxPath `
        -PfxPassword $SigningPfxPassword `
        -TimestampUrl $TimestampUrl `
        -Description 'RemoteDesk Labs' `
        -Files $signingTargets
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$wixCommand = Get-Command wix -ErrorAction SilentlyContinue
if ($null -ne $wixCommand) {
    $wix = $wixCommand.Source
}
else {
    $wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
}

if (-not (Test-Path $wix)) {
    throw "WiX Toolset 5 was not found. Install it with: dotnet tool install --global wix --version 5.0.2"
}

$extensions = @(
    '-ext', 'WixToolset.UI.wixext',
    '-ext', 'WixToolset.Firewall.wixext'
)

$installerOutput = Join-Path $root 'artifacts\installers'
New-Item -ItemType Directory -Force -Path $installerOutput | Out-Null

$common = @(
    '-arch', 'x64',
    '-d', "ProductVersion=$Version",
    '-d', "LicenseRtf=$root\installer\License.rtf"
)

$hubMsi = Join-Path $installerOutput "RemoteDesk-Hub-$Version-x64.msi"
$agentMsi = Join-Path $installerOutput "RemoteDesk-Agent-$Version-x64.msi"

Write-Host "Building RemoteDesk Hub MSI..." -ForegroundColor Cyan
& $wix build installer\Hub\Hub.wxs @common @extensions `
    -d "PublishDir=$root\artifacts\publish\hub" `
    -o $hubMsi
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building RemoteDesk Agent MSI..." -ForegroundColor Cyan
& $wix build installer\Agent\Agent.wxs @common @extensions `
    -d "PublishDir=$root\artifacts\publish\agent" `
    -o $agentMsi
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not [string]::IsNullOrWhiteSpace($SigningPfxPath)) {
    & (Join-Path $PSScriptRoot 'sign-artifacts.ps1') `
        -PfxPath $SigningPfxPath `
        -PfxPassword $SigningPfxPassword `
        -TimestampUrl $TimestampUrl `
        -Description 'RemoteDesk Labs installer' `
        -Files @($hubMsi, $agentMsi)
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Get-ChildItem $installerOutput -Filter *.wixpdb | Remove-Item -Force

$hashes = Get-ChildItem $installerOutput -Filter *.msi | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
}
Set-Content -Path (Join-Path $installerOutput 'SHA256SUMS.txt') -Value $hashes -Encoding ascii

Write-Host "Installers written to $installerOutput" -ForegroundColor Green
Get-ChildItem $installerOutput -Filter *.msi | Select-Object Name, Length
