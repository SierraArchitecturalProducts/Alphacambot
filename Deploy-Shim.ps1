# Deploy fresh CambotAlphacam shim. REQUIRES ADMIN.
# Strategy: copy the working sample manifest byte-for-byte, change ONLY the
# assembly-name line. Keep $2=0 (sample default) so the addin appears in
# Utils > Add-Ins for the user to enable.

$ErrorActionPreference = 'Stop'

$dest      = 'C:\Program Files\Hexagon\ALPHACAM 2025\DotNetAdd-Ins\AlphacamBridge'
$buildDll  = Join-Path $PSScriptRoot 'AlphacamBridge\src\AlphacamBridge.Shim\bin\Debug\net48\CambotAlphacam.dll'
$buildPdb  = Join-Path $PSScriptRoot 'AlphacamBridge\src\AlphacamBridge.Shim\bin\Debug\net48\CambotAlphacam.pdb'
$stageDll  = Join-Path $PSScriptRoot 'deploy\CambotAlphacam.dll'
$stagePdb  = Join-Path $PSScriptRoot 'deploy\CambotAlphacam.pdb'
# Canonical manifest checked into deploy\. Plain ASCII, CRLF, $2 = 0.
# History: previously sourced from a build of AlphacamAddinSample and
# string-replaced — that template was fragile and easily missing. Treat
# the deploy\ copy as the source of truth now.
$stageAddin = Join-Path $PSScriptRoot 'deploy\CambotAlphacam.acamaddin'

# Auto-stage: if the build output is newer than the deploy\ staging copy, refresh it.
# This prevents the "rebuild but old DLL deployed" footgun.
if (Test-Path $buildDll) {
    $stageNeeded = (-not (Test-Path $stageDll)) -or ((Get-Item $buildDll).LastWriteTime -gt (Get-Item $stageDll).LastWriteTime)
    if ($stageNeeded) {
        Write-Host "Staging fresh build output to deploy\..." -ForegroundColor Cyan
        Copy-Item $buildDll $stageDll -Force
        if (Test-Path $buildPdb) { Copy-Item $buildPdb $stagePdb -Force }
    }
} else {
    Write-Host "WARN: no build output at $buildDll; using existing deploy\ staging copy." -ForegroundColor Yellow
}

$srcDll = $stageDll
$srcPdb = $stagePdb

foreach ($f in @($srcDll, $srcPdb, $stageAddin)) {
    if (-not (Test-Path $f)) { Write-Host "MISSING: $f" -ForegroundColor Red; exit 1 }
}

Write-Host "Looking for processes with Alphacam DLLs loaded..." -ForegroundColor Cyan
$blockers = Get-Process | Where-Object {
    try { $_.Modules | Where-Object { $_.FileName -like 'C:\Program Files\Hexagon\ALPHACAM*' } } catch {}
} | Select-Object -Unique
if ($blockers) {
    Write-Host "These processes are holding Alphacam DLLs. Close them first." -ForegroundColor Red
    $blockers | Format-Table Name, Id, Path
    exit 2
}
Get-Process -Name 'AlphacamBridge.Launcher' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host ""
Write-Host "Before deploy:" -ForegroundColor Cyan
Get-ChildItem $dest | Format-Table Name, Length, LastWriteTime

# Manifest: byte-for-byte copy of the canonical deploy\ manifest.
$manifestDest = Join-Path $dest 'CambotAlphacam.acamaddin'
$manifestBytes = [System.IO.File]::ReadAllBytes($stageAddin)
[System.IO.File]::WriteAllBytes($manifestDest, $manifestBytes)
Write-Host ("Wrote manifest (" + $manifestBytes.Length + " bytes) to " + $manifestDest) -ForegroundColor Green

# DLL and PDB
Copy-Item $srcDll (Join-Path $dest 'CambotAlphacam.dll') -Force
Copy-Item $srcPdb (Join-Path $dest 'CambotAlphacam.pdb') -Force

$srcLen = (Get-Item $srcDll).Length
$dstLen = (Get-Item (Join-Path $dest 'CambotAlphacam.dll')).Length
if ($srcLen -ne $dstLen) {
    Write-Host ("FAIL: deployed DLL is {0} bytes, source is {1}." -f $dstLen, $srcLen) -ForegroundColor Red
    exit 3
}

# Remove stale legacy DLL if present
$stale = Join-Path $dest 'AlphacamBridge.Shim.dll'
if (Test-Path $stale) { Remove-Item $stale -Force; Write-Host "Removed stale: $stale" -ForegroundColor Yellow }

# ---- Launcher deploy (added 2026-07-20) ----
# The Launcher (.NET 8) lives in LOCALAPPDATA (no admin needed) and must be
# kept in step with the shim - the ghost-Acam fix spans BOTH sides.
$launcherBuild = Join-Path $PSScriptRoot 'AlphacamBridge\src\AlphacamBridge.Launcher\bin\Debug\net8.0-windows'
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
    Write-Host "      Build it first: dotnet build AlphacamBridge\src\AlphacamBridge.Launcher\AlphacamBridge.Launcher.csproj -c Debug" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "After deploy:" -ForegroundColor Cyan
Get-ChildItem $dest | Format-Table Name, Length, LastWriteTime

Write-Host ""
Write-Host "Manifest hex of last 32 bytes (verify proper CRLF, no trailing garbage):" -ForegroundColor Cyan
$mfBytes = [System.IO.File]::ReadAllBytes($manifestDest)
$tail = $mfBytes[($mfBytes.Length - 32)..($mfBytes.Length - 1)]
($tail | ForEach-Object { '{0:x2}' -f $_ }) -join ' '

Write-Host ""
Write-Host "Deploy OK. Now:" -ForegroundColor Green
Write-Host "  1. Start Alphacam." -ForegroundColor Gray
Write-Host "  2. Open Utils > Add-Ins. In the dialog, check the box next to 'CambotAlphacam'." -ForegroundColor Gray
Write-Host "  3. Click OK. Restart Alphacam if prompted." -ForegroundColor Gray
Write-Host "  4. After restart, check the log:" -ForegroundColor Gray
Write-Host "       Get-Content -Tail 20 `$env:LOCALAPPDATA\AlphacamBridge\shim.log" -ForegroundColor Gray
