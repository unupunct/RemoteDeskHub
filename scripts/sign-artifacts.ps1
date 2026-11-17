param(
    [string]$PfxPath = '',

    [string]$PfxPassword = '',

    [string[]]$Files = @(),

    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$Description = 'RemoteDesk Labs',

    [switch]$NoTimestamp,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

if (-not $ValidateOnly) {
    if ([string]::IsNullOrWhiteSpace($PfxPath)) {
        throw 'PfxPath is required when signing.'
    }

    if (-not (Test-Path $PfxPath)) {
        throw "Signing certificate was not found: $PfxPath"
    }
}

$targets = @($Files | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if (-not $ValidateOnly -and $targets.Count -eq 0) {
    throw 'No files were supplied for signing.'
}

if (-not $ValidateOnly) {
    $missing = @($targets | Where-Object { -not (Test-Path $_) })
    if ($missing.Count -gt 0) {
        throw "Signing target(s) not found: $($missing -join ', ')"
    }
}

function Find-SignTool {
    $candidates = @()

    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $candidates += $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kitsRoot) {
        $candidates += Get-ChildItem $kitsRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -ExpandProperty FullName
    }

    $unique = @($candidates | Select-Object -Unique)
    return ($unique | Select-Object -First 1)
}

$signTool = Find-SignTool
if (-not $signTool) {
    throw 'signtool.exe was not found. Install the Windows SDK signing tools or run this script on a Windows runner with the SDK.'
}

Write-Host "Using signtool: $signTool" -ForegroundColor Cyan

foreach ($target in $targets) {
    $item = Get-Item $target
    Write-Host "Signing $($item.FullName)" -ForegroundColor Cyan

    if ($ValidateOnly) {
        continue
    }

    $arguments = @(
        'sign',
        '/fd', 'SHA256',
        '/f', $PfxPath,
        '/p', $PfxPassword,
        '/d', $Description
    )

    if (-not $NoTimestamp) {
        $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }

    $arguments += $item.FullName

    & $signTool @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for $($item.FullName) with exit code $LASTEXITCODE."
    }

    $signature = Get-AuthenticodeSignature -FilePath $item.FullName
    if ($signature.Status -ne 'Valid') {
        throw "Signature validation failed for $($item.FullName): $($signature.Status) $($signature.StatusMessage)"
    }

    Write-Host "Signed $($item.Name) ($($signature.SignerCertificate.Subject))" -ForegroundColor Green
}
