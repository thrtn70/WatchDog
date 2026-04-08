<#
.SYNOPSIS
    Downloads and extracts FFmpeg + FFprobe binaries for TikrClipr.

.DESCRIPTION
    Downloads a static FFmpeg build from gyan.dev, extracts ffmpeg.exe and
    ffprobe.exe, and places them in the ffmpeg-runtime directory. These files
    are copied to the output directory at build time.

.PARAMETER FfmpegVersion
    FFmpeg release version. Default: 7.1

.EXAMPLE
    .\setup-ffmpeg.ps1
    .\setup-ffmpeg.ps1 -FfmpegVersion 7.1
#>

param(
    [string]$FfmpegVersion = "7.1"
)

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $ProjectRoot "TikrClipr.sln"))) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

$FfmpegDir = Join-Path $ProjectRoot "ffmpeg-runtime"
$TempDir = Join-Path $ProjectRoot ".ffmpeg-temp"
$ZipUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-$FfmpegVersion-essentials_build.zip"
$ZipPath = Join-Path $TempDir "ffmpeg.zip"

Write-Host "TikrClipr FFmpeg Setup" -ForegroundColor Cyan
Write-Host "=====================" -ForegroundColor Cyan
Write-Host "FFmpeg Version: $FfmpegVersion"
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
    Write-Host "Failed to download FFmpeg. URL: $ZipUrl" -ForegroundColor Red
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "You can manually download FFmpeg from: https://www.gyan.dev/ffmpeg/builds/" -ForegroundColor Yellow
    Write-Host "Extract ffmpeg.exe and ffprobe.exe to: $FfmpegDir" -ForegroundColor Yellow
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
