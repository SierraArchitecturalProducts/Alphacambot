# Deploy-Shim.ps1 - deploys the CambotAlphacam add-in and the Launcher.
# REQUIRES ADMIN (the add-in goes under Program Files). Close ALPHACAM first.
#
# Strategy: copy the canonical manifest in deploy\ byte-for-byte. The
# .acamaddin manifest is extremely picky (plain ASCII, CRLF, exact field
# order); never regenerate it by hand. $2 = 1 in the manifest means the
# add-in loads automatically on ALPHACAM startup - it does NOT appear in
# the Utils > Add-Ins dialog, which is normal.
#
#   .\Deploy-Shim.ps1                                  # auto-detect ALPHACAM
#   .\Deploy-Shim.ps1 -AlphacamDir "C:\Program Files\Hexagon\ALPHACAM 2025"

param([string]$AlphacamDir)

$ErrorActionPreference = 'Stop'

# ── Locate ALPHACAM ────────────────────────────────────────────────
if (-not $AlphacamDir) {
    $roots = @('C:\Program Files\Hexagon', 'C:\Program Files (x86)\Hexagon',
               'C:\Program Files\Vero Software') | Where-Object { Test-Path $_ }
    $installs = foreach ($root in $roots) {
        Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'DotNetAdd-Ins') }
    }
    if (-not $installs) {
        Write-Host "No ALPHACAM installation with a DotNetAdd-Ins folder found." -ForegroundColor Red
        Write-Host "Re-run with -AlphacamDir `"C:\path\to\your\ALPHACAM`"" -ForegroundColor Yellow
        exit 1
    }
    $AlphacamDir = ($installs | Sort-Object Name -Descending | Select-Object -First 1).FullName
}
Write-Host "ALPHACAM: $AlphacamDir" -ForegroundColor Cyan

$dest      = Join-Path $AlphacamDir 'DotNetAdd-Ins\AlphacamBridge'
$buildDll  = Join-Path $PSScriptRoot 'src\AlphacamBridge.Shim\bin\Debug\net48\CambotAlphacam.dll'
$buildPdb  = Join-Path $PSScriptRoot 'src\AlphacamBridge.Shim\bin\Debug\net48\CambotAlphacam.pdb'
$stageDll  = Join-Path $PSScriptRoot 'deploy\CambotAlphacam.dll'
$stagePdb  = Join-Path $PSScriptRoot 'deploy\CambotAlphacam.pdb'
# Canonical manifest checked into deploy\ - the source of truth.
$stageAddin = Join-Path $PSScriptRoot 'deploy\CambotAlphacam.acamaddin'

# Auto-stage: if the build output is newer than the deploy\ staging copy, refresh it.
# This prevents the "rebuilt but old DLL deployed" footgun.
if (Test-Path $buildDll) {
    $stageNeeded = (-not (Test-Path $stageDll)) -or ((Get-Item $buildDll).LastWriteTime -gt (Get-Item $stageDll).LastWriteTime)
    if ($stageNeeded) {
        Write-Host "Staging fresh build output to deploy\..." -ForegroundColor Cyan
        Copy-Item $buildDll $stageDll -Force
        if (Test-Path $buildPdb) { Copy-Item $buildPdb $stagePdb -Force }
    }
} else {
    Write-Host "No build output at $buildDll" -ForegroundColor Red
    Write-Host "Build first:  dotnet build src\AlphacamBridge.Shim\AlphacamBridge.Shim.csproj -c Debug" -ForegroundColor Yellow
    if (-not (Test-Path $stageDll)) { exit 1 }
    Write-Host "Using existing deploy\ staging copy." -ForegroundColor Yellow
}

$srcDll = $stageDll
$srcPdb = $stagePdb

foreach ($f in @($srcDll, $stageAddin)) {
    if (-not (Test-Path $f)) { Write-Host "MISSING: $f" -ForegroundColor Red; exit 1 }
}

Write-Host "Looking for processes with ALPHACAM DLLs loaded..." -ForegroundColor Cyan
$blockers = Get-Process | Where-Object {
    try { $_.Modules | Where-Object { $_.FileName -like (Join-Path $AlphacamDir '*') } } catch {}
} | Select-Object -Unique
if ($blockers) {
    Write-Host "These processes are holding ALPHACAM DLLs. Close them first." -ForegroundColor Red
    $blockers | Format-Table Name, Id, Path
    exit 2
}
Get-Process -Name 'AlphacamBridge.Launcher' -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }

# Manifest: byte-for-byte copy of the canonical deploy\ manifest.
$manifestDest = Join-Path $dest 'CambotAlphacam.acamaddin'
$manifestBytes = [System.IO.File]::ReadAllBytes($stageAddin)
[System.IO.File]::WriteAllBytes($manifestDest, $manifestBytes)
Write-Host ("Wrote manifest (" + $manifestBytes.Length + " bytes) to " + $manifestDest) -ForegroundColor Green

# DLL and PDB
Copy-Item $srcDll (Join-Path $dest 'CambotAlphacam.dll') -Force
if (Test-Path $srcPdb) { Copy-Item $srcPdb (Join-Path $dest 'CambotAlphacam.pdb') -Force }

$srcLen = (Get-Item $srcDll).Length
$dstLen = (Get-Item (Join-Path $dest 'CambotAlphacam.dll')).Length
if ($srcLen -ne $dstLen) {
    Write-Host ("FAIL: deployed DLL is {0} bytes, source is {1}." -f $dstLen, $srcLen) -ForegroundColor Red
    exit 3
}

# ── Launcher deploy ────────────────────────────────────────────────
# The Launcher (.NET 8) lives in LOCALAPPDATA (no admin needed) and must be
# kept in step with the shim - the process-lifecycle logic spans BOTH sides.
$launcherBuild = Join-Path $PSScriptRoot 'src\AlphacamBridge.Launcher\bin\Debug\net8.0-windows'
$launcherDest  = Join-Path $env:LOCALAPPDATA 'Programs\AlphacamBridge'
if (Test-Path (Join-Path $launcherBuild 'AlphacamBridge.Launcher.exe')) {
    Write-Host ""
    Write-Host "Deploying Launcher from build output..." -ForegroundColor Cyan
    if (-not (Test-Path $launcherDest)) { New-Item -ItemType Directory -Path $launcherDest | Out-Null }
    Copy-Item (Join-Path $launcherBuild '*') $launcherDest -Recurse -Force
    $exeTime = (Get-Item (Join-Path $launcherDest 'AlphacamBridge.dll')).LastWriteTime
    Write-Host ("Launcher deployed (AlphacamBridge.dll " + $exeTime + ")") -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "WARN: no Launcher build output at $launcherBuild - Launcher NOT deployed." -ForegroundColor Yellow
    Write-Host "      Build it first: dotnet build src\AlphacamBridge.Launcher\AlphacamBridge.Launcher.csproj -c Debug" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Deployed files:" -ForegroundColor Cyan
Get-ChildItem $dest | Format-Table Name, Length, LastWriteTime

Write-Host ""
Write-Host "Deploy OK. Now:" -ForegroundColor Green
Write-Host "  1. Start ALPHACAM. The add-in loads automatically" -ForegroundColor Gray
Write-Host "     (it does not appear in Utils > Add-Ins - that is normal)." -ForegroundColor Gray
Write-Host "  2. Restart Claude Desktop." -ForegroundColor Gray
Write-Host "  3. Check the log:" -ForegroundColor Gray
Write-Host "       Get-Content -Tail 20 `$env:LOCALAPPDATA\AlphacamBridge\shim.log" -ForegroundColor Gray
Write-Host "  4. Ask Claude: `"give me alphacam status`"" -ForegroundColor Gray
