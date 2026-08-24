namespace Praxis.Core.Actions;

public sealed class NoopActionHandler : IActionHandler
{
    public string Type => "noop";

    public Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new ActionProgress(100, "Praxis Bluetooth 连接正常"));
        return Task.FromResult(ActionHandlerResult.Ok("连接正常"));
    }
}
