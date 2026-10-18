param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $taskRoot
try {
    dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    dotnet format --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Formatting verification failed.' }
    dotnet build --configuration Release --no-restore -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test --configuration Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally { Pop-Location }
