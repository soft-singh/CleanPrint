$ErrorActionPreference = "Stop"

# Keeps the window open at the end so the output can be read when the script is launched by
# double-click / "Run with PowerShell" (the window would otherwise close the instant it finishes,
# taking any error message with it). Runs on success AND failure - see the try/catch/finally below.
function Wait-ForKeyPress {
    Write-Host ""
    Write-Host "Press any key to exit ..." -NoNewline
    try {
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    }
    catch {
        # ReadKey is not supported everywhere (e.g. PowerShell ISE). Fall back to Enter where a
        # prompt is possible; if input is redirected (CI, piped) there is nobody to wait for, so
        # just carry on instead of hanging or failing.
        try { $null = Read-Host } catch { }
    }
    Write-Host ""
}

$exitCode = 0

try {
    # Publish.ps1 is inside the "Scripts" directory.
    # Therefore the solution/project root is one level above it.
    $root = Split-Path $PSScriptRoot -Parent
    $publish = Join-Path $root "Publish"

    Write-Host "Project root: $root"
    Write-Host "Publish directory: $publish"
    Write-Host ""

    # Clean previous publication
    if (Test-Path $publish) {
        Write-Host "Cleaning previous publication..."
        Remove-Item $publish -Recurse -Force
    }

    New-Item -ItemType Directory -Path $publish -Force | Out-Null

    # Publish WPF application
    Write-Host "Publishing CleanPrint.App..."

    dotnet publish "$root\CleanPrint.App\CleanPrint.App.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -o $publish

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish CleanPrint.App."
    }

    # Publish watcher
    Write-Host "Publishing CleanPrint.Watcher..."

    dotnet publish "$root\CleanPrint.Watcher\CleanPrint.Watcher.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -o $publish

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish CleanPrint.Watcher."
    }

    # Copy Clean Printer PowerShell scripts
    Write-Host "Copying printer scripts..."

    $addPrinterScript = Join-Path $PSScriptRoot "AddCleanPrinter.ps1"
    $removePrinterScript = Join-Path $PSScriptRoot "RemoveCleanPrinter.ps1"

    if (-not (Test-Path $addPrinterScript)) {
        throw "Required file not found: $addPrinterScript"
    }

    if (-not (Test-Path $removePrinterScript)) {
        throw "Required file not found: $removePrinterScript"
    }

    Copy-Item $addPrinterScript $publish -Force
    Copy-Item $removePrinterScript $publish -Force

    Write-Host "Copied:"
    Write-Host "  AddCleanPrinter.ps1"
    Write-Host "  RemoveCleanPrinter.ps1"

    Write-Host ""
    Write-Host "========================================="
    Write-Host "Publication completed successfully."
    Write-Host "Output: $publish"
    Write-Host "========================================="
}
catch {
    Write-Host ""
    Write-Host "=========================================" -ForegroundColor Red
    Write-Host "Publication FAILED." -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "=========================================" -ForegroundColor Red
    $exitCode = 1
}
finally {
    Wait-ForKeyPress
}

exit $exitCode
