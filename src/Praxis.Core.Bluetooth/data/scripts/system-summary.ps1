$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

Add-Type -AssemblyName Microsoft.VisualBasic
$computer = New-Object Microsoft.VisualBasic.Devices.ComputerInfo
$cpu = (Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -Name ProcessorNameString).ProcessorNameString
$systemDrive = New-Object System.IO.DriveInfo($env:SystemDrive)
$totalMemoryGb = [math]::Round($computer.TotalPhysicalMemory / 1GB, 1)
$freeMemoryGb = [math]::Round($computer.AvailablePhysicalMemory / 1GB, 1)
$usedMemoryGb = [math]::Round($totalMemoryGb - $freeMemoryGb, 1)
$driveTotalGb = [math]::Round($systemDrive.TotalSize / 1GB, 1)
$driveFreeGb = [math]::Round($systemDrive.AvailableFreeSpace / 1GB, 1)
$architecture = if ([Environment]::Is64BitOperatingSystem) { '64-bit' } else { '32-bit' }
$windows = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$osName = if ([string]::IsNullOrWhiteSpace($computer.OSFullName)) { "$($windows.ProductName) $($windows.DisplayVersion)" } else { $computer.OSFullName }

[ordered]@{
    Computer = $env:COMPUTERNAME
    User = $env:USERNAME
    OS = $osName
    Architecture = $architecture
    Processor = $cpu.Trim()
    MemoryUsedGB = $usedMemoryGb
    MemoryTotalGB = $totalMemoryGb
    SystemDrive = $env:SystemDrive
    DriveFreeGB = $driveFreeGb
    DriveTotalGB = $driveTotalGb
    UpdatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
} | ConvertTo-Json -Compress
