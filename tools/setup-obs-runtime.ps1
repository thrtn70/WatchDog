<#
.SYNOPSIS
    Downloads and extracts OBS Studio binaries required by ObsKit.NET.

.DESCRIPTION
    Downloads a specific OBS Studio release from GitHub, extracts the required
    binaries (obs.dll, plugins, data files), and places them in the obs-runtime
    directory. These files are needed at build time and copied to the output directory.

.PARAMETER ObsVersion
    OBS Studio version to download. Default: 31.0.1

.EXAMPLE
    .\setup-obs-runtime.ps1
    .\setup-obs-runtime.ps1 -ObsVersion 31.0.1
#>

param(
    [string]$ObsVersion = "31.0.1"
)

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $ProjectRoot "WatchDog.sln"))) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

$ObsRuntimeDir = Join-Path $ProjectRoot "obs-runtime"
$TempDir = Join-Path $ProjectRoot ".obs-temp"
$ZipUrl = "https://github.com/obsproject/obs-studio/releases/download/$ObsVersion/OBS-Studio-$ObsVersion-Windows.zip"
$ZipPath = Join-Path $TempDir "obs-studio.zip"

Write-Host "WatchDog OBS Runtime Setup" -ForegroundColor Cyan
Write-Host "==========================" -ForegroundColor Cyan
Write-Host "OBS Version: $ObsVersion"
Write-Host "Target: $ObsRuntimeDir"
Write-Host ""

# Check if already set up
if (Test-Path (Join-Path $ObsRuntimeDir "obs.dll")) {
    Write-Host "OBS runtime already exists. Delete obs-runtime/ to re-download." -ForegroundColor Yellow
    exit 0
}

# Create temp directory
if (Test-Path $TempDir) { Remove-Item $TempDir -Recurse -Force }
New-Item -ItemType Directory -Path $TempDir | Out-Null

# Download OBS
Write-Host "Downloading OBS Studio $ObsVersion..." -ForegroundColor Green
try {
    $ProgressPreference = 'SilentlyContinue'  # Speed up download
    Invoke-WebRequest -Uri $ZipUrl -OutFile $ZipPath -UseBasicParsing
} catch {
    Write-Host "Failed to download OBS Studio. URL: $ZipUrl" -ForegroundColor Red
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "You can manually download OBS Studio and extract the required files:" -ForegroundColor Yellow
    Write-Host "  1. Download from: https://github.com/obsproject/obs-studio/releases" -ForegroundColor Yellow
    Write-Host "  2. Extract obs.dll, data/, obs-plugins/ to: $ObsRuntimeDir" -ForegroundColor Yellow
    exit 1
}

Write-Host "Download complete. Extracting..." -ForegroundColor Green

# Extract
$ExtractDir = Join-Path $TempDir "extracted"
Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir -Force

# OBS 31+ zip has no wrapper folder: bin/, data/, obs-plugins/ are at the zip root
$BinDir = [System.IO.Path]::Combine($ExtractDir, "bin", "64bit")
$DataSrc = Join-Path $ExtractDir "data"
$PluginSrc = Join-Path $ExtractDir "obs-plugins"

if (-not (Test-Path $BinDir)) {
    Write-Host "ERROR: Could not find bin/64bit in extracted zip. Contents:" -ForegroundColor Red
    Get-ChildItem $ExtractDir | ForEach-Object { Write-Host "  $($_.Name)" }
    exit 1
}

# Create obs-runtime directory (flat — all DLLs at root alongside data/ and obs-plugins/)
if (Test-Path $ObsRuntimeDir) { Remove-Item $ObsRuntimeDir -Recurse -Force }
New-Item -ItemType Directory -Path $ObsRuntimeDir | Out-Null

# Copy all files from bin/64bit flat into obs-runtime/ root
# ObsKit.NET expects obs.dll in the same directory as the app.
# Also copies .exe test helpers required by obs-nvenc, obs-ffmpeg (AMF), and obs-qsv11
# to detect hardware encoder support at runtime.
Write-Host "Copying OBS binaries from bin/64bit/..." -ForegroundColor Green
Get-ChildItem -Path $BinDir -File | ForEach-Object {
    Copy-Item $_.FullName -Destination $ObsRuntimeDir
    Write-Host "  Copied $($_.Name)" -ForegroundColor DarkGray
}

# Copy data directory
if (Test-Path $DataSrc) {
    Copy-Item $DataSrc -Destination $ObsRuntimeDir -Recurse
    Write-Host "  Copied data/" -ForegroundColor DarkGray
} else {
    Write-Host "WARNING: data/ directory not found in zip" -ForegroundColor Yellow
}

# Copy obs-plugins directory
if (Test-Path $PluginSrc) {
    Copy-Item $PluginSrc -Destination $ObsRuntimeDir -Recurse
    Write-Host "  Copied obs-plugins/" -ForegroundColor DarkGray
} else {
    Write-Host "WARNING: obs-plugins/ directory not found in zip" -ForegroundColor Yellow
}

# Cleanup temp
Remove-Item $TempDir -Recurse -Force

# Verify
$obsExists = Test-Path (Join-Path $ObsRuntimeDir "obs.dll")
$dataExists = Test-Path ([System.IO.Path]::Combine($ObsRuntimeDir, "data", "libobs"))
$pluginsExist = Test-Path ([System.IO.Path]::Combine($ObsRuntimeDir, "obs-plugins", "64bit"))

Write-Host ""
if ($obsExists -and $dataExists -and $pluginsExist) {
    Write-Host "OBS runtime setup complete!" -ForegroundColor Green
    Write-Host "  obs.dll: OK" -ForegroundColor Green
    Write-Host "  data/libobs: OK" -ForegroundColor Green
    Write-Host "  obs-plugins/64bit: OK" -ForegroundColor Green
} else {
    Write-Host "WARNING: Some components may be missing:" -ForegroundColor Yellow
    Write-Host "  obs.dll: $(if ($obsExists) { 'OK' } else { 'MISSING' })"
    Write-Host "  data/libobs: $(if ($dataExists) { 'OK' } else { 'MISSING' })"
    Write-Host "  obs-plugins/64bit: $(if ($pluginsExist) { 'OK' } else { 'MISSING' })"
    Write-Host ""
    Write-Host "The OBS release ZIP structure may have changed. Check the extracted contents." -ForegroundColor Yellow
}
