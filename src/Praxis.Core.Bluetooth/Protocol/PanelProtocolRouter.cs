using System.Text.Json;
using Praxis.Core.Actions;
using Praxis.Core.Configuration;

namespace Praxis.Core.Protocol;

public sealed class PanelProtocolRouter(
    ConfigurationStore configurations,
    ActionDispatcher actions,
    ILogger<PanelProtocolRouter> logger)
{
    public async Task HandleJsonAsync(
        string json,
        string? source,
        Func<ProtocolMessage, CancellationToken, Task> reply,
        CancellationToken cancellationToken)
    {
        ProtocolMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ProtocolMessage>(json, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            await reply(Error("消息不是有效的 JSON。"), cancellationToken);
            return;
        }

        if (message is null || message.V != 1 ||
            string.IsNullOrWhiteSpace(message.Id) || message.Id.Length > 128 ||
            string.IsNullOrWhiteSpace(message.Type) || message.Type.Length > 128)
        {
            await reply(Error("不支持的协议消息。"), cancellationToken);
            return;
        }

        switch (message.Type)
        {
            case "ping":
                await reply(
                    ProtocolMessage.Create("pong", new { utc = DateTimeOffset.UtcNow }, message.Id),
                    cancellationToken);
                break;

            case "config.refresh":
                var configuration = await configurations.GetAsync(cancellationToken);
                await reply(
                    ProtocolMessage.Create(
                        "config.snapshot",
                        ConfigurationSnapshot.CreatePayload(configuration),
                        message.Id),
                    cancellationToken);
                break;

            case "control.invoke":
                var request = message.Payload.Deserialize<ControlInvokeRequest>(JsonDefaults.Options);
                if (request is null || string.IsNullOrWhiteSpace(request.ActionId))
                {
                    await reply(Error("control.invoke 缺少 actionId。", message.Id), cancellationToken);
                    return;
                }

                _ = RunActionAsync(message.Id, request, source);
                break;

            default:
                await reply(Error($"未知消息类型：{message.Type}", message.Id), cancellationToken);
                break;
        }
    }

    private async Task RunActionAsync(string commandId, ControlInvokeRequest request, string? source)
    {
        try
        {
            await actions.ExecuteAsync(commandId, request, source, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled error while dispatching Praxis action {ActionId}.", request.ActionId);
        }
    }

    private static ProtocolMessage Error(string message, string? id = null) =>
        ProtocolMessage.Create("protocol.error", new { error = message }, id);
}
