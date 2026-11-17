param(
    [string]$Version = '1.0.0',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publishRoot = Join-Path $root 'artifacts\publish'
$hubOutput = Join-Path $publishRoot 'hub'
$agentOutput = Join-Path $publishRoot 'agent'

if (Test-Path $publishRoot) {
    Remove-Item $publishRoot -Recurse -Force
}

Write-Host "Publishing RemoteDesk Hub $Version ($Runtime, self-contained)..." -ForegroundColor Cyan
dotnet publish src\RemoteDeskHub.Desktop\RemoteDeskHub.Desktop.csproj `
    -c Release -r $Runtime --self-contained true -o $hubOutput `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing RemoteDesk Hub headless host $Version ($Runtime, self-contained)..." -ForegroundColor Cyan
dotnet publish src\RemoteDeskHub.HubHost\RemoteDeskHub.HubHost.csproj `
    -c Release -r $Runtime --self-contained true -o $hubOutput `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing RemoteDesk Agent $Version ($Runtime, self-contained)..." -ForegroundColor Cyan
dotnet publish src\RemoteDeskHub.Agent\RemoteDeskHub.Agent.csproj `
    -c Release -r $Runtime --self-contained true -o $agentOutput `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Published to $publishRoot" -ForegroundColor Green
