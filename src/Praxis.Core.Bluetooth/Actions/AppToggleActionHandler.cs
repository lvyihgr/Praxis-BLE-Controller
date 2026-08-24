using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Praxis.Core.Actions;

public sealed class AppToggleActionHandler : IActionHandler
{
    private readonly object _toggleGate = new();
    private string? _lastLocalDisplayedActionId;
    private IntPtr _lastLocalDisplayedWindow;

    public string Type => "appToggle";

    public Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用窗口切换仅支持 Windows。");

        lock (_toggleGate)
        {
            var processNames = ParseProcessNames(context.Definition.RequireParameter("processNames"));
            var windows = AppWindowController.Find(processNames);
            var foreground = AppWindowController.ForegroundWindow;
            var foregroundTarget = windows.FirstOrDefault(window => window == foreground);
            var isLocalPanel = IsLoopback(context.Source);

            if (!isLocalPanel)
                ClearLocalDisplayMemory();

            if (foregroundTarget != IntPtr.Zero && !AppWindowController.IsMinimized(foregroundTarget))
            {
                AppWindowController.Minimize(foregroundTarget);
                ClearLocalDisplayMemory();
                progress.Report(new ActionProgress(100, $"已最小化 {context.Definition.Name}"));
                return Task.FromResult(ActionHandlerResult.Ok($"已最小化 {context.Definition.Name}"));
            }

            if (windows.Count > 0)
            {
                var rememberedTarget = ResolveRememberedTarget(context.Definition.Id, windows);
                if (isLocalPanel && rememberedTarget != IntPtr.Zero && !AppWindowController.IsMinimized(rememberedTarget))
                {
                    AppWindowController.Minimize(rememberedTarget);
                    ClearLocalDisplayMemory();
                    progress.Report(new ActionProgress(100, $"已最小化 {context.Definition.Name}"));
                    return Task.FromResult(ActionHandlerResult.Ok($"已最小化 {context.Definition.Name}"));
                }

                var target = windows.FirstOrDefault(AppWindowController.IsMinimized);
                if (target == IntPtr.Zero)
                    target = windows[0];
                AppWindowController.Activate(target);
                if (isLocalPanel)
                    RememberLocalDisplay(context.Definition.Id, target);
                progress.Report(new ActionProgress(100, $"已显示 {context.Definition.Name}"));
                return Task.FromResult(ActionHandlerResult.Ok($"已显示 {context.Definition.Name}"));
            }

            Launch(context);
            if (isLocalPanel)
                RememberLocalDisplay(context.Definition.Id, IntPtr.Zero);
            progress.Report(new ActionProgress(100, $"已启动 {context.Definition.Name}"));
            return Task.FromResult(ActionHandlerResult.Ok($"已启动 {context.Definition.Name}"));
        }
    }

    internal static string[] ParseProcessNames(string value) =>
        value.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static bool IsLoopback(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || !IPAddress.TryParse(source, out var address))
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private IntPtr ResolveRememberedTarget(string actionId, IReadOnlyList<IntPtr> windows)
    {
        if (!string.Equals(_lastLocalDisplayedActionId, actionId, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        if (_lastLocalDisplayedWindow == IntPtr.Zero)
            return windows.FirstOrDefault(window => !AppWindowController.IsMinimized(window));
        return windows.Contains(_lastLocalDisplayedWindow) ? _lastLocalDisplayedWindow : IntPtr.Zero;
    }

    private void RememberLocalDisplay(string actionId, IntPtr window)
    {
        _lastLocalDisplayedActionId = actionId;
        _lastLocalDisplayedWindow = window;
    }

    private void ClearLocalDisplayMemory()
    {
        _lastLocalDisplayedActionId = null;
        _lastLocalDisplayedWindow = IntPtr.Zero;
    }

    private static void Launch(ActionExecutionContext context)
    {
        var appUserModelId = context.Definition.OptionalParameter("appUserModelId");
        ProcessStartInfo startInfo;
        if (!string.IsNullOrWhiteSpace(appUserModelId))
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{appUserModelId}",
                UseShellExecute = true
            };
        }
        else
        {
            var file = Environment.ExpandEnvironmentVariables(context.Definition.RequireParameter("file"));
            if (Path.IsPathFullyQualified(file) && !File.Exists(file))
                throw new FileNotFoundException($"找不到 {context.Definition.Name} 的程序文件。", file);

            startInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = context.Definition.OptionalParameter("arguments") ?? string.Empty,
                UseShellExecute = true
            };
            var workingDirectory = context.Definition.OptionalParameter("workingDirectory");
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                startInfo.WorkingDirectory = Environment.ExpandEnvironmentVariables(workingDirectory);
        }

        if (Process.Start(startInfo) is null)
            throw new Win32Exception($"无法启动 {context.Definition.Name}。");
    }
}

internal static class AppWindowController
{
    private const int SwMaximize = 3;
    private const int SwShow = 5;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint GwOwner = 4;

    public static IntPtr ForegroundWindow => GetForegroundWindow();

    public static IReadOnlyList<IntPtr> Find(IReadOnlyCollection<string> processNames, bool requireWindowTitle = true)
    {
        var names = processNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var windows = new List<IntPtr>();
        EnumWindows((window, parameter) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != IntPtr.Zero ||
                (requireWindowTitle && GetWindowTextLength(window) == 0))
                return true;

            GetWindowThreadProcessId(window, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (names.Contains(process.ProcessName))
                    windows.Add(window);
            }
            catch (ArgumentException)
            {
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static bool IsMinimized(IntPtr window) => IsIconic(window);

    public static bool IsFromProcess(IntPtr window, IReadOnlyCollection<string> processNames)
    {
        if (window == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
            return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return processNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void Minimize(IntPtr window) => ShowWindowAsync(window, SwMinimize);

    public static void Activate(IntPtr window)
    {
        ShowWindowAsync(window, IsIconic(window) ? SwRestore : SwShow);
        FocusWindow(window);
    }

    public static void ActivateMaximized(IntPtr window)
    {
        ShowWindow(window, SwRestore);
        ShowWindow(window, SwMaximize);
        FocusWindow(window);
    }

    private static void FocusWindow(IntPtr window)
    {
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetThread = GetWindowThreadProcessId(window, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
            SetActiveWindow(window);
            SetFocus(window);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
