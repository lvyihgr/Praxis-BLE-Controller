using System.Diagnostics;

namespace Praxis.Core.Actions;

public sealed class LaunchActionHandler : IActionHandler
{
    public string Type => "launch";

    public Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = context.Definition.RequireParameter("file");
        var arguments = context.Definition.OptionalParameter("arguments") ?? string.Empty;
        var workingDirectory = context.Definition.OptionalParameter("workingDirectory");

        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            UseShellExecute = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        var process = Process.Start(startInfo);
        if (process is null)
            return Task.FromResult(ActionHandlerResult.Fail($"无法启动 {file}"));

        progress.Report(new ActionProgress(100, $"已启动 {context.Definition.Name}"));
        return Task.FromResult(ActionHandlerResult.Ok($"已启动 {context.Definition.Name}"));
    }
}
