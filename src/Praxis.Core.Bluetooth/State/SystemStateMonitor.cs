using System.Diagnostics;
using System.Runtime.InteropServices;
using Praxis.Core.Configuration;

namespace Praxis.Core.State;

public sealed class SystemStateMonitor(
    StateStore states,
    ConfigurationStore configurations,
    ILogger<SystemStateMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await states.SetAsync("core.status", "online", "online", "Praxis Bluetooth", stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PublishForegroundApplicationAsync(stoppingToken);
                await PublishConfiguredProcessesAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "Could not refresh a system state value.");
            }
        }
    }

    private async Task PublishConfiguredProcessesAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurations.GetAsync(cancellationToken);
        var processNames = configuration.Profiles
            .SelectMany(profile => profile.Pages)
            .SelectMany(page => page.EnumerateControls())
            .Select(control => control.StateBinding)
            .Where(binding => binding?.StartsWith("process:", StringComparison.OrdinalIgnoreCase) == true)
            .Select(binding => binding!["process:".Length..])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var processName in processNames)
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                var running = processes.Length > 0;
                await states.SetAsync($"process:{processName}", running, running ? "active" : "idle", processName, cancellationToken);
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
    }

    private Task PublishForegroundApplicationAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return Task.CompletedTask;
        _ = GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return states.SetAsync("system.foregroundApp", process.ProcessName, "active", process.ProcessName, cancellationToken);
        }
        catch (ArgumentException)
        {
            return Task.CompletedTask;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
