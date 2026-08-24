namespace Praxis.Core.Bluetooth;

public sealed class BluetoothSessionReadiness
{
    private int _isReady;

    public bool IsReady => Volatile.Read(ref _isReady) == 1;

    public void MarkSynchronizing() => Interlocked.Exchange(ref _isReady, 0);

    public void MarkReady() => Interlocked.Exchange(ref _isReady, 1);
}

public sealed record BluetoothBootstrapPreparation(
    string? ConfigurationHash,
    Dictionary<string, string>? IconHashes);

public static class BluetoothBootstrapCachePlanner
{
    public static bool ShouldUseCachedFastPath(bool preparationProvided, int missingIconCount)
    {
        if (missingIconCount < 0)
            throw new ArgumentOutOfRangeException(nameof(missingIconCount));
        return preparationProvided && missingIconCount == 0;
    }

    public static IReadOnlyList<string> FindMissingIcons(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string>? cached)
    {
        cached ??= new Dictionary<string, string>();
        return current
            .Where(item => !cached.TryGetValue(item.Key, out var hash) ||
                           !string.Equals(item.Value, hash, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Key)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
