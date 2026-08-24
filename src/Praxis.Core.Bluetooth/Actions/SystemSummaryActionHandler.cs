using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.VisualBasic.Devices;

namespace Praxis.Core.Actions;

public sealed class SystemSummaryActionHandler : IActionHandler
{
    public string Type => "systemSummary";

    public async Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("系统摘要仅支持 Windows。");

        progress.Report(new ActionProgress(10, "正在读取系统状态"));
        var summary = await SystemSummaryCollector.CollectAsync(cancellationToken);
        progress.Report(new ActionProgress(100, "系统摘要已更新"));
        return ActionHandlerResult.Ok("系统摘要已更新", JsonSerializer.Serialize(summary));
    }
}

internal static partial class SystemSummaryCollector
{
    public static async Task<object> CollectAsync(CancellationToken cancellationToken)
    {
        var computer = new ComputerInfo();
        var systemDriveName = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var systemDrive = new DriveInfo(systemDriveName);
        var totalMemoryGb = RoundGb(computer.TotalPhysicalMemory);
        var freeMemoryGb = RoundGb(computer.AvailablePhysicalMemory);
        var gpuIdentity = ReadPrimaryGpuIdentity();

        var cpuTask = SampleCpuUsageAsync(cancellationToken);
        var gpuTask = SampleGpuMetricsAsync(cancellationToken);
        await Task.WhenAll(cpuTask, gpuTask);
        var gpu = await gpuTask;

        return new
        {
            Computer = Environment.MachineName,
            User = Environment.UserName,
            OS = ReadOperatingSystemName(computer),
            Architecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
            Processor = ReadRegistryString(
                Registry.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString")?.Trim() ?? "Unknown CPU",
            CpuUsagePercent = await cpuTask,
            MemoryUsedGB = Math.Round(totalMemoryGb - freeMemoryGb, 1),
            MemoryTotalGB = totalMemoryGb,
            SystemDrive = systemDriveName,
            DriveFreeGB = RoundGb((ulong)systemDrive.AvailableFreeSpace),
            DriveTotalGB = RoundGb((ulong)systemDrive.TotalSize),
            GpuName = gpuIdentity.Name,
            GpuUsagePercent = gpu.UsagePercent,
            GpuDedicatedUsedGB = gpu.DedicatedUsedBytes is null ? (double?)null : RoundGb(gpu.DedicatedUsedBytes.Value),
            GpuDedicatedTotalGB = gpuIdentity.DedicatedBytes is null ? (double?)null : RoundGb(gpuIdentity.DedicatedBytes.Value),
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    private static string ReadOperatingSystemName(ComputerInfo computer)
    {
        if (!string.IsNullOrWhiteSpace(computer.OSFullName))
            return computer.OSFullName;
        var product = ReadRegistryString(
            Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "ProductName") ?? "Windows";
        var displayVersion = ReadRegistryString(
            Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "DisplayVersion");
        return string.IsNullOrWhiteSpace(displayVersion) ? product : $"{product} {displayVersion}";
    }

    private static string? ReadRegistryString(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static GpuIdentity ReadPrimaryGpuIdentity()
    {
        var best = new GpuIdentity(null, null);
        try
        {
            using var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (video is null)
                return best;

            foreach (var adapterId in video.GetSubKeyNames())
            {
                using var adapter = video.OpenSubKey(adapterId + @"\0000");
                if (adapter is null)
                    continue;
                var name = ReadRegistryText(adapter.GetValue("HardwareInformation.AdapterString"))
                           ?? ReadRegistryText(adapter.GetValue("DriverDesc"));
                var memory = ReadRegistryMemory(adapter.GetValue("HardwareInformation.qwMemorySize"))
                             ?? ReadRegistryMemory(adapter.GetValue("HardwareInformation.MemorySize"));
                if (memory is not null && (best.DedicatedBytes is null || memory > best.DedicatedBytes))
                    best = new GpuIdentity(name, memory);
                else if (best.Name is null && !string.IsNullOrWhiteSpace(name))
                    best = new GpuIdentity(name, best.DedicatedBytes);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
        return best;
    }

    private static ulong? ReadRegistryMemory(object? value) => value switch
    {
        long signed when signed > 0 => (ulong)signed,
        int signed when signed != 0 => (ulong)(uint)signed,
        uint unsigned when unsigned > 0 => unsigned,
        ulong unsigned when unsigned > 0 => unsigned,
        byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
        byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
        _ => null
    };

    private static string? ReadRegistryText(object? value)
    {
        var text = value switch
        {
            string plain => plain,
            byte[] bytes when bytes.Length >= 2 => Encoding.Unicode.GetString(bytes),
            _ => null
        };
        text = text?.TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static async Task<double> SampleCpuUsageAsync(CancellationToken cancellationToken)
    {
        if (!GetSystemTimes(out var idleStart, out var kernelStart, out var userStart))
            return 0;
        await Task.Delay(350, cancellationToken);
        if (!GetSystemTimes(out var idleEnd, out var kernelEnd, out var userEnd))
            return 0;

        var idle = ToUInt64(idleEnd) - ToUInt64(idleStart);
        var kernel = ToUInt64(kernelEnd) - ToUInt64(kernelStart);
        var user = ToUInt64(userEnd) - ToUInt64(userStart);
        var total = kernel + user;
        if (total == 0)
            return 0;
        return Math.Round(Math.Clamp((total - idle) * 100d / total, 0, 100), 1);
    }

    private static async Task<GpuMetrics> SampleGpuMetricsAsync(CancellationToken cancellationToken)
    {
        const string enginePath = @"\GPU Engine(*)\Utilization Percentage";
        const string memoryPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
        if (PdhOpenQuery(null, UIntPtr.Zero, out var query) != 0)
            return new GpuMetrics(null, null);

        try
        {
            var engineCounter = AddEnglishCounter(query, enginePath);
            var memoryCounter = AddEnglishCounter(query, memoryPath);
            if (engineCounter == IntPtr.Zero && memoryCounter == IntPtr.Zero)
                return new GpuMetrics(null, null);

            if (PdhCollectQueryData(query) != 0)
                return new GpuMetrics(null, null);
            await Task.Delay(350, cancellationToken);
            if (PdhCollectQueryData(query) != 0)
                return new GpuMetrics(null, null);

            double? usage = null;
            if (engineCounter != IntPtr.Zero)
            {
                var engineValues = ReadCounterArray(engineCounter);
                var engineGroups = engineValues
                    .GroupBy(item => EngineGroup(item.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Sum(item => Math.Max(0, item.Value)))
                    .ToArray();
                if (engineGroups.Length > 0)
                    usage = Math.Round(Math.Clamp(engineGroups.Max(), 0, 100), 1);
            }

            ulong? dedicatedBytes = null;
            if (memoryCounter != IntPtr.Zero)
            {
                var memoryValues = ReadCounterArray(memoryCounter).Select(item => item.Value).Where(value => value >= 0).ToArray();
                if (memoryValues.Length > 0)
                    dedicatedBytes = (ulong)Math.Round(memoryValues.Max());
            }
            return new GpuMetrics(usage, dedicatedBytes);
        }
        finally
        {
            PdhCloseQuery(query);
        }
    }

    private static IntPtr AddEnglishCounter(IntPtr query, string path) =>
        PdhAddEnglishCounter(query, path, UIntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    private static IReadOnlyList<CounterItem> ReadCounterArray(IntPtr counter)
    {
        const uint moreData = 0x800007D2;
        const uint formatDouble = 0x00000200;
        uint bufferSize = 0;
        uint itemCount = 0;
        var status = PdhGetFormattedCounterArray(counter, formatDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != moreData || bufferSize == 0 || itemCount == 0)
            return [];

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(counter, formatDouble, ref bufferSize, ref itemCount, buffer);
            if (status != 0)
                return [];
            var result = new List<CounterItem>((int)itemCount);
            var itemSize = Marshal.SizeOf<PdhCounterValueItem>();
            for (var index = 0; index < itemCount; index++)
            {
                var item = Marshal.PtrToStructure<PdhCounterValueItem>(IntPtr.Add(buffer, checked((int)index * itemSize)));
                if (item.Value.Status == 0)
                    result.Add(new CounterItem(Marshal.PtrToStringUni(item.Name) ?? index.ToString(), item.Value.DoubleValue));
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string EngineGroup(string name)
    {
        var match = EngineGroupExpression().Match(name);
        return match.Success ? match.Value : name;
    }

    private static double RoundGb(ulong bytes) => Math.Round(bytes / 1024d / 1024d / 1024d, 1);
    private static ulong ToUInt64(NativeFileTime value) => ((ulong)value.High << 32) | value.Low;

    private sealed record GpuIdentity(string? Name, ulong? DedicatedBytes);
    private sealed record GpuMetrics(double? UsagePercent, ulong? DedicatedUsedBytes);
    private sealed record CounterItem(string Name, double Value);

    [GeneratedRegex(@"luid_.+?_phys_\d+_eng_\d+", RegexOptions.IgnoreCase)]
    private static partial Regex EngineGroupExpression();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFormattedValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValueItem
    {
        public IntPtr Name;
        public PdhFormattedValue Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
