using System.Collections.Concurrent;

namespace Praxis.Core.Actions;

public sealed class CommandDeduplicator
{
    private readonly ConcurrentDictionary<string, CacheEntry> _commands = new(StringComparer.Ordinal);

    public Task<ActionExecutionResult> RunOnceAsync(string commandId, Func<Task<ActionExecutionResult>> factory)
    {
        Cleanup();
        var entry = _commands.GetOrAdd(commandId, _ => new CacheEntry(
            DateTimeOffset.UtcNow,
            new Lazy<Task<ActionExecutionResult>>(factory, LazyThreadSafetyMode.ExecutionAndPublication)));
        return entry.Result.Value;
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var item in _commands.Where(item => item.Value.CreatedAt < cutoff))
            _commands.TryRemove(item.Key, out _);
    }

    private sealed record CacheEntry(DateTimeOffset CreatedAt, Lazy<Task<ActionExecutionResult>> Result);
}
