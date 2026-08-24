namespace Praxis.Core.Actions;

public sealed class HotkeyActionHandler(HotkeySender sender) : IActionHandler
{
    public string Type => "hotkey";

    public Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = context.Definition.RequireParameter("keys");
        sender.Send(keys);
        progress.Report(new ActionProgress(100, $"已发送 {keys}"));
        return Task.FromResult(ActionHandlerResult.Ok($"已发送 {keys}"));
    }
}
