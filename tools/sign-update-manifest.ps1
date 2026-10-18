#Requires -Version 7.3
<#
.SYNOPSIS
    Writes and signs the update manifest that WatchDog's in-app updater requires.

.DESCRIPTION
    Sign mode (used by release.yml): reads the PKCS#8 PEM private key from the
    WATCHDOG_UPDATE_SIGNING_KEY environment variable and writes, next to the installer:
      WatchDog-Setup.exe.manifest.json  exact signed bytes: tag, file name, length, SHA-256
      WatchDog-Setup.exe.manifest.sig   detached ECDSA P-256/SHA-256 signature (IEEE P1363)
    The app refuses any installer whose manifest is missing, unsigned by the pinned key,
    for another release, older than the running version, or whose bytes do not match.
    The secret must match the public key pinned in UpdateSigningKey.cs (or -ExpectedPublicKey),
    so a wrong or rotated secret fails the release instead of shipping an update every client refuses.

    -NewKey: generates the key pair once. The private key is written to -PrivateKeyPath
    and never printed; the printed public key (SubjectPublicKeyInfo, base64) belongs in
    src/WatchDog.Core/Updates/UpdateSigningKey.cs.

.EXAMPLE
    ./tools/sign-update-manifest.ps1 -Installer installer/Output/WatchDog-Setup.exe -Tag v1.4.0

.EXAMPLE
    ./tools/sign-update-manifest.ps1 -NewKey -PrivateKeyPath "$HOME/.watchdog-keys/update-signing-key.pem"
#>
[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Sign')]
    [string]$Installer,

    [Parameter(Mandatory, ParameterSetName = 'Sign')]
    [string]$Tag,

    [Parameter(ParameterSetName = 'Sign')]
    [string]$OutputDirectory,

    # Base64 SubjectPublicKeyInfo the secret must match; defaults to the key pinned in the app.
    [Parameter(ParameterSetName = 'Sign')]
    [string]$ExpectedPublicKey,

    [Parameter(Mandatory, ParameterSetName = 'NewKey')]
    [switch]$NewKey,

    [Parameter(Mandatory, ParameterSetName = 'NewKey')]
    [string]$PrivateKeyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$InstallerFileName = 'WatchDog-Setup.exe'
# Must stay identical to UpdateManifestVerifier's tag pattern and installer size limit.
$TagPattern = '\Av[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}(-[0-9A-Za-z.-]{1,32})?\z'
$MaxInstallerBytes = 512L * 1024 * 1024
$PinnedKeyFile = Join-Path $PSScriptRoot '..' 'src' 'WatchDog.Core' 'Updates' 'UpdateSigningKey.cs'
$Sha256 = [System.Security.Cryptography.HashAlgorithmName]::SHA256

# ── Key generation (one-time) ────────────────────────────────────────
if ($NewKey) {
    if (Test-Path -LiteralPath $PrivateKeyPath) {
        throw "Refusing to overwrite an existing key at $PrivateKeyPath"
    }
    $keyDirectory = Split-Path -Parent $PrivateKeyPath
    if ($keyDirectory) { New-Item -ItemType Directory -Force -Path $keyDirectory | Out-Null }

    $ecdsa = [System.Security.Cryptography.ECDsa]::Create(
        [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        [System.IO.File]::WriteAllText($PrivateKeyPath, $ecdsa.ExportPkcs8PrivateKeyPem())
        Write-Output ([Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo()))
    }
    finally {
        $ecdsa.Dispose()
    }
    return
}

# ── Sign mode ────────────────────────────────────────────────────────
if ($Tag -cnotmatch $TagPattern) {
    throw "Tag '$Tag' is not a release tag like v1.4.0 or v1.4.0-rc.1"
}

$privateKeyPem = $env:WATCHDOG_UPDATE_SIGNING_KEY
if ([string]::IsNullOrWhiteSpace($privateKeyPem)) {
    throw 'WATCHDOG_UPDATE_SIGNING_KEY is not set; refusing to publish an unsigned update.'
}

$installerItem = Get-Item -LiteralPath $Installer
if ($installerItem.Name -cne $InstallerFileName) {
    throw "Installer must be named $InstallerFileName (got $($installerItem.Name))"
}
if ($installerItem.Length -le 0 -or $installerItem.Length -gt $MaxInstallerBytes) {
    throw "Installer size $($installerItem.Length) is outside the updater's 1 byte - 512 MiB limit"
}
if (-not $OutputDirectory) { $OutputDirectory = $installerItem.DirectoryName }
if (-not $ExpectedPublicKey) {
    $pinned = [regex]::Match((Get-Content -LiteralPath $PinnedKeyFile -Raw), '"(MF[A-Za-z0-9+/=]+)"')
    if (-not $pinned.Success) { throw "Could not read the pinned public key from $PinnedKeyFile" }
    $ExpectedPublicKey = $pinned.Groups[1].Value
}

$digest = (Get-FileHash -LiteralPath $installerItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$length = $installerItem.Length.ToString([System.Globalization.CultureInfo]::InvariantCulture)

# Field order and spacing are fixed; the app verifies these exact bytes.
$json = '{"schema":1,"product":"WatchDog","tag":"' + $Tag + '","file":"' + $InstallerFileName +
    '","length":' + $length + ',"sha256":"' + $digest + '"}'
$manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)

$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.ImportFromPem($privateKeyPem)
    if ($ecdsa.KeySize -ne 256) { throw 'Update signing key must be ECDSA P-256.' }
    if ([Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo()) -cne $ExpectedPublicKey) {
        throw 'WATCHDOG_UPDATE_SIGNING_KEY does not match the public key pinned in the app; refusing to sign.'
    }
    $signature = $ecdsa.SignData($manifestBytes, $Sha256)
    if (-not $ecdsa.VerifyData($manifestBytes, $signature, $Sha256)) {
        throw 'Signature self-check failed.'
    }
}
finally {
    $ecdsa.Dispose()
}

$manifestPath = Join-Path $OutputDirectory "$InstallerFileName.manifest.json"
$signaturePath = Join-Path $OutputDirectory "$InstallerFileName.manifest.sig"
[System.IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
[System.IO.File]::WriteAllBytes($signaturePath, $signature)
Write-Host "Signed update manifest for $Tag (sha256 $digest, $length bytes)"
