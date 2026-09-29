# Setup.ps1
# One-command bootstrap for a fresh clone of Alphacambot.
#
#   1. Checks prerequisites (Node.js, .NET SDK)
#   2. Copies Interop.AlphaCAMMill.dll from your ALPHACAM install (tools\Get-Interop.ps1)
#   3. Builds the shim (.NET 4.8) and the Launcher (.NET 8)
#   4. Installs the MCP server (npm install + Claude Desktop config entry)
#   5. Builds the optional API-help index from your own ACAMAPI.chm
#   6. Deploys the add-in and Launcher (Deploy-Shim.ps1; needs an elevated shell)
#
# Run from the repository root. For step 6 to run, start PowerShell
# "as Administrator"; otherwise Setup does everything else and tells you
# what is left.
#
#   .\Setup.ps1                    # everything
#   .\Setup.ps1 -SkipDeploy        # build + configure only
#   .\Setup.ps1 -SkipApiReference  # skip the ACAMAPI.chm parsing step

param(
    [switch]$SkipDeploy,
    [switch]$SkipApiReference
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
Set-Location $repoRoot

function Step($n, $msg) { Write-Host ""; Write-Host "[$n] $msg" -ForegroundColor Cyan }

# ── 1. Prerequisites ───────────────────────────────────────────────
Step 1 "Checking prerequisites"
$missing = @()
if (-not (Get-Command node   -ErrorAction SilentlyContinue)) { $missing += "Node.js LTS       https://nodejs.org" }
if (-not (Get-Command npm    -ErrorAction SilentlyContinue)) { $missing += "npm (comes with Node.js)" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $missing += ".NET SDK 8+       https://dotnet.microsoft.com/download" }
if ($missing) {
    Write-Host "Missing prerequisites:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host ("  node " + (node --version) + ", dotnet " + (dotnet --version)) -ForegroundColor Green

# ── 2. Interop assembly ────────────────────────────────────────────
Step 2 "Getting Interop.AlphaCAMMill.dll from your ALPHACAM installation"
& (Join-Path $repoRoot 'tools\Get-Interop.ps1')
if ($LASTEXITCODE -ne 0) {
    Write-Host "Cannot build without the interop assembly. Fix the above, then re-run Setup.ps1." -ForegroundColor Red
    exit 1
}

# ── 3. Build ───────────────────────────────────────────────────────
Step 3 "Building the ALPHACAM add-in (shim, .NET Framework 4.8)"
dotnet build (Join-Path $repoRoot 'src\AlphacamBridge.Shim\AlphacamBridge.Shim.csproj') -c Debug
if ($LASTEXITCODE -ne 0) { Write-Host "Shim build failed." -ForegroundColor Red; exit 1 }

Step 3 "Building the Launcher (.NET 8)"
dotnet build (Join-Path $repoRoot 'src\AlphacamBridge.Launcher\AlphacamBridge.Launcher.csproj') -c Debug
if ($LASTEXITCODE -ne 0) { Write-Host "Launcher build failed." -ForegroundColor Red; exit 1 }

# ── 4. MCP server + Claude Desktop config ──────────────────────────
Step 4 "Installing the MCP server and registering it with Claude Desktop"
Push-Location (Join-Path $repoRoot 'src\alphacam-mcp-server')
try {
    & .\Install.ps1
    if ($LASTEXITCODE -ne 0) { throw "Install.ps1 failed" }
} finally { Pop-Location }

# ── 5. Optional: API help index from your own ACAMAPI.chm ──────────
if (-not $SkipApiReference) {
    Step 5 "Building the API-help index from your ACAMAPI.chm (optional)"
    $apiJson = Join-Path $repoRoot 'src\alphacam-mcp-server\data\acamapi-reference.json'
    if (Test-Path $apiJson) {
        Write-Host "  Already present: $apiJson" -ForegroundColor Green
    } else {
        node (Join-Path $repoRoot 'tools\generate-api-reference.js')
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  Could not build the API index (ACAMAPI.chm not found?)." -ForegroundColor Yellow
            Write-Host "  Everything else works without it; only the alphacam_api_help tool is affected." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host ""; Write-Host "[5] Skipping API-help index (-SkipApiReference)" -ForegroundColor DarkGray
}

# ── 6. Deploy ──────────────────────────────────────────────────────
$deployDone = $false
if ($SkipDeploy) {
    Write-Host ""; Write-Host "[6] Skipping deploy (-SkipDeploy)" -ForegroundColor DarkGray
} else {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        Step 6 "Deploying the add-in and Launcher (Deploy-Shim.ps1)"
        & (Join-Path $repoRoot 'Deploy-Shim.ps1')
        $deployDone = ($LASTEXITCODE -eq 0 -or $null -eq $LASTEXITCODE)
    } else {
        Write-Host ""
        Write-Host "[6] Not elevated - deploy step skipped." -ForegroundColor Yellow
        Write-Host "    Close ALPHACAM, open PowerShell as Administrator, and run:" -ForegroundColor Yellow
        Write-Host "      cd `"$repoRoot`"; .\Deploy-Shim.ps1" -ForegroundColor Yellow
    }
}

# ── Summary ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
if (-not $deployDone) {
    Write-Host "Remaining: run Deploy-Shim.ps1 from an elevated PowerShell (with ALPHACAM closed)." -ForegroundColor Yellow
}
Write-Host "Then: start ALPHACAM (the add-in loads automatically), restart Claude Desktop," -ForegroundColor Gray
Write-Host "and ask Claude: `"give me alphacam status`"." -ForegroundColor Gray
