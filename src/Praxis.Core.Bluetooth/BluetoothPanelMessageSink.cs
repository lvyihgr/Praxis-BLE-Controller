using System.Text;
using System.Text.Json;
using Praxis.Core.Protocol;
using Praxis.Link.Bluetooth;
using Praxis.Core.Security;

namespace Praxis.Core.Bluetooth;

public sealed class BluetoothPanelMessageSink(
    BluetoothCentralTransport transport,
    SecureSession secureSession,
    BluetoothSessionReadiness readiness) : IPanelMessageSink
{
    public int Count => transport.IsReady && secureSession.IsActive && readiness.IsReady ? 1 : 0;

    public Task BroadcastAsync(
        ProtocolMessage message,
        CancellationToken cancellationToken = default) =>
        readiness.IsReady ? SendAsync(message, cancellationToken) : Task.CompletedTask;

    public async Task SendAsync(
        ProtocolMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!transport.IsReady || !secureSession.IsActive)
        {
            return;
        }

        var secured = secureSession.Wrap(message);
        await SendRawAsync(secured, Classify(message), cancellationToken);
    }

    public async Task SendAsync(
        ProtocolMessage message,
        BluetoothOutgoingMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (!transport.IsReady || !secureSession.IsActive)
            return;

        var secured = secureSession.Wrap(message);
        await SendRawAsync(secured, metadata, cancellationToken);
    }

    public Task SendPlainAsync(
        ProtocolMessage message,
        CancellationToken cancellationToken = default) =>
        SendRawAsync(message, new BluetoothOutgoingMetadata(
            message.Type,
            BluetoothMessagePriority.High,
            CommandId: message.Id), cancellationToken);

    private async Task SendRawAsync(
        ProtocolMessage message,
        BluetoothOutgoingMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (!transport.IsReady)
            return;

        var json = JsonSerializer.Serialize(message, JsonDefaults.CompactOptions);
        await transport.SendAsync(
            Encoding.UTF8.GetBytes(json),
            metadata,
            cancellationToken);
    }

    public static BluetoothOutgoingMetadata Classify(ProtocolMessage message)
    {
        if (message.Type is "action.completed" or "action.failed" or "protocol.error" or "pong" or
            "core.hello" or "config.unchanged" or "bootstrap.plan" or "bootstrap.completed")
        {
            return new BluetoothOutgoingMetadata(
                message.Type,
                BluetoothMessagePriority.High,
                CommandId: message.Id);
        }

        if (message.Type == "state.changed" &&
            message.Payload.TryGetProperty("key", out var keyElement))
        {
            var key = keyElement.GetString();
            string? commandId = null;
            if (string.Equals(key, "presentation.state", StringComparison.Ordinal) &&
                message.Payload.TryGetProperty("value", out var valueElement) &&
                valueElement.ValueKind == JsonValueKind.Object &&
                valueElement.TryGetProperty("commandId", out var commandIdElement) &&
                commandIdElement.ValueKind == JsonValueKind.String)
            {
                commandId = commandIdElement.GetString();
            }

            if (!string.IsNullOrWhiteSpace(commandId))
            {
                return new BluetoothOutgoingMetadata(
                    message.Type,
                    BluetoothMessagePriority.High,
                    CommandId: commandId);
            }

            return new BluetoothOutgoingMetadata(
                message.Type,
                BluetoothMessagePriority.Normal,
                string.IsNullOrWhiteSpace(key) ? null : $"state.changed:{key}");
        }

        if (message.Type == "app.icon" &&
            message.Payload.TryGetProperty("actionId", out var actionIdElement))
        {
            var actionId = actionIdElement.GetString();
            return new BluetoothOutgoingMetadata(
                message.Type,
                BluetoothMessagePriority.Normal,
                string.IsNullOrWhiteSpace(actionId) ? "app.icon" : $"app.icon:{actionId}",
                WriteMode: BluetoothWriteMode.WithoutResponse);
        }

        return new BluetoothOutgoingMetadata(message.Type);
    }
}
