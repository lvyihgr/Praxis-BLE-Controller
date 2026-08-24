using System.Collections.Concurrent;
using System.Text.Json;
using Praxis.Core.Protocol;

namespace Praxis.Core.Hosting;

public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Event,
    string? Source,
    bool Success,
    string? Detail);

public sealed class AuditLog(PraxisRuntimeOptions runtime, ILogger<AuditLog> logger)
{
    private readonly ConcurrentQueue<AuditEntry> _recent = new();
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly string _logDirectory = Path.Combine(runtime.DataDirectory, "logs");

    public async Task WriteAsync(
        string eventName,
        string? source,
        bool success,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        var entry = new AuditEntry(DateTimeOffset.UtcNow, eventName, source, success, Limit(detail, 2_000));
        _recent.Enqueue(entry);
        while (_recent.Count > 500)
            _recent.TryDequeue(out _);

        var acquired = false;
        try
        {
            await _fileGate.WaitAsync(cancellationToken);
            acquired = true;
            Directory.CreateDirectory(_logDirectory);
            var path = Path.Combine(_logDirectory, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry, JsonDefaults.CompactOptions) + Environment.NewLine, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not write the Praxis audit log.");
        }
        finally
        {
            if (acquired)
                _fileGate.Release();
        }
    }

    public IReadOnlyList<AuditEntry> Recent(int take) => _recent.Reverse().Take(take).ToArray();

    private static string? Limit(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
