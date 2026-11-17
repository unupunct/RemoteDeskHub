param(
    [Parameter(Mandatory = $true)]
    [string]$DataRoot,

    [Parameter(Mandatory = $true)]
    [string]$UserName,

    [Parameter(Mandatory = $true)]
    [string]$Password,

    [string]$ExePath,

    [string]$OutputDirectory,

    [string[]]$Pages = @('Dashboard', 'Computers', 'Remote Assistance', 'Settings/Security')
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $root 'src\RemoteDeskHub.Desktop\bin\Release\net10.0-windows\RemoteDeskHub.Desktop.exe'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'docs\images'
}

if (-not (Test-Path $ExePath)) {
    throw "Desktop executable was not found: $ExePath"
}
if (-not (Test-Path $DataRoot)) {
    throw "Data root was not found: $DataRoot"
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$DataRoot = [IO.Path]::GetFullPath($DataRoot)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class RemoteDeskScreenshotNative
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);
}
"@

try {
    [RemoteDeskScreenshotNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
}
catch {
    # Already DPI-aware; harmless.
}

$width = 1813
$height = 1750

function Find-AutomationWindow {
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [int]$TimeoutSeconds = 45
    )

    $rootElement = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        foreach ($window in $rootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)) {
            if ($window.Current.Name -like $Name) {
                return $window
            }
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Window '$Name' did not appear within $TimeoutSeconds seconds."
}

function Find-AutomationElementById {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $element = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) {
        throw "Automation element '$AutomationId' was not found."
    }

    return $element
}

function Set-AutomationValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Element,
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}

function Select-NavigationItem {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $listCondition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::List)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'ListBox')))
    $navList = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCondition)
    if ($null -eq $navList) {
        throw 'The sidebar navigation list was not found.'
    }

    # WPF does not expose the template TextBlocks through FindAll on the list item, so walk the
    # raw tree and match the visible label.
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $item = $walker.GetFirstChild($navList)
    while ($null -ne $item) {
        if ($item.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) {
            $child = $walker.GetFirstChild($item)
            while ($null -ne $child) {
                if ($child.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
                    $child.Current.Name -eq $Name) {
                    $pattern = $item.GetCurrentPattern(
                        [System.Windows.Automation.SelectionItemPattern]::Pattern)
                    $pattern.Select()
                    Start-Sleep -Seconds 3
                    return
                }
                $child = $walker.GetNextSibling($child)
            }
        }
        $item = $walker.GetNextSibling($item)
    }

    throw "Navigation item '$Name' was not found."
}

function Select-TabItem {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::TabItem)))
    $item = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $item) {
        throw "Tab '$Name' was not found."
    }

    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2
}

function Save-WindowScreenshot {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) {
        throw 'The main window has no native handle.'
    }

    [RemoteDeskScreenshotNative]::ShowWindow($handle, 9) | Out-Null
    [RemoteDeskScreenshotNative]::SetForegroundWindow($handle) | Out-Null
    [RemoteDeskScreenshotNative]::SetWindowPos(
        $handle, [IntPtr]::Zero, 0, 0, $width, $height, 0x0004) | Out-Null
    Start-Sleep -Milliseconds 1500

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [RemoteDeskScreenshotNative]::PrintWindow($handle, $hdc, 2)) {
            throw 'PrintWindow failed.'
        }
    }
    finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }

    try {
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    Write-Host "Captured $Path" -ForegroundColor Green
}

$previousDataRoot = $env:REMOTEDESKHUB_DATA
$process = $null
try {
    $env:REMOTEDESKHUB_DATA = $DataRoot
    $process = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath) -PassThru

    $login = Find-AutomationWindow -ProcessId $process.Id -Name 'Sign in*'
    $loginHandle = [IntPtr]$login.Current.NativeWindowHandle
    [RemoteDeskScreenshotNative]::SetForegroundWindow($loginHandle) | Out-Null
    Start-Sleep -Milliseconds 750

    $userNameBox = Find-AutomationElementById -Window $login -AutomationId 'UserNameBox'
    $passwordBox = Find-AutomationElementById -Window $login -AutomationId 'PasswordBox'
    $signInButton = Find-AutomationElementById -Window $login -AutomationId 'SignInButton'
    Set-AutomationValue -Element $userNameBox -Value $UserName
    Set-AutomationValue -Element $passwordBox -Value $Password
    $signInButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $main = Find-AutomationWindow -ProcessId $process.Id -Name 'RemoteDesk Hub' -TimeoutSeconds 60
    $mainHandle = [IntPtr]$main.Current.NativeWindowHandle
    [RemoteDeskScreenshotNative]::SetForegroundWindow($mainHandle) | Out-Null
    Start-Sleep -Seconds 5

    foreach ($page in $Pages) {
        $pageName = $page
        $tabName = $null
        if ($page.Contains('/')) {
            $parts = $page.Split('/', 2)
            $pageName = $parts[0]
            $tabName = $parts[1]
        }

        if ($pageName -ne 'Dashboard') {
            Select-NavigationItem -Window $main -Name $pageName
        }
        if (-not [string]::IsNullOrWhiteSpace($tabName)) {
            Select-TabItem -Window $main -Name $tabName
        }

        $slug = ($page.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
        Save-WindowScreenshot -Window $main -Path (Join-Path $OutputDirectory "$slug.png")
    }

    $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $process.WaitForExit(3000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    $env:REMOTEDESKHUB_DATA = $previousDataRoot
}
