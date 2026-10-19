<#
.SYNOPSIS
    Builds and packages WatchDog for distribution.

.DESCRIPTION
    Publishes WatchDog as a self-contained Windows x64 app with OBS runtime,
    FFmpeg, and .NET runtime bundled. Creates both a portable ZIP and an
    Inno Setup installer (if ISCC.exe is available).

    With -Tag, also writes the signed update manifest next to the installer
    (tools/sign-update-manifest.ps1). That needs the signing key in the
    WATCHDOG_UPDATE_SIGNING_KEY environment variable; the in-app updater
    refuses an installer without a signed manifest.

.EXAMPLE
    .\package.ps1
    .\package.ps1 -Configuration Release
    .\package.ps1 -SkipInstaller
    .\package.ps1 -Tag v1.4.0-rc.1
#>

param(
    [string]$Configuration = "Release",
    [switch]$SkipInstaller,
    # Release tag to sign the update manifest for (for example v1.4.0 or v1.4.0-rc.1).
    [string]$Tag
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

# ── Signing preflight (fail before the long publish) ─────────────
if ($Tag) {
    if ($SkipInstaller) {
        throw "-Tag signs the installer's update manifest; it cannot be combined with -SkipInstaller."
    }
    if ($Tag -cnotmatch "\Av$([regex]::Escape($AppVersion))(-[0-9A-Za-z.-]{1,32})?\z") {
        throw "Tag '$Tag' does not match Directory.Build.props Version $AppVersion (expected v$AppVersion or v$AppVersion-<suffix>)."
    }
    if ([string]::IsNullOrWhiteSpace($env:WATCHDOG_UPDATE_SIGNING_KEY)) {
        throw "WATCHDOG_UPDATE_SIGNING_KEY is not set; cannot sign the update manifest for $Tag."
    }
}

# ── Clean ────────────────────────────────────────────────────────
if (Test-Path $PublishDir) {
    $resolvedPublish = (Resolve-Path -LiteralPath $PublishDir).Path
    $expectedPublish = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'publish'))
    if ($resolvedPublish -ne $expectedPublish -or (Get-Item -LiteralPath $PublishDir).LinkType) {
        throw "Refusing to clean an unexpected or linked publish directory: $resolvedPublish"
    }
    Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
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
    -p:RestoreLockedMode=true `
    --output $PublishDir `
    2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# ── Verify key files ─────────────────────────────────────────────
$requiredFiles = @("WatchDog.App.exe", "obs.dll", "data", "obs-plugins", "ffmpeg/ffmpeg.exe", "ffmpeg/ffprobe.exe", "LICENSE", "NOTICE")
foreach ($file in $requiredFiles) {
    $path = Join-Path $PublishDir $file
    if (-not (Test-Path $path)) {
        throw "Missing required publish output: $file"
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

# ── Sign update manifest ─────────────────────────────────────────
if ($Tag) {
    if (-not $InstallerExe) { throw "No installer was built, so the update manifest for $Tag cannot be signed." }
    & (Join-Path $PSScriptRoot "sign-update-manifest.ps1") -Installer $InstallerExe -Tag $Tag
} elseif ($InstallerExe) {
    Write-Host "Update manifest not signed: the in-app updater will refuse this installer." -ForegroundColor Yellow
    Write-Host "Pass -Tag vX.Y.Z with WATCHDOG_UPDATE_SIGNING_KEY set to sign it." -ForegroundColor Yellow
}

# ── Summary ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "Package complete (v$AppVersion)" -ForegroundColor Green
Write-Host "  Portable ZIP:  $OutputZip ($zipSize MB)" -ForegroundColor DarkGray
if ($InstallerExe) {
    Write-Host "  Installer:     $InstallerExe" -ForegroundColor DarkGray
}
if ($Tag) {
    Write-Host "  Manifest:      $InstallerExe.manifest.json (+ .manifest.sig)" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "Contents:" -ForegroundColor Green
Write-Host "  - WatchDog application (self-contained, .NET 10 bundled)" -ForegroundColor DarkGray
Write-Host "  - OBS runtime (obs.dll, plugins, data)" -ForegroundColor DarkGray
Write-Host "  - FFmpeg (clip editing, thumbnails)" -ForegroundColor DarkGray
Write-Host "  - LICENSE (GPL-2.0) and NOTICE (third-party notices)" -ForegroundColor DarkGray
Write-Host "  - All dependencies" -ForegroundColor DarkGray
