<#
.SYNOPSIS
    Builds and packages WatchDog for distribution.

.DESCRIPTION
    Publishes WatchDog as a self-contained Windows x64 app with OBS runtime,
    FFmpeg, and .NET runtime bundled. Creates both a portable ZIP and an
    Inno Setup installer (if ISCC.exe is available).

.EXAMPLE
    .\package.ps1
    .\package.ps1 -Configuration Release
    .\package.ps1 -SkipInstaller
#>

param(
    [string]$Configuration = "Release",
    [switch]$SkipInstaller
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

# ── Read version from Directory.Build.props ──────────────────────
$BuildPropsPath = Join-Path $ProjectRoot "Directory.Build.props"
[xml]$BuildProps = Get-Content $BuildPropsPath
$AppVersion = $BuildProps.Project.PropertyGroup.Version
if (-not $AppVersion) {
    Write-Host "ERROR: No <Version> property in Directory.Build.props" -ForegroundColor Red
    exit 1
}
Write-Host "Version: $AppVersion" -ForegroundColor Green
Write-Host ""

# ── Clean ────────────────────────────────────────────────────────
if (Test-Path $PublishDir) {
    Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
    Remove-Item $PublishDir -Recurse -Force
}

# ── Verify OBS runtime ───────────────────────────────────────────
$ObsRuntime = Join-Path $ProjectRoot "obs-runtime"
if (-not (Test-Path (Join-Path $ObsRuntime "obs.dll"))) {
    Write-Host "ERROR: OBS runtime not found. Run setup-obs-runtime.ps1 first." -ForegroundColor Red
    exit 1
}

# ── Build and publish (self-contained) ───────────────────────────
Write-Host "Publishing $Configuration build (self-contained)..." -ForegroundColor Green
dotnet publish $AppProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $PublishDir `
    2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# ── Verify key files ─────────────────────────────────────────────
$requiredFiles = @("WatchDog.App.exe", "obs.dll", "data", "obs-plugins")
foreach ($file in $requiredFiles) {
    $path = Join-Path $PublishDir $file
    if (-not (Test-Path $path)) {
        Write-Host "WARNING: Missing in publish output: $file" -ForegroundColor Yellow
    }
}

# ── Create portable ZIP ──────────────────────────────────────────
Write-Host "Creating portable ZIP..." -ForegroundColor Green
if (Test-Path $OutputZip) { Remove-Item $OutputZip }
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $OutputZip

$zipSize = [math]::Round((Get-Item $OutputZip).Length / 1MB, 1)
Write-Host "  ZIP: $OutputZip ($zipSize MB)" -ForegroundColor DarkGray

# ── Build Inno Setup installer ───────────────────────────────────
$InstallerExe = $null
if (-not $SkipInstaller) {
    # Try standard Inno Setup install paths, then PATH
    $IsccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "ISCC.exe"
    )

    $IsccPath = $null
    foreach ($candidate in $IsccCandidates) {
        if (Get-Command $candidate -ErrorAction SilentlyContinue) {
            $IsccPath = $candidate
            break
        }
        if (Test-Path $candidate) {
            $IsccPath = $candidate
            break
        }
    }

    $IssFile = Join-Path $ProjectRoot "installer" "watchdog.iss"
    if ($IsccPath -and (Test-Path $IssFile)) {
        Write-Host "Building installer with Inno Setup..." -ForegroundColor Green
        & $IsccPath "/DAppVersion=$AppVersion" $IssFile

        if ($LASTEXITCODE -ne 0) {
            Write-Host "Installer build failed!" -ForegroundColor Red
            exit 1
        }

        $InstallerPath = Join-Path $ProjectRoot "installer" "Output" "WatchDog-Setup.exe"
        if (Test-Path $InstallerPath) {
            $setupSize = [math]::Round((Get-Item $InstallerPath).Length / 1MB, 1)
            $InstallerExe = $InstallerPath
            Write-Host "  Installer: $InstallerPath ($setupSize MB)" -ForegroundColor DarkGray
        }
    } else {
        Write-Host "Inno Setup not found — skipping installer build." -ForegroundColor Yellow
        Write-Host "Install Inno Setup 6 from https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    }
}

# ── Summary ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "Package complete (v$AppVersion)" -ForegroundColor Green
Write-Host "  Portable ZIP:  $OutputZip ($zipSize MB)" -ForegroundColor DarkGray
if ($InstallerExe) {
    Write-Host "  Installer:     $InstallerExe" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "Contents:" -ForegroundColor Green
Write-Host "  - WatchDog application (self-contained, .NET 9 bundled)" -ForegroundColor DarkGray
Write-Host "  - OBS runtime (obs.dll, plugins, data)" -ForegroundColor DarkGray
Write-Host "  - FFmpeg (clip editing, thumbnails)" -ForegroundColor DarkGray
Write-Host "  - All dependencies" -ForegroundColor DarkGray
