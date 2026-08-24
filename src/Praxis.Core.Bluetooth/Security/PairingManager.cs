using System.Security.Cryptography;
using System.Text.Json;
using Praxis.Core.Protocol;

namespace Praxis.Core.Security;

public sealed class PairingManager(PairingStore store, ILogger<PairingManager> logger)
{
    private readonly object _gate = new();
    private Handshake? _handshake;
    private string _pairingCode = NewCode();
    private DateTimeOffset _codeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

    public event Action? PairingCodeChanged;
    public string PairingCode
    {
        get
        {
            lock (_gate)
            {
                EnsureFreshCodeLocked();
                return _pairingCode;
            }
        }
    }

    public string FormattedPairingCode => PairingCode.Insert(3, " ");

    public ProtocolMessage Begin(string deviceId, string? deviceName)
    {
        byte[] serverNonce = RandomNumberGenerator.GetBytes(24);
        lock (_gate)
        {
            EnsureFreshCodeLocked();
            if (store.TryGetSecret(deviceId, out var secret))
            {
                _handshake = new Handshake(deviceId, deviceName, serverNonce, null, null, secret, 0, DateTimeOffset.UtcNow.AddMinutes(2));
                return ProtocolMessage.Create("pairing.resume", new
                {
                    serverId = store.ServerId,
                    deviceId,
                    serverNonce = Convert.ToBase64String(serverNonce)
                });
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            _handshake = new Handshake(deviceId, deviceName, serverNonce, salt, _pairingCode, null, 0, DateTimeOffset.UtcNow.AddMinutes(2));
            return ChallengeLocked(_handshake);
        }
    }

    public async Task<PairingOutcome> ProcessAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        Handshake handshake;
        lock (_gate)
        {
            if (_handshake is null)
                return PairingOutcome.Failed(Failure("当前没有有效的配对会话，请等待重新连接。", 0));
            handshake = _handshake;
            if (DateTimeOffset.UtcNow > handshake.ExpiresAt)
                return PairingOutcome.Failed(Failure("配对会话已过期，请重新连接。", 0));
        }

        return message.Type switch
        {
            "pairing.request" => await CompleteNewPairingAsync(handshake, message, cancellationToken),
            "pairing.resumeProof" => CompleteResume(handshake, message),
            "pairing.reset" => await ResetPairingAsync(handshake, cancellationToken),
            _ => PairingOutcome.Failed(Failure("完成配对前不能执行控制指令。", Math.Max(0, 3 - handshake.Attempts)))
        };
    }

    public void Abort()
    {
        lock (_gate) _handshake = null;
    }

    public void RotateCode()
    {
        lock (_gate)
        {
            _pairingCode = NewCode();
            _codeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            _handshake = null;
        }
        PairingCodeChanged?.Invoke();
    }

    public Task ForgetAllAsync(CancellationToken cancellationToken = default)
    {
        Abort();
        RotateCode();
        return store.ForgetAsync(null, cancellationToken);
    }

    private async Task<PairingOutcome> CompleteNewPairingAsync(
        Handshake handshake,
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        if (handshake.Salt is null || handshake.PairingCode is null || handshake.RootSecret is not null ||
            !TryString(message.Payload, "deviceId", out var deviceId) ||
            !TryBytes(message.Payload, "clientNonce", out var clientNonce) ||
            !TryString(message.Payload, "proof", out var proof) ||
            !string.Equals(deviceId, handshake.DeviceId, StringComparison.OrdinalIgnoreCase))
            return RegisterFailure(handshake, "配对请求格式无效。");

        var codeKey = PairingCrypto.DeriveCodeKey(handshake.PairingCode, handshake.Salt);
        var serverNonce = Convert.ToBase64String(handshake.ServerNonce);
        var clientNonceText = Convert.ToBase64String(clientNonce);
        var expected = PairingCrypto.Hmac(codeKey, "pair-v1", store.ServerId, handshake.DeviceId, serverNonce, clientNonceText);
        if (!PairingCrypto.VerifyBase64(expected, proof))
        {
            CryptographicOperations.ZeroMemory(codeKey);
            return RegisterFailure(handshake, "配对码不正确。");
        }

        var root = PairingCrypto.Hmac(codeKey, "root-v1", store.ServerId, handshake.DeviceId, serverNonce, clientNonceText);
        CryptographicOperations.ZeroMemory(codeKey);
        await store.TrustAsync(handshake.DeviceId, handshake.DeviceName, root, cancellationToken);
        var activation = CreateActivation(root, handshake, clientNonceText);
        var serverProof = PairingCrypto.Hmac(root, "server-v1", store.ServerId, handshake.DeviceId, serverNonce, clientNonceText);
        lock (_gate) _handshake = null;
        logger.LogInformation("Praxis Panel {DeviceId} paired successfully.", handshake.DeviceId);
        return PairingOutcome.Succeeded(ProtocolMessage.Create("pairing.completed", new
        {
            serverId = store.ServerId,
            deviceId = handshake.DeviceId,
            sessionId = activation.SessionId,
            serverProof = Convert.ToBase64String(serverProof)
        }, message.Id), activation);
    }

    private PairingOutcome CompleteResume(Handshake handshake, ProtocolMessage message)
    {
        if (handshake.RootSecret is null ||
            !TryString(message.Payload, "deviceId", out var deviceId) ||
            !TryBytes(message.Payload, "clientNonce", out var clientNonce) ||
            !TryString(message.Payload, "proof", out var proof) ||
            !string.Equals(deviceId, handshake.DeviceId, StringComparison.OrdinalIgnoreCase))
            return RegisterFailure(handshake, "恢复配对请求格式无效。");

        var serverNonce = Convert.ToBase64String(handshake.ServerNonce);
        var clientNonceText = Convert.ToBase64String(clientNonce);
        var expected = PairingCrypto.Hmac(handshake.RootSecret, "resume-client-v1", store.ServerId, handshake.DeviceId, serverNonce, clientNonceText);
        if (!PairingCrypto.VerifyBase64(expected, proof))
            return RegisterFailure(handshake, "无法验证已配对的 iPad。");

        var activation = CreateActivation(handshake.RootSecret, handshake, clientNonceText);
        var serverProof = PairingCrypto.Hmac(handshake.RootSecret, "resume-server-v1", store.ServerId, handshake.DeviceId, serverNonce, clientNonceText);
        lock (_gate) _handshake = null;
        return PairingOutcome.Succeeded(ProtocolMessage.Create("pairing.resumed", new
        {
            serverId = store.ServerId,
            deviceId = handshake.DeviceId,
            sessionId = activation.SessionId,
            serverProof = Convert.ToBase64String(serverProof)
        }, message.Id), activation);
    }

    private async Task<PairingOutcome> ResetPairingAsync(Handshake handshake, CancellationToken cancellationToken)
    {
        await store.ForgetAsync(handshake.DeviceId, cancellationToken);
        return PairingOutcome.Pending(Begin(handshake.DeviceId, handshake.DeviceName));
    }

    private PairingOutcome RegisterFailure(Handshake handshake, string error)
    {
        var codeChanged = false;
        PairingOutcome outcome;
        lock (_gate)
        {
            var attempts = handshake.Attempts + 1;
            _handshake = handshake with { Attempts = attempts };
            if (attempts >= 3)
            {
                _pairingCode = NewCode();
                _codeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
                _handshake = null;
                codeChanged = true;
                outcome = PairingOutcome.Failed(Failure("配对尝试次数过多，配对码已更新。", 0));
            }
            else
            {
                outcome = PairingOutcome.Failed(Failure(error, 3 - attempts));
            }
        }

        if (codeChanged) PairingCodeChanged?.Invoke();
        return outcome;
    }

    private ProtocolMessage ChallengeLocked(Handshake handshake) => ProtocolMessage.Create("pairing.challenge", new
    {
        serverId = store.ServerId,
        serverName = Environment.MachineName,
        deviceId = handshake.DeviceId,
        serverNonce = Convert.ToBase64String(handshake.ServerNonce),
        salt = Convert.ToBase64String(handshake.Salt!),
        iterations = PairingCrypto.DefaultIterations,
        expiresInSeconds = 120
    });

    private static SessionActivation CreateActivation(byte[] root, Handshake handshake, string clientNonce)
    {
        var serverNonce = Convert.ToBase64String(handshake.ServerNonce);
        var sessionId = PairingCrypto.SessionId("session-id-v1", handshake.DeviceId, serverNonce, clientNonce);
        var key = PairingCrypto.Hmac(root, "session-key-v1", sessionId, handshake.DeviceId, serverNonce, clientNonce);
        return new SessionActivation(key, sessionId, handshake.DeviceId);
    }

    private static ProtocolMessage Failure(string error, int attemptsRemaining) =>
        ProtocolMessage.Create("pairing.failed", new { error, attemptsRemaining });

    private void EnsureFreshCodeLocked()
    {
        if (DateTimeOffset.UtcNow <= _codeExpiresAt) return;
        _pairingCode = NewCode();
        _codeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        PairingCodeChanged?.Invoke();
    }

    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    private static bool TryString(JsonElement payload, string name, out string value)
    {
        value = string.Empty;
        return payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = element.GetString()!);
    }
    private static bool TryBytes(JsonElement payload, string name, out byte[] value)
    {
        value = [];
        if (!TryString(payload, name, out var encoded)) return false;
        try { value = Convert.FromBase64String(encoded); return value.Length is >= 16 and <= 64; }
        catch (FormatException) { return false; }
    }

    private sealed record Handshake(
        string DeviceId,
        string? DeviceName,
        byte[] ServerNonce,
        byte[]? Salt,
        string? PairingCode,
        byte[]? RootSecret,
        int Attempts,
        DateTimeOffset ExpiresAt);
}

public sealed record SessionActivation(byte[] Key, string SessionId, string DeviceId);
public sealed record PairingOutcome(ProtocolMessage Response, SessionActivation? Activation, bool IsFailure)
{
    public static PairingOutcome Pending(ProtocolMessage response) => new(response, null, false);
    public static PairingOutcome Failed(ProtocolMessage response) => new(response, null, true);
    public static PairingOutcome Succeeded(ProtocolMessage response, SessionActivation activation) => new(response, activation, false);
}
