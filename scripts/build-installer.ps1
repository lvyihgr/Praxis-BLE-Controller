param(
    [string]$Version = '1.2.0'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    $iscc = $command.Source
}
if (-not $iscc) {
    throw '未找到 Inno Setup 6。请先安装 Inno Setup 6，再重新运行此脚本。'
}

Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $installerOutput = Join-Path $projectRoot 'artifacts\installer'
    if (Test-Path -LiteralPath $installerOutput) {
        $resolvedRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')
        $resolvedOutput = [IO.Path]::GetFullPath($installerOutput).TrimEnd('\')
        if (-not $resolvedOutput.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝清理工作区外的安装包目录：$resolvedOutput"
        }
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }

    $env:PRAXIS_VERSION = $Version
    & $iscc (Join-Path $projectRoot 'installer\Praxis.Bluetooth.iss')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Praxis Bluetooth $Version 安装包已生成到：$installerOutput"
}
finally {
    Remove-Item Env:PRAXIS_VERSION -ErrorAction SilentlyContinue
    Pop-Location
}
