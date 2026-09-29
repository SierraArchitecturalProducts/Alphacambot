# Install.ps1
# Installs the Alphacam MCP server into Claude Desktop's configuration.
# Run from the alphacam-mcp-server directory.

$ErrorActionPreference = "Stop"

Write-Host "=== Alphacam MCP Server Installer ===" -ForegroundColor Cyan

# 1. Check Node.js
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    Write-Host "Node.js not found. Please install Node.js 18+ from https://nodejs.org" -ForegroundColor Red
    exit 1
}
$nodeVersion = (node --version)
Write-Host "Node.js: $nodeVersion" -ForegroundColor Green

# 2. Install npm dependencies
Write-Host "Installing dependencies..." -ForegroundColor Cyan
npm install --production
if ($LASTEXITCODE -ne 0) {
    Write-Host "npm install failed" -ForegroundColor Red
    exit 1
}
Write-Host "Dependencies installed" -ForegroundColor Green

# 3. Find Claude Desktop config
$claudeConfigDir = Join-Path $env:APPDATA "Claude"
$claudeConfigPath = Join-Path $claudeConfigDir "claude_desktop_config.json"

if (-not (Test-Path $claudeConfigDir)) {
    New-Item -ItemType Directory -Path $claudeConfigDir -Force | Out-Null
}

# 4. Build the MCP server entry
$serverPath = (Resolve-Path "src\index.js").Path -replace '\\', '\\'

$mcpEntry = @{
    command = "node"
    args = @($serverPath, "--pipe", "AlphacamBridge", "--key", "anonymous")
}

# 5. Merge into existing config
if (Test-Path $claudeConfigPath) {
    $config = Get-Content $claudeConfigPath -Raw | ConvertFrom-Json
    Write-Host "Found existing Claude Desktop config" -ForegroundColor Green
} else {
    $config = @{}
    Write-Host "Creating new Claude Desktop config" -ForegroundColor Yellow
}

if (-not $config.mcpServers) {
    $config | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue @{}
}

# Add or update the alphacam entry
$config.mcpServers | Add-Member -NotePropertyName "alphacam" -NotePropertyValue $mcpEntry -Force

$config | ConvertTo-Json -Depth 10 | Set-Content $claudeConfigPath -Encoding UTF8

Write-Host ""
Write-Host "Installed to: $claudeConfigPath" -ForegroundColor Green
Write-Host "Server path:  $serverPath" -ForegroundColor Green
Write-Host ""
Write-Host "Restart Claude Desktop to activate the Alphacam tools." -ForegroundColor Yellow
Write-Host "Make sure AlphacamBridge (launcher or Alphacam add-in) is running." -ForegroundColor Yellow
