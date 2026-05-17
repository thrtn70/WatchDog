<#
.SYNOPSIS
    Downloads and extracts FFmpeg + FFprobe binaries for WatchDog.

.DESCRIPTION
    Downloads the latest FFmpeg release essentials build from gyan.dev,
    extracts ffmpeg.exe and ffprobe.exe, and places them in the ffmpeg-runtime
    directory. These files are copied to the output directory at build time.

.EXAMPLE
    .\setup-ffmpeg.ps1
#>

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $ProjectRoot "WatchDog.slnx"))) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

$FfmpegDir = Join-Path $ProjectRoot "ffmpeg-runtime"
$TempDir = Join-Path $ProjectRoot ".ffmpeg-temp"
$ZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$ZipPath = Join-Path $TempDir "ffmpeg.zip"

Write-Host "WatchDog FFmpeg Setup" -ForegroundColor Cyan
Write-Host "=====================" -ForegroundColor Cyan
Write-Host "Source: $ZipUrl"
Write-Host "Target: $FfmpegDir"
Write-Host ""

# Check if already set up
if ((Test-Path (Join-Path $FfmpegDir "ffmpeg.exe")) -and (Test-Path (Join-Path $FfmpegDir "ffprobe.exe"))) {
    Write-Host "FFmpeg runtime already exists. Delete ffmpeg-runtime/ to re-download." -ForegroundColor Yellow
    exit 0
}

# Create temp directory
if (Test-Path $TempDir) { Remove-Item $TempDir -Recurse -Force }
New-Item -ItemType Directory -Path $TempDir | Out-Null

# Download
Write-Host "Downloading FFmpeg $FfmpegVersion..." -ForegroundColor Green
try {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $ZipUrl -OutFile $ZipPath -UseBasicParsing
} catch {
    Write-Host "Failed to download FFmpeg from: $ZipUrl" -ForegroundColor Red
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "Manual download:" -ForegroundColor Yellow
    Write-Host "  1. Go to: https://www.gyan.dev/ffmpeg/builds/" -ForegroundColor Yellow
    Write-Host "  2. Download 'ffmpeg-release-essentials.zip'" -ForegroundColor Yellow
    Write-Host "  3. Extract ffmpeg.exe and ffprobe.exe from bin/ to: $FfmpegDir" -ForegroundColor Yellow
    exit 1
}

Write-Host "Download complete. Extracting..." -ForegroundColor Green

# Extract
$ExtractDir = Join-Path $TempDir "extracted"
Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir -Force

# Find the bin directory (zip has a versioned folder inside)
$BinDir = Get-ChildItem -Path $ExtractDir -Recurse -Directory -Filter "bin" | Select-Object -First 1
if (-not $BinDir) {
    Write-Host "ERROR: Could not find bin/ directory in extracted zip." -ForegroundColor Red
    Get-ChildItem $ExtractDir -Recurse -Depth 2 | ForEach-Object { Write-Host "  $($_.FullName)" }
    exit 1
}

# Create ffmpeg-runtime directory
if (Test-Path $FfmpegDir) { Remove-Item $FfmpegDir -Recurse -Force }
New-Item -ItemType Directory -Path $FfmpegDir | Out-Null

# Copy ffmpeg.exe and ffprobe.exe only (skip ffplay.exe to save space)
foreach ($binary in @("ffmpeg.exe", "ffprobe.exe")) {
    $src = Join-Path $BinDir.FullName $binary
    if (Test-Path $src) {
        Copy-Item $src -Destination $FfmpegDir
        $size = [math]::Round((Get-Item (Join-Path $FfmpegDir $binary)).Length / 1MB, 1)
        Write-Host "  Copied $binary (${size}MB)" -ForegroundColor DarkGray
    } else {
        Write-Host "WARNING: $binary not found in bin/" -ForegroundColor Yellow
    }
}

# Cleanup temp
Remove-Item $TempDir -Recurse -Force

# Verify
$ffmpegExists = Test-Path (Join-Path $FfmpegDir "ffmpeg.exe")
$ffprobeExists = Test-Path (Join-Path $FfmpegDir "ffprobe.exe")

Write-Host ""
if ($ffmpegExists -and $ffprobeExists) {
    Write-Host "FFmpeg setup complete!" -ForegroundColor Green
    Write-Host "  ffmpeg.exe:  OK" -ForegroundColor Green
    Write-Host "  ffprobe.exe: OK" -ForegroundColor Green
} else {
    Write-Host "WARNING: Some binaries may be missing:" -ForegroundColor Yellow
    Write-Host "  ffmpeg.exe:  $(if ($ffmpegExists) { 'OK' } else { 'MISSING' })"
    Write-Host "  ffprobe.exe: $(if ($ffprobeExists) { 'OK' } else { 'MISSING' })"
}
