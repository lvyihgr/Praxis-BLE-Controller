using System.Text.Json;
using System.Text.Json.Serialization;

namespace Praxis.Link.Bluetooth;

public static class BluetoothProtocol
{
    public static readonly Guid ServiceUuid = Guid.Parse("7a51f000-5e8f-4c7a-9a1d-707261786973");
    public static readonly Guid CommandToCoreUuid = Guid.Parse("7a51f001-5e8f-4c7a-9a1d-707261786973");
    public static readonly Guid EventFromCoreUuid = Guid.Parse("7a51f002-5e8f-4c7a-9a1d-707261786973");
    public static readonly Guid PeerInfoUuid = Guid.Parse("7a51f003-5e8f-4c7a-9a1d-707261786973");

    public const int ProtocolVersion = 1;
    public const int MaximumMessageBytes = 64 * 1024;
}

public enum BluetoothCentralState
{
    Stopped,
    Initializing,
    Scanning,
    Connecting,
    DiscoveringServices,
    Ready,
    Reconnecting,
    Faulted
}

public sealed record BluetoothConnectionSnapshot(
    BluetoothCentralState State,
    string? DeviceName,
    string? DeviceAddress,
    short? SignalStrengthDbm,
    int? MaximumPduSize,
    string? Error,
    DateTimeOffset UpdatedAt,
    string? PeerId = null,
    string? PeerVersion = null)
{
    public static BluetoothConnectionSnapshot Initial { get; } = new(
        BluetoothCentralState.Stopped,
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.UtcNow);
}

public sealed class BluetoothCentralOptions
{
    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
    public int OutgoingQueueCapacity { get; init; } = 128;
    public int MaximumMessageBytes { get; init; } = BluetoothProtocol.MaximumMessageBytes;
}

public sealed record BluetoothPeerInfo(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("name")] string? Name);

public static class BluetoothPeerInfoCodec
{
    public static BluetoothPeerInfo Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            var peer = JsonSerializer.Deserialize<BluetoothPeerInfo>(payload);
            if (peer is null || string.IsNullOrWhiteSpace(peer.DeviceId) || peer.DeviceId.Length > 128)
                throw new InvalidDataException("Praxis Panel returned invalid peer information.");
            return peer;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Praxis Panel returned invalid peer information.", exception);
        }
    }
}
