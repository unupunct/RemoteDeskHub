param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Restoring packages..." -ForegroundColor Cyan
dotnet restore RemoteDeskHub.slnx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building $Configuration..." -ForegroundColor Cyan
dotnet build RemoteDeskHub.slnx -c $Configuration --no-restore -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Running unit tests..." -ForegroundColor Cyan
dotnet test tests\RemoteDeskHub.UnitTests\RemoteDeskHub.UnitTests.csproj -c $Configuration --no-build --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Running integration tests..." -ForegroundColor Cyan
dotnet test tests\RemoteDeskHub.IntegrationTests\RemoteDeskHub.IntegrationTests.csproj -c $Configuration --no-build --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Build and tests completed successfully." -ForegroundColor Green
