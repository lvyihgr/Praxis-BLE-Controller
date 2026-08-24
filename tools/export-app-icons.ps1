param(
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

if (-not ("PraxisIconNative" -as [type]))
{
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public static class PraxisIconNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string fileName,
        int iconIndex,
        int iconWidth,
        int iconHeight,
        IntPtr[] iconHandles,
        uint[] iconIdentifiers,
        uint iconCount,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    public static Icon Extract(string path, int size)
    {
        IntPtr[] handles = new IntPtr[1];
        uint[] identifiers = new uint[1];
        uint extracted = PrivateExtractIcons(path, 0, size, size, handles, identifiers, 1, 0);
        if (extracted == 0 || handles[0] == IntPtr.Zero)
        {
            return Icon.ExtractAssociatedIcon(path);
        }

        try
        {
            using (Icon borrowed = Icon.FromHandle(handles[0]))
            {
                return (Icon)borrowed.Clone();
            }
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }
}
"@
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $OutputDirectory = Join-Path $repositoryRoot "assets\app-icons"
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Get-ProcessExecutable
{
    param([string[]]$Names)

    foreach ($name in $Names)
    {
        foreach ($process in Get-Process -Name $name -ErrorAction SilentlyContinue)
        {
            try
            {
                $path = $process.MainModule.FileName
                if (Test-Path -LiteralPath $path)
                {
                    return $path
                }
            }
            catch
            {
                # Some helper processes do not expose MainModule. Continue to
                # the configured installation paths below.
            }
            finally
            {
                $process.Dispose()
            }
        }
    }

    return $null
}

function Get-ChatGptExecutable
{
    $running = Get-ProcessExecutable -Names @("ChatGPT")
    if ($running)
    {
        return $running
    }

    $windowsApps = Join-Path $env:ProgramFiles "WindowsApps"
    try
    {
        foreach ($package in Get-ChildItem -LiteralPath $windowsApps -Directory -Filter "OpenAI.Codex_*_x64__2p2nqsd0c76g0" -ErrorAction Stop |
                 Sort-Object LastWriteTime -Descending)
        {
            $candidate = Join-Path $package.FullName "app\ChatGPT.exe"
            if (Test-Path -LiteralPath $candidate)
            {
                return $candidate
            }
        }
    }
    catch
    {
        # WindowsApps enumeration may be restricted. A running ChatGPT process
        # remains the preferred source on such systems.
    }

    return $null
}

function Get-FirstExistingPath
{
    param([string[]]$Candidates)

    foreach ($candidate in $Candidates)
    {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate))
        {
            return $candidate
        }
    }

    return $null
}

$chatGptPath = Get-ChatGptExecutable
$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$specifications = @(
    [pscustomobject]@{
        Name = "chatgpt"
        Source = Get-FirstExistingPath @(
            $chatGptPath,
            (Get-ProcessExecutable -Names @("ChatGPT"))
        )
    },
    [pscustomobject]@{
        Name = "zcode"
        Source = Get-FirstExistingPath @(
            "C:\Program Files\ZCode\ZCode.exe",
            (Get-ProcessExecutable -Names @("ZCode"))
        )
    },
    [pscustomobject]@{
        Name = "trae-solo"
        Source = Get-FirstExistingPath @(
            (Join-Path $env:LOCALAPPDATA "Programs\TRAE SOLO\TRAE SOLO.exe"),
            (Get-ProcessExecutable -Names @("TRAE SOLO"))
        )
    },
    [pscustomobject]@{
        Name = "trae"
        Source = Get-FirstExistingPath @(
            (Join-Path $env:LOCALAPPDATA "Programs\Trae\Trae.exe"),
            (Get-ProcessExecutable -Names @("Trae"))
        )
    },
    [pscustomobject]@{
        Name = "edge"
        Source = Get-FirstExistingPath @(
            (Join-Path $programFilesX86 "Microsoft\Edge\Application\msedge.exe"),
            (Get-ProcessExecutable -Names @("msedge"))
        )
    },
    [pscustomobject]@{
        Name = "feishu"
        Source = Get-FirstExistingPath @(
            (Join-Path $env:LOCALAPPDATA "Feishu\icon.ico"),
            (Join-Path $env:LOCALAPPDATA "Feishu\Feishu.exe"),
            (Get-ProcessExecutable -Names @("Feishu"))
        )
    },
    [pscustomobject]@{
        Name = "vscode"
        Source = Get-FirstExistingPath @(
            (Join-Path $env:LOCALAPPDATA "Programs\Microsoft VS Code\Code.exe"),
            (Get-ProcessExecutable -Names @("Code"))
        )
    },
    [pscustomobject]@{
        Name = "powerpoint"
        Source = Get-FirstExistingPath @(
            (Join-Path $env:ProgramFiles "Microsoft Office\root\Office16\POWERPNT.EXE"),
            (Join-Path $programFilesX86 "Microsoft Office\root\Office16\POWERPNT.EXE"),
            (Get-ProcessExecutable -Names @("POWERPNT"))
        )
    }
)

$missing = @($specifications | Where-Object { [string]::IsNullOrWhiteSpace($_.Source) })
if ($missing.Count -gt 0)
{
    throw "Could not locate icon sources for: $($missing.Name -join ', ')."
}

$size = 256
$results = foreach ($specification in $specifications)
{
    $source = $specification.Source
    $icon = if ([System.IO.Path]::GetExtension($source).Equals(".ico", [StringComparison]::OrdinalIgnoreCase))
    {
        [System.Drawing.Icon]::new($source, $size, $size)
    }
    else
    {
        [PraxisIconNative]::Extract($source, $size)
    }

    if ($null -eq $icon)
    {
        throw "Windows did not return an icon for $($specification.Name): $source"
    }

    try
    {
        $sourceBitmap = $icon.ToBitmap()
        try
        {
            $bitmap = [System.Drawing.Bitmap]::new(
                $size,
                $size,
                [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try
            {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try
                {
                    $graphics.Clear([System.Drawing.Color]::Transparent)
                    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
                    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                    $graphics.DrawImage($sourceBitmap, 0, 0, $size, $size)
                }
                finally
                {
                    $graphics.Dispose()
                }

                $destination = Join-Path $OutputDirectory "$($specification.Name).png"
                $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
                $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
                [pscustomobject]@{
                    Name = $specification.Name
                    Width = $bitmap.Width
                    Height = $bitmap.Height
                    Bytes = (Get-Item -LiteralPath $destination).Length
                    Sha256 = $hash
                    Destination = $destination
                }
            }
            finally
            {
                $bitmap.Dispose()
            }
        }
        finally
        {
            $sourceBitmap.Dispose()
        }
    }
    finally
    {
        $icon.Dispose()
    }
}

$results | Format-Table Name, Width, Height, Bytes, Sha256 -AutoSize
