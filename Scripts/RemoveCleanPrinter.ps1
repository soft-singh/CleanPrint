#Requires -Version 5.1

<#
.SYNOPSIS
    Removes the Clean Print printer and its printer port.

.DESCRIPTION
    Automatically requests Administrator privileges when required.

    Removes:
      - Clean Print printer
      - Clean Print printer port
      - Clean Print capture directory (optional)

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
# Uninstallation
# ============================================================

try {

    Write-Host ""
    Write-Host "============================================"
    Write-Host "       Clean Print Printer Uninstaller"
    Write-Host "============================================"
    Write-Host ""

    # --------------------------------------------------------
    # Remove printer
    # --------------------------------------------------------

    Write-Host "Checking for Clean Print printer..."

    $printer = Get-Printer `
        -Name $PrinterName `
        -ErrorAction SilentlyContinue

    if ($printer) {

        Write-Host "Removing printer:"
        Write-Host "  $PrinterName"

        Remove-Printer `
            -Name $PrinterName `
            -Confirm:$false

        Write-Host "Printer removed successfully."
    }
    else {

        Write-Host "Clean Print printer is not installed."
    }

    # --------------------------------------------------------
    # Remove printer port
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "Checking for Clean Print printer port..."

    $port = Get-PrinterPort `
        -Name $CaptureFile `
        -ErrorAction SilentlyContinue

    if ($port) {

        Write-Host "Removing printer port:"
        Write-Host "  $CaptureFile"

        Remove-PrinterPort `
            -Name $CaptureFile `
            -Confirm:$false

        Write-Host "Printer port removed successfully."
    }
    else {

        Write-Host "Clean Print printer port does not exist."
    }

    # --------------------------------------------------------
    # Remove capture directory
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "Checking capture directory..."

    if (Test-Path $CaptureDirectory) {

        $files = Get-ChildItem `
            -Path $CaptureDirectory `
            -Force `
            -ErrorAction SilentlyContinue

        if (-not $files) {

            Remove-Item `
                -Path $CaptureDirectory `
                -Force `
                -ErrorAction SilentlyContinue

            Write-Host "Empty capture directory removed."
        }
        else {

            Write-Host "Capture directory contains files."
            Write-Host "It has NOT been removed."
            Write-Host ""
            Write-Host "Directory:"
            Write-Host "  $CaptureDirectory"
        }
    }

    # --------------------------------------------------------
    # Complete
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "============================================"
    Write-Host "     Clean Print removal complete"
    Write-Host "============================================"
    Write-Host ""

}
catch {

    Write-Host ""
    Write-Host "============================================"
    Write-Host "     Clean Print removal FAILED"
    Write-Host "============================================"
    Write-Host ""

    Write-Host "Error:"
    Write-Host $_.Exception.Message -ForegroundColor Red

    Write-Host ""
    Write-Host "Press Enter to close..."
    Read-Host

    exit 1
}

Write-Host "Press Enter to close..."
Read-Host