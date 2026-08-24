using System.Text.Json;
using Praxis.Core.Hosting;
using Praxis.Core.Protocol;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace Praxis.Core.Security;

public sealed class PairingStore(PraxisRuntimeOptions runtime, ILogger<PairingStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(runtime.DataDirectory, "pairing.json");
    private PairingDocument _document = new(Guid.NewGuid().ToString("N"), []);
    private Dictionary<string, byte[]> _secrets = new(StringComparer.OrdinalIgnoreCase);

    public string ServerId => _document.ServerId;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path))
            {
                await using var stream = File.OpenRead(_path);
                _document = await JsonSerializer.DeserializeAsync<PairingDocument>(stream, JsonDefaults.Options, cancellationToken)
                    ?? throw new InvalidDataException("Praxis pairing store is empty.");
                if (string.IsNullOrWhiteSpace(_document.ServerId))
                    throw new InvalidDataException("Praxis pairing store has no server identity.");
            }

            _secrets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in _document.Devices)
            {
                try
                {
                    _secrets[device.DeviceId] = await UnprotectAsync(device.ProtectedSecret, cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Ignoring an unreadable Praxis pairing record for {DeviceId}.", device.DeviceId);
                }
            }

            if (!File.Exists(_path))
                await SaveLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryGetSecret(string deviceId, out byte[] secret)
    {
        lock (_secrets)
        {
            if (_secrets.TryGetValue(deviceId, out var stored))
            {
                secret = stored.ToArray();
                return true;
            }
        }
        secret = [];
        return false;
    }

    public async Task TrustAsync(string deviceId, string? name, byte[] secret, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var protectedSecret = await ProtectAsync(secret, cancellationToken);
            var devices = _document.Devices
                .Where(device => !string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                .Append(new PairingDevice(deviceId, name, protectedSecret, DateTimeOffset.UtcNow))
                .ToList();
            _document = _document with { Devices = devices };
            lock (_secrets) _secrets[deviceId] = secret.ToArray();
            await SaveLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ForgetAsync(string? deviceId = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _document = _document with
            {
                Devices = deviceId is null
                    ? []
                    : _document.Devices.Where(device => !string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToList()
            };
            lock (_secrets)
            {
                if (deviceId is null) _secrets.Clear();
                else _secrets.Remove(deviceId);
            }
            await SaveLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveLockedAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_document, JsonDefaults.Options), cancellationToken);
        File.Move(temporary, _path, true);
    }

    private static async Task<string> ProtectAsync(byte[] secret, CancellationToken cancellationToken)
    {
        var provider = new DataProtectionProvider("LOCAL=user");
        var protectedBuffer = await provider.ProtectAsync(CryptographicBuffer.CreateFromByteArray(secret)).AsTask(cancellationToken);
        CryptographicBuffer.CopyToByteArray(protectedBuffer, out var protectedBytes);
        return Convert.ToBase64String(protectedBytes);
    }

    private static async Task<byte[]> UnprotectAsync(string protectedSecret, CancellationToken cancellationToken)
    {
        var provider = new DataProtectionProvider();
        var buffer = CryptographicBuffer.CreateFromByteArray(Convert.FromBase64String(protectedSecret));
        var clearBuffer = await provider.UnprotectAsync(buffer).AsTask(cancellationToken);
        CryptographicBuffer.CopyToByteArray(clearBuffer, out var clearBytes);
        return clearBytes;
    }

    private sealed record PairingDocument(string ServerId, List<PairingDevice> Devices);
    private sealed record PairingDevice(string DeviceId, string? Name, string ProtectedSecret, DateTimeOffset PairedAt);
}
