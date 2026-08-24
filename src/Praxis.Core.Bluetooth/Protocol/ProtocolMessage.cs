using System.Text.Json;

namespace Praxis.Core.Protocol;

public sealed record ProtocolMessage(
    int V,
    string Id,
    string Type,
    DateTimeOffset Timestamp,
    JsonElement Payload)
{
    public static ProtocolMessage Create<T>(string type, T payload, string? id = null) => new(
        1,
        id ?? Guid.NewGuid().ToString("N"),
        type,
        DateTimeOffset.UtcNow,
        JsonSerializer.SerializeToElement(payload, JsonDefaults.Options));
}

public sealed record ControlInvokeRequest(
    string ActionId,
    string? ControlId,
    bool Confirmed = false,
    string? ItemId = null,
    bool SuppressFeedback = false);
