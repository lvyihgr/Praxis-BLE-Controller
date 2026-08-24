namespace Praxis.Core.Actions;

public sealed class MediaActionHandler(HotkeySender sender) : IActionHandler
{
    public string Type => "media";

    public Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = context.Definition.RequireParameter("command");
        var key = command.ToUpperInvariant() switch
        {
            "VOLUMEUP" => (ushort)0xAF,
            "VOLUMEDOWN" => (ushort)0xAE,
            "MUTE" => (ushort)0xAD,
            "PLAYPAUSE" => (ushort)0xB3,
            "NEXT" => (ushort)0xB0,
            "PREVIOUS" => (ushort)0xB1,
            "STOP" => (ushort)0xB2,
            _ => throw new InvalidOperationException($"不支持的媒体命令 '{command}'。")
        };
        sender.SendVirtualKey(key);
        progress.Report(new ActionProgress(100, "媒体命令已发送"));
        return Task.FromResult(ActionHandlerResult.Ok("媒体命令已发送"));
    }
}
