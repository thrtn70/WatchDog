<#
.SYNOPSIS
    Builds and packages WatchDog for distribution.

.DESCRIPTION
    Publishes WatchDog as a self-contained Windows x64 app with OBS runtime
    and creates a ZIP for distribution.

.EXAMPLE
    .\package.ps1
    .\package.ps1 -Configuration Release
#>

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AppProject = Join-Path $ProjectRoot "src" "WatchDog.App" "WatchDog.App.csproj"
$PublishDir = Join-Path $ProjectRoot "publish"
$OutputZip = Join-Path $ProjectRoot "WatchDog-win-x64.zip"

Write-Host "WatchDog Packager" -ForegroundColor Cyan
Write-Host "==================" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host ""

# Clean
if (Test-Path $PublishDir) {
    Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
    Remove-Item $PublishDir -Recurse -Force
}

# Verify OBS runtime exists
$ObsRuntime = Join-Path $ProjectRoot "obs-runtime"
if (-not (Test-Path (Join-Path $ObsRuntime "obs.dll"))) {
    Write-Host "ERROR: OBS runtime not found. Run setup-obs-runtime.ps1 first." -ForegroundColor Red
    exit 1
}

# Build and publish
Write-Host "Publishing $Configuration build..." -ForegroundColor Green
dotnet publish $AppProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $PublishDir `
    2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Verify key files
$requiredFiles = @("WatchDog.App.exe", "obs.dll", "data", "obs-plugins")
foreach ($file in $requiredFiles) {
    $path = Join-Path $PublishDir $file
    if (-not (Test-Path $path)) {
        Write-Host "WARNING: Missing in publish output: $file" -ForegroundColor Yellow
    }
}

# Create ZIP
Write-Host "Creating distribution ZIP..." -ForegroundColor Green
if (Test-Path $OutputZip) { Remove-Item $OutputZip }
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $OutputZip

$zipSize = [math]::Round((Get-Item $OutputZip).Length / 1MB, 1)
Write-Host ""
Write-Host "Package created: $OutputZip ($zipSize MB)" -ForegroundColor Green
Write-Host "Contents:" -ForegroundColor Green
Write-Host "  - WatchDog application" -ForegroundColor DarkGray
Write-Host "  - OBS runtime (obs.dll, plugins, data)" -ForegroundColor DarkGray
Write-Host "  - All dependencies" -ForegroundColor DarkGray
