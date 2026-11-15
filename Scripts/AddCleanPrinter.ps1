#Requires -Version 5.1

<#
.SYNOPSIS
    Installs the Clean Print virtual printer.

.DESCRIPTION
    Checks for administrator privileges and automatically relaunches
    the script with elevated privileges when required.

    The script:
      - Creates the Clean Print capture directory
      - Locates the Microsoft XPS Document Writer driver
      - Creates the Clean Print printer port
      - Creates the "Clean Print" printer

.NOTES
    Run normally. The script will request Administrator privileges automatically.
#>

$ErrorActionPreference = 'Stop'

# ============================================================
# Configuration
# ============================================================

$PrinterName = 'Clean Print'

$CaptureFile = Join-Path `
    $env:LOCALAPPDATA `
    'CleanPrint\Capture\incoming.xps'

$CaptureDirectory = Split-Path -Parent $CaptureFile

# ============================================================
# Check Administrator Privileges
# ============================================================

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()

$principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)

$isAdministrator = $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdministrator) {

    Write-Host "Administrator privileges are required."
    Write-Host "Requesting elevation..."

    $scriptPath = $MyInvocation.MyCommand.Path

    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""

    try {
        Start-Process `
            -FilePath 'powershell.exe' `
            -ArgumentList $arguments `
            -Verb RunAs

        exit
    }
    catch {
        Write-Error "Administrator privileges were not granted."
        exit 1
    }
}

# ============================================================
# Installation
# ============================================================

try {

    Write-Host ""
    Write-Host "============================================"
    Write-Host "        Clean Print Printer Installer"
    Write-Host "============================================"
    Write-Host ""

    # --------------------------------------------------------
    # Create capture directory
    # --------------------------------------------------------

    Write-Host "Creating capture directory..."

    New-Item `
        -ItemType Directory `
        -Force `
        -Path $CaptureDirectory | Out-Null

    Write-Host "Capture directory:"
    Write-Host "  $CaptureDirectory"

    # --------------------------------------------------------
    # Check whether Clean Print printer already exists
    # --------------------------------------------------------

    $existingPrinter = Get-Printer `
        -Name $PrinterName `
        -ErrorAction SilentlyContinue

    if ($existingPrinter) {

        Write-Host ""
        Write-Host "Clean Print printer already exists."

        Write-Host "Printer:"
        Write-Host "  $PrinterName"

        Write-Host ""
        Write-Host "Installation completed."

        exit 0
    }

    # --------------------------------------------------------
    # Find Microsoft XPS Document Writer driver
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "Searching for Microsoft XPS Document Writer driver..."

    $driver = $null

    $xpsPrinter = Get-Printer `
        -Name 'Microsoft XPS Document Writer' `
        -ErrorAction SilentlyContinue

    if ($xpsPrinter) {
        $driver = $xpsPrinter.DriverName
    }

    # Fallback: search installed printer drivers
    if (-not $driver) {

        $driver = Get-PrinterDriver |
            Where-Object {
                $_.Name -like '*XPS*Document*Writer*'
            } |
            Select-Object -First 1 -ExpandProperty Name
    }

    if (-not $driver) {

        throw @"
Microsoft XPS Document Writer driver could not be found.

Please make sure the Microsoft XPS Document Writer printer
is installed in Windows before installing Clean Print.
"@
    }

    Write-Host "XPS driver found:"
    Write-Host "  $driver"

    # --------------------------------------------------------
    # Create printer port
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "Creating Clean Print printer port..."

    $existingPort = Get-PrinterPort `
        -Name $CaptureFile `
        -ErrorAction SilentlyContinue

    if (-not $existingPort) {

        Add-PrinterPort `
            -Name $CaptureFile

        Write-Host "Printer port created:"
        Write-Host "  $CaptureFile"
    }
    else {

        Write-Host "Printer port already exists."
    }

    # --------------------------------------------------------
    # Create Clean Print printer
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "Creating Clean Print printer..."

    Add-Printer `
        -Name $PrinterName `
        -DriverName $driver `
        -PortName $CaptureFile

    Write-Host ""
    Write-Host "============================================"
    Write-Host "       Clean Print installation complete"
    Write-Host "============================================"
    Write-Host ""

    Write-Host "Printer:"
    Write-Host "  $PrinterName"

    Write-Host ""
    Write-Host "Driver:"
    Write-Host "  $driver"

    Write-Host ""
    Write-Host "Capture file:"
    Write-Host "  $CaptureFile"

    Write-Host ""

}
catch {

    Write-Host ""
    Write-Host "============================================"
    Write-Host "       Clean Print installation FAILED"
    Write-Host "============================================"
    Write-Host ""

    Write-Host "Error:"
    Write-Host $_.Exception.Message -ForegroundColor Red

    Write-Host ""
    Write-Host "Press Enter to close..."
    Read-Host

    exit 1
}

# Keep the window open when launched by double-click
Write-Host "Press Enter to close..."
Read-Host