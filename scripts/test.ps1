$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }

Push-Location $projectRoot
try {
    & $dotnet build 'Praxis.sln'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnet run --project 'tests\Praxis.Core.SelfTest\Praxis.Core.SelfTest.csproj' --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnet run --project 'tests\Praxis.Link.Bluetooth.SelfTest\Praxis.Link.Bluetooth.SelfTest.csproj' --no-build
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
