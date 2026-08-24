using System.Collections.Concurrent;
using System.Text.Json;
using Praxis.Core.Protocol;

namespace Praxis.Core.State;

public sealed record StateEntry(
    string Key,
    JsonElement Value,
    string Status,
    string? Label,
    DateTimeOffset UpdatedAt);

public sealed class StateStore(IPanelMessageSink panels)
{
    private readonly ConcurrentDictionary<string, StateEntry> _states = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<StateEntry> Snapshot() => _states.Values.OrderBy(x => x.Key).ToArray();

    public async Task SetAsync<T>(
        string key,
        T value,
        string status = "idle",
        string? label = null,
        CancellationToken cancellationToken = default,
        bool forcePublish = false)
    {
        var element = JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
        if (!forcePublish && _states.TryGetValue(key, out var current) &&
            current.Status == status && current.Label == label && current.Value.GetRawText() == element.GetRawText())
            return;

        var entry = new StateEntry(key, element, status, label, DateTimeOffset.UtcNow);
        _states[key] = entry;
        await panels.BroadcastAsync(ProtocolMessage.Create("state.changed", entry), cancellationToken);
    }
}
