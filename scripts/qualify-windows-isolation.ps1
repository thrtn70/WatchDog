[CmdletBinding()]
param(
    [switch]$RunNative,
    [switch]$PublicationOnly,
    [switch]$DistinctRootOnly
)

$ErrorActionPreference = 'Stop'

if (-not $RunNative) {
    [Console]::Error.WriteLine(
        'Native isolation qualification is opt-in. Re-run with -RunNative.')
    exit 2
}
if ($PublicationOnly -and $DistinctRootOnly) {
    [Console]::Error.WriteLine('Choose only one native publication probe.')
    exit 2
}
if ($PublicationOnly -or $DistinctRootOnly) {
    [Console]::Error.WriteLine(
        'Atomic-publication native probes remain closed pending retained-root evidence and cleanup review.')
    exit 2
}

function Get-TextSha256 {
    param([Parameter(Mandatory)][string]$Value)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function ConvertTo-WindowsArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $slashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($slashes * 2) + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        [void]$builder.Append(('\' * $slashes))
        [void]$builder.Append($character)
        $slashes = 0
    }
    [void]$builder.Append(('\' * ($slashes * 2)))
    [void]$builder.Append('"')
    return $builder.ToString()
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureName = 'WatchDog.CaptureIsolation.Windows.Qualification'
$appHost = Join-Path $repoRoot "tools\$fixtureName\bin\Release\net10.0-windows\win-x64\$fixtureName.exe"
if (-not (Test-Path -LiteralPath $appHost -PathType Leaf)) {
    [Console]::Error.WriteLine(
        'The already-built Release qualification apphost was not found. See the fixture README.')
    exit 2
}
$binaryDirectory = Split-Path -Parent $appHost
$qualificationAssembly = Join-Path $binaryDirectory "$fixtureName.dll"
$captureIsolationWindowsAssembly = Join-Path $binaryDirectory 'WatchDog.CaptureIsolation.Windows.dll'
$captureIsolationAssembly = Join-Path $binaryDirectory 'WatchDog.CaptureIsolation.dll'
$captureProtocolAssembly = Join-Path $binaryDirectory 'WatchDog.CaptureProtocol.dll'
foreach ($assemblyPath in @(
    $qualificationAssembly,
    $captureIsolationWindowsAssembly,
    $captureIsolationAssembly,
    $captureProtocolAssembly)) {
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        [Console]::Error.WriteLine('The already-built Release qualification DLL set is incomplete.')
        exit 2
    }
}

try {
$utcStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss.fffffffZ')
$nonce = [Guid]::NewGuid()
$artifactRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "WatchDog-Isolation-Qualification\$utcStamp-$($nonce.ToString('D'))")
[System.IO.Directory]::CreateDirectory($artifactRoot) | Out-Null

$repoHead = (& git -C $repoRoot rev-parse HEAD 2>$null | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($repoHead)) {
    $repoHead = 'unavailable'
}
$dirtySummary = (& git -C $repoRoot status --short --untracked-files=all 2>$null) -join "`n"
$dirtySummaryHash = Get-TextSha256 -Value $dirtySummary

$sourceFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repoRoot "tools\$fixtureName") -File -Recurse |
        Where-Object {
            $_.Extension -in '.cs', '.csproj', '.md' -and
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src\WatchDog.CaptureIsolation.Windows') -File -Recurse |
        Where-Object {
            $_.Extension -in '.cs', '.csproj' -and
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src\WatchDog.CaptureIsolation') -File -Recurse |
        Where-Object {
            $_.Extension -in '.cs', '.csproj' -and
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src\WatchDog.CaptureProtocol') -File -Recurse |
        Where-Object {
            $_.Extension -in '.cs', '.csproj' -and
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
    Get-Item -LiteralPath $PSCommandPath
) | Sort-Object FullName -Unique
$sourceReceipt = ($sourceFiles | ForEach-Object {
    $relative = [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
    "$relative=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
}) -join "`n"
$sourceHash = Get-TextSha256 -Value $sourceReceipt
}
catch {
    [Console]::Error.WriteLine('Qualification prelaunch evidence could not be established.')
    exit 6
}
$binaryPaths = @(
    $appHost,
    $qualificationAssembly,
    $captureIsolationWindowsAssembly,
    $captureIsolationAssembly,
    $captureProtocolAssembly
)
$binaryStreams = [System.Collections.Generic.List[System.IO.FileStream]]::new()
$exitCode = 6
$runner = $null
$launchAttempted = $false
try {
    foreach ($binaryPath in $binaryPaths) {
        $binaryStreams.Add([System.IO.FileStream]::new(
            $binaryPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read))
    }
    $binaryHashes = @()
    foreach ($binaryStream in $binaryStreams) {
        $binaryHashes += [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData($binaryStream)).ToLowerInvariant()
        $binaryStream.Position = 0
    }

    $stdoutPath = Join-Path $artifactRoot 'runner.stdout.txt'
    $stderrPath = Join-Path $artifactRoot 'runner.stderr.txt'
    $argumentValues = @(
        '--opt-in', 'WATCHDOG-RUN-NATIVE-ISOLATION-QUALIFICATION',
        '--mode', 'runner',
        '--nonce', $nonce.ToString('D'),
        '--artifact-root', $artifactRoot,
        '--executable', $appHost,
        '--repo-head', $repoHead,
        '--dirty-summary-hash', $dirtySummaryHash,
        '--source-hash', $sourceHash,
        '--binary-hash', $binaryHashes[0],
        '--qualification-assembly-hash', $binaryHashes[1],
        '--capture-isolation-windows-hash', $binaryHashes[2],
        '--capture-isolation-hash', $binaryHashes[3],
        '--capture-protocol-hash', $binaryHashes[4]
    )
    if ($PublicationOnly) {
        $argumentValues += @('--publication-only', 'true')
    }
    if ($DistinctRootOnly) {
        $argumentValues += @('--distinct-root-only', 'true')
    }
    $argumentLine = ($argumentValues | ForEach-Object { ConvertTo-WindowsArgument -Value $_ }) -join ' '

    $startParameters = @{
        FilePath = $appHost
        ArgumentList = $argumentLine
        WindowStyle = 'Hidden'
        RedirectStandardOutput = $stdoutPath
        RedirectStandardError = $stderrPath
        PassThru = $true
    }
    if ($PublicationOnly -or $DistinctRootOnly) {
        $startParameters.WorkingDirectory = $artifactRoot
    }
    $launchAttempted = $true
    $runner = Start-Process @startParameters

    if (-not $runner.WaitForExit(210000)) {
        try {
            $runner.Kill($false)
            if (-not $runner.WaitForExit(15000)) {
                [Console]::Error.WriteLine(
                    'The exact retained qualification runner could not be confirmed terminated.')
            }
        }
        catch {
            [Console]::Error.WriteLine(
                'The exact retained qualification runner could not be confirmed terminated.')
        }
        Write-Host "Qualification timed out. Artifacts: $artifactRoot"
        $exitCode = 5
    }
    else {
        $exitCode = $runner.ExitCode
        Write-Host "Qualification artifacts: $artifactRoot"
        Write-Host "Qualification exit code: $exitCode"
    }
}
catch {
    [Console]::Error.WriteLine('Qualification launcher failed before a confirmed result.')
    $exitCode = if ($launchAttempted) { 5 } else { 6 }
    if ($null -ne $runner) {
        try {
            if (-not $runner.HasExited) {
                $runner.Kill($false)
            }
            if (-not $runner.WaitForExit(15000)) {
                [Console]::Error.WriteLine(
                    'The exact retained qualification runner could not be confirmed terminated.')
            }
        }
        catch {
            [Console]::Error.WriteLine(
                'The exact retained qualification runner could not be confirmed terminated.')
        }
    }
}
finally {
    foreach ($binaryStream in $binaryStreams) {
        try {
            $binaryStream.Dispose()
        }
        catch {
            [Console]::Error.WriteLine('A retained qualification binary handle could not be closed.')
            $exitCode = 5
        }
    }
}
exit $exitCode
