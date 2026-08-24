$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$output = Join-Path $projectRoot 'artifacts\publish\bluetooth-win-x64'

Push-Location $projectRoot
try {
    if (Test-Path -LiteralPath $output) {
        $resolvedRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')
        $resolvedOutput = [IO.Path]::GetFullPath($output).TrimEnd('\')
        if (-not $resolvedOutput.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝清理工作区外的发布目录：$resolvedOutput"
        }
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }
    & $dotnet publish 'src\Praxis.Core.Bluetooth\Praxis.Core.Bluetooth.csproj' -c Release -r win-x64 --self-contained true -o $output
    if ($LASTEXITCODE -eq 0) {
        $dotnetExecutable = (Get-Command $dotnet -ErrorAction Stop).Source
        $dotnetRoot = Split-Path -Parent $dotnetExecutable
        $dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
        $dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
        if (-not (Test-Path -LiteralPath $dotnetLicense) -or -not (Test-Path -LiteralPath $dotnetNotices)) {
            throw '当前 .NET SDK 缺少 LICENSE.txt 或 ThirdPartyNotices.txt，不能生成合规的自包含发布包。'
        }
        Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $output 'DOTNET-LICENSE.txt') -Force
        Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $output 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
        Write-Host "Praxis Bluetooth 1.2.0 已发布到：$output"
    }
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
