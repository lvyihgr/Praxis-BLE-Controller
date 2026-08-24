namespace Praxis.Core.Protocol;

public interface IPanelMessageSink
{
    int Count { get; }

    Task BroadcastAsync(
        ProtocolMessage message,
        CancellationToken cancellationToken = default);
}
