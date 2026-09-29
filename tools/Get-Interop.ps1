# Get-Interop.ps1
# Finds Interop.AlphaCAMMill.dll in your ALPHACAM installation and copies it
# to src\AlphacamBridge.Shim\PrivateReferences\ so the solution can build.
#
# The interop assembly is Hexagon's, generated from their type library, so it
# is not distributed with this repository - it comes from YOUR licensed
# ALPHACAM installation. (Despite the "Mill" name it is the canonical interop
# for Mill, Router, and the other modules.)
#
# Usage:
#   .\tools\Get-Interop.ps1                  # search ALPHACAM installs
#   .\tools\Get-Interop.ps1 -SourceDll "C:\somewhere\Interop.AlphaCAMMill.dll"

param([string]$SourceDll)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$destDir  = Join-Path $repoRoot 'src\AlphacamBridge.Shim\PrivateReferences'
$destDll  = Join-Path $destDir 'Interop.AlphaCAMMill.dll'

if (Test-Path $destDll) {
    Write-Host "Already present: $destDll" -ForegroundColor Green
    exit 0
}

if (-not $SourceDll) {
    Write-Host "Searching for Interop.AlphaCAMMill.dll in ALPHACAM installations..." -ForegroundColor Cyan
    $roots = @(
        'C:\Program Files\Hexagon',
        'C:\Program Files (x86)\Hexagon',
        'C:\Program Files\Vero Software',
        'C:\Program Files (x86)\Vero Software'
    ) | Where-Object { Test-Path $_ }

    $candidates = foreach ($root in $roots) {
        Get-ChildItem $root -Recurse -Filter 'Interop.AlphaCAMMill.dll' -ErrorAction SilentlyContinue
    }
    if ($candidates) {
        # Prefer the newest file (newest ALPHACAM version)
        $SourceDll = ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    }
}

if (-not $SourceDll -or -not (Test-Path $SourceDll)) {
    Write-Host ""
    Write-Host "Could not find Interop.AlphaCAMMill.dll automatically." -ForegroundColor Red
    Write-Host "Options:" -ForegroundColor Yellow
    Write-Host "  1. Search your ALPHACAM install folder for Interop.AlphaCAMMill.dll and re-run:"
    Write-Host "       .\tools\Get-Interop.ps1 -SourceDll `"C:\path\to\Interop.AlphaCAMMill.dll`""
    Write-Host "  2. Or generate it from ALPHACAM's type library with tlbimp (Windows SDK):"
    Write-Host "       tlbimp `"C:\...\Acam.exe`" /out:Interop.AlphaCAMMill.dll /namespace:AlphaCAMMill"
    Write-Host "  Then place it at: $destDll"
    exit 1
}

if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
Copy-Item $SourceDll $destDll -Force
$ver = (Get-Item $destDll).VersionInfo.FileVersion
Write-Host "Copied $SourceDll" -ForegroundColor Green
Write-Host "    to $destDll (version $ver)" -ForegroundColor Green
