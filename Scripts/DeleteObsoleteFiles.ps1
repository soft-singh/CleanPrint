<#
.SYNOPSIS
    Deletes source files that were removed from CleanPrint in earlier versions.
    Needed because unzipping a new version over an old tree never deletes anything.

.NOTES
    Run from the Scripts folder (or anywhere): the project root is the parent of this folder.
    Add one line per removed file to $Obsolete (path relative to the project root) with the
    version that removed it. Every release ships this script with its list updated.
    Use -WhatIf to preview.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not (Test-Path (Join-Path $root 'CleanPrint.Core'))) {
    throw "Not a CleanPrint project root: $root"
}

# ---- Files removed so far (path relative to project root) ----
$Obsolete = @(
    'CleanPrint.App\Themes\DarkTheme.xaml'          # removed in 0.21.0 (themes are generated now)
    'CleanPrint.App\ViewModels\IconColorOption.cs'  # removed in 0.22.0 (icon colour selector gone)
)

$removed = 0
foreach ($rel in $Obsolete) {
    $path = Join-Path $root $rel
    if (Test-Path -LiteralPath $path) {
        if ($PSCmdlet.ShouldProcess($path, 'Delete')) {
            Remove-Item -LiteralPath $path -Force
            Write-Host "Deleted: $rel"
            $removed++
        }
    }
}

Write-Host ""
Write-Host "Obsolete files deleted: $removed (listed: $($Obsolete.Count))"
