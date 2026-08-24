$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }

Push-Location $projectRoot
try {
    & $dotnet run --project 'src\Praxis.Core.Bluetooth\Praxis.Core.Bluetooth.csproj'
}
finally {
    Pop-Location
}
