param(
    [string]$Version = '1.0.0',

    [string]$InstallerDir,

    [string]$UpgradeVersion,

    [string]$UpgradeInstallerDir,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($InstallerDir)) {
    $InstallerDir = Join-Path $root 'artifacts\installers'
}

$InstallerDir = [IO.Path]::GetFullPath($InstallerDir)
if (-not (Test-Path $InstallerDir)) {
    throw "Installer directory was not found: $InstallerDir"
}

function Get-InstallerPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,
        [Parameter(Mandatory = $true)]
        [string]$Product,
        [Parameter(Mandatory = $true)]
        [string]$ProductVersion
    )

    $path = Join-Path $Directory "RemoteDesk-$Product-$ProductVersion-x64.msi"
    if (-not (Test-Path $path)) {
        throw "Installer was not found: $path"
    }

    return $path
}

$hubMsi = Get-InstallerPath -Directory $InstallerDir -Product 'Hub' -ProductVersion $Version
$agentMsi = Get-InstallerPath -Directory $InstallerDir -Product 'Agent' -ProductVersion $Version

$upgradeHubMsi = $null
$upgradeAgentMsi = $null
if (-not [string]::IsNullOrWhiteSpace($UpgradeInstallerDir)) {
    if ([string]::IsNullOrWhiteSpace($UpgradeVersion)) {
        throw 'UpgradeVersion is required when UpgradeInstallerDir is supplied.'
    }

    if ([version]$UpgradeVersion -le [version]$Version) {
        throw "UpgradeVersion ($UpgradeVersion) must be greater than Version ($Version)."
    }

    $UpgradeInstallerDir = [IO.Path]::GetFullPath($UpgradeInstallerDir)
    $upgradeHubMsi = Get-InstallerPath -Directory $UpgradeInstallerDir -Product 'Hub' -ProductVersion $UpgradeVersion
    $upgradeAgentMsi = Get-InstallerPath -Directory $UpgradeInstallerDir -Product 'Agent' -ProductVersion $UpgradeVersion
}

if ($ValidateOnly) {
    Write-Host "Installers found:" -ForegroundColor Green
    $hubMsi, $agentMsi, $upgradeHubMsi, $upgradeAgentMsi |
        Where-Object { $_ } |
        ForEach-Object { Write-Host "  $_" }
    return
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installer tests require an elevated PowerShell session because the MSIs install a Windows service and firewall rules.'
}

$logBase = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { $env:TEMP } else { $env:RUNNER_TEMP }
$logRoot = Join-Path $logBase ("RemoteDeskInstallerTests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
Write-Host "Installer test logs: $logRoot" -ForegroundColor Cyan

$hubInstalledMsi = $null
$agentInstalledMsi = $null

function Invoke-MsiInstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Msi,
        [Parameter(Mandatory = $true)]
        [string]$LogName,
        [switch]$EnableRdpFirewall
    )

    $log = Join-Path $logRoot $LogName
    $arguments = "/i `"$Msi`" /qn /norestart /l*v `"$log`""
    if ($EnableRdpFirewall) {
        $arguments += ' ENABLE_RDP_FIREWALL=1'
    }
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -Wait -PassThru

    if ($process.ExitCode -notin @(0, 3010)) {
        if (Test-Path $log) {
            Write-Host "--- Tail of $log ---" -ForegroundColor Yellow
            Get-Content $log -Tail 120 | ForEach-Object { Write-Host $_ }
        }
        throw "MSI install failed for $Msi (exit code $($process.ExitCode)). See $log"
    }

    Write-Host "Installed $(Split-Path -Leaf $Msi)" -ForegroundColor Green
}

function Invoke-MsiUninstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Msi,
        [Parameter(Mandatory = $true)]
        [string]$LogName,
        [switch]$AllowMissing
    )

    $log = Join-Path $logRoot $LogName
    $arguments = "/x `"$Msi`" /qn /norestart /l*v `"$log`""
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -Wait -PassThru

    if ($process.ExitCode -eq 1605 -and $AllowMissing) {
        return
    }

    if ($process.ExitCode -notin @(0, 3010)) {
        if (Test-Path $log) {
            Write-Host "--- Tail of $log ---" -ForegroundColor Yellow
            Get-Content $log -Tail 120 | ForEach-Object { Write-Host $_ }
        }
        throw "MSI uninstall failed for $Msi (exit code $($process.ExitCode)). See $log"
    }

    Write-Host "Uninstalled $(Split-Path -Leaf $Msi)" -ForegroundColor Green
}

function Assert-RegistryVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Product,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    $key = "HKLM:\SOFTWARE\RemoteDesk Labs\$Product"
    $value = Get-ItemProperty -Path $key -ErrorAction Stop
    if ($value.Version -ne $ExpectedVersion) {
        throw "$Product registry version is '$($value.Version)', expected '$ExpectedVersion'."
    }

    if ([string]::IsNullOrWhiteSpace($value.InstallFolder) -or -not (Test-Path $value.InstallFolder)) {
        throw "$Product install folder is missing: '$($value.InstallFolder)'."
    }

    return $value.InstallFolder
}

function Assert-FirewallRule {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DisplayName,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedPort
    )

    $rule = Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction Stop | Select-Object -First 1
    $port = $rule | Get-NetFirewallPortFilter
    if ([string]$port.LocalPort -ne $ExpectedPort) {
        throw "Firewall rule '$DisplayName' uses port '$($port.LocalPort)', expected '$ExpectedPort'."
    }

    $address = $rule | Get-NetFirewallAddressFilter
    if (@($address.RemoteAddress) -notcontains 'LocalSubnet') {
        throw "Firewall rule '$DisplayName' is not scoped to LocalSubnet (RemoteAddress=$($address.RemoteAddress -join ','))."
    }
}

function Assert-FirewallRuleAbsent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DisplayName
    )

    if (Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue) {
        throw "Firewall rule '$DisplayName' exists but should not be installed."
    }
}

function Wait-ServiceRemoved {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    for ($i = 0; $i -lt 30; $i++) {
        if ($null -eq (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Seconds 1
    }

    throw "Service '$Name' is still registered after uninstall."
}

try {
    Write-Host '--- Hub install ---' -ForegroundColor Cyan
    Invoke-MsiInstall -Msi $hubMsi -LogName 'hub-install.log'
    $hubInstalledMsi = $hubMsi
    $hubFolder = Assert-RegistryVersion -Product 'RemoteDesk Hub' -ExpectedVersion $Version
    if (-not (Test-Path (Join-Path $hubFolder 'RemoteDeskHub.Desktop.exe'))) {
        throw 'Hub executable was not installed.'
    }
    Assert-FirewallRule -DisplayName 'RemoteDesk Hub (HTTPS)' -ExpectedPort '7443'

    if ($upgradeHubMsi) {
        Write-Host '--- Hub upgrade ---' -ForegroundColor Cyan
        Invoke-MsiInstall -Msi $upgradeHubMsi -LogName 'hub-upgrade.log'
        $hubInstalledMsi = $upgradeHubMsi
        $hubFolder = Assert-RegistryVersion -Product 'RemoteDesk Hub' -ExpectedVersion $UpgradeVersion
        if (-not (Test-Path (Join-Path $hubFolder 'RemoteDeskHub.Desktop.exe'))) {
            throw 'Hub executable was not present after upgrade.'
        }
        Assert-FirewallRule -DisplayName 'RemoteDesk Hub (HTTPS)' -ExpectedPort '7443'
    }

    Write-Host '--- Hub uninstall ---' -ForegroundColor Cyan
    Invoke-MsiUninstall -Msi $hubInstalledMsi -LogName 'hub-uninstall.log'
    $hubInstalledMsi = $null
    if (Test-Path 'HKLM:\SOFTWARE\RemoteDesk Labs\RemoteDesk Hub') {
        throw 'Hub registry key remains after uninstall.'
    }
    if (Get-NetFirewallRule -DisplayName 'RemoteDesk Hub (HTTPS)' -ErrorAction SilentlyContinue) {
        throw 'Hub firewall rule remains after uninstall.'
    }

    Write-Host '--- Agent install ---' -ForegroundColor Cyan
    Invoke-MsiInstall -Msi $agentMsi -LogName 'agent-install.log'
    $agentInstalledMsi = $agentMsi
    $agentFolder = Assert-RegistryVersion -Product 'RemoteDesk Agent' -ExpectedVersion $Version
    if (-not (Test-Path (Join-Path $agentFolder 'RemoteDeskHub.Agent.exe'))) {
        throw 'Agent executable was not installed.'
    }

    $service = Get-Service -Name 'RemoteDeskAgent' -ErrorAction Stop
    if ($service.StartType -ne 'Automatic') {
        throw "Agent service start type is '$($service.StartType)', expected Automatic."
    }
    Assert-FirewallRule -DisplayName 'RemoteDesk Agent (management)' -ExpectedPort '7444'
    Assert-FirewallRuleAbsent -DisplayName 'RemoteDesk Agent (Remote Desktop)'

    Start-Service -Name 'RemoteDeskAgent'
    Start-Sleep -Seconds 5
    $service.Refresh()
    if ($service.Status -ne 'Running') {
        throw "Agent service did not start (status $($service.Status))."
    }

    if ($upgradeAgentMsi) {
        Write-Host '--- Agent upgrade ---' -ForegroundColor Cyan
        Invoke-MsiInstall -Msi $upgradeAgentMsi -LogName 'agent-upgrade.log'
        $agentInstalledMsi = $upgradeAgentMsi
        $agentFolder = Assert-RegistryVersion -Product 'RemoteDesk Agent' -ExpectedVersion $UpgradeVersion
        if (-not (Test-Path (Join-Path $agentFolder 'RemoteDeskHub.Agent.exe'))) {
            throw 'Agent executable was not present after upgrade.'
        }
        $service = Get-Service -Name 'RemoteDeskAgent' -ErrorAction Stop
        if ($service.StartType -ne 'Automatic') {
            throw "Agent service start type is '$($service.StartType)' after upgrade, expected Automatic."
        }
        Start-Sleep -Seconds 5
        $service.Refresh()
        if ($service.Status -ne 'Running') {
            throw "Agent service did not start after upgrade (status $($service.Status))."
        }
        Assert-FirewallRule -DisplayName 'RemoteDesk Agent (management)' -ExpectedPort '7444'
        Assert-FirewallRuleAbsent -DisplayName 'RemoteDesk Agent (Remote Desktop)'
    }

    Write-Host '--- Agent uninstall ---' -ForegroundColor Cyan
    Stop-Service -Name 'RemoteDeskAgent' -Force -ErrorAction SilentlyContinue
    Invoke-MsiUninstall -Msi $agentInstalledMsi -LogName 'agent-uninstall.log'
    $agentInstalledMsi = $null
    Wait-ServiceRemoved -Name 'RemoteDeskAgent'
    if (Get-NetFirewallRule -DisplayName 'RemoteDesk Agent (management)' -ErrorAction SilentlyContinue) {
        throw 'Agent management firewall rule remains after uninstall.'
    }
    Assert-FirewallRuleAbsent -DisplayName 'RemoteDesk Agent (Remote Desktop)'

    Write-Host '--- Agent install with optional Remote Desktop firewall ---' -ForegroundColor Cyan
    Invoke-MsiInstall -Msi $agentMsi -LogName 'agent-rdp-install.log' -EnableRdpFirewall
    $agentInstalledMsi = $agentMsi
    Assert-FirewallRule -DisplayName 'RemoteDesk Agent (management)' -ExpectedPort '7444'
    Assert-FirewallRule -DisplayName 'RemoteDesk Agent (Remote Desktop)' -ExpectedPort '3389'

    Stop-Service -Name 'RemoteDeskAgent' -Force -ErrorAction SilentlyContinue
    Invoke-MsiUninstall -Msi $agentInstalledMsi -LogName 'agent-rdp-uninstall.log'
    $agentInstalledMsi = $null
    Wait-ServiceRemoved -Name 'RemoteDeskAgent'
    Assert-FirewallRuleAbsent -DisplayName 'RemoteDesk Agent (management)'
    Assert-FirewallRuleAbsent -DisplayName 'RemoteDesk Agent (Remote Desktop)'

    Write-Host 'Installer install/upgrade/uninstall tests passed.' -ForegroundColor Green
}
finally {
    if ($agentInstalledMsi) {
        Stop-Service -Name 'RemoteDeskAgent' -Force -ErrorAction SilentlyContinue
        try {
            Invoke-MsiUninstall -Msi $agentInstalledMsi -LogName 'agent-cleanup.log' -AllowMissing
        }
        catch {
            Write-Warning "Agent cleanup failed: $($_.Exception.Message)"
        }
    }

    if ($hubInstalledMsi) {
        try {
            Invoke-MsiUninstall -Msi $hubInstalledMsi -LogName 'hub-cleanup.log' -AllowMissing
        }
        catch {
            Write-Warning "Hub cleanup failed: $($_.Exception.Message)"
        }
    }

    Write-Host "Installer logs retained in $logRoot" -ForegroundColor Cyan
}
