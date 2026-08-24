using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Praxis.Core.Protocol;

namespace Praxis.Core.Security;

public sealed class SecureSession
{
    private readonly object _gate = new();
    private byte[]? _key;
    private string? _sessionId;
    private ulong _outboundSequence;
    private ulong _inboundSequence;
    public event Action<bool>? StateChanged;

    public bool IsActive
    {
        get { lock (_gate) return _key is not null; }
    }

    public void Activate(byte[] key, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32 || string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A secure Praxis session requires a 256-bit key and session identifier.");
        lock (_gate)
        {
            ResetLocked();
            _key = key.ToArray();
            _sessionId = sessionId;
        }
        StateChanged?.Invoke(true);
    }

    public void Reset()
    {
        var changed = false;
        lock (_gate)
        {
            changed = _key is not null;
            ResetLocked();
        }
        if (changed) StateChanged?.Invoke(false);
    }

    public ProtocolMessage Wrap(ProtocolMessage message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.CompactOptions);
        lock (_gate)
        {
            var key = _key ?? throw new InvalidOperationException("Praxis secure session is not active.");
            var sessionId = _sessionId!;
            var sequence = ++_outboundSequence;
            var mac = ComputeMac(key, sessionId, sequence, body);
            return ProtocolMessage.Create("secure.message", new
            {
                sessionId,
                sequence,
                body = Encoding.UTF8.GetString(body),
                mac = Convert.ToBase64String(mac)
            });
        }
    }

    public bool TryUnwrap(ReadOnlySpan<byte> json, out ProtocolMessage? message, out string? error)
    {
        message = null;
        error = null;
        ProtocolMessage? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ProtocolMessage>(json, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            error = "The secure envelope is not valid JSON.";
            return false;
        }

        if (envelope is null || envelope.Type != "secure.message" ||
            !envelope.Payload.TryGetProperty("sessionId", out var sessionElement) ||
            !envelope.Payload.TryGetProperty("sequence", out var sequenceElement) ||
            !envelope.Payload.TryGetProperty("body", out var bodyElement) ||
            !envelope.Payload.TryGetProperty("mac", out var macElement) ||
            sessionElement.ValueKind != JsonValueKind.String ||
            !sequenceElement.TryGetUInt64(out var sequence) ||
            bodyElement.ValueKind != JsonValueKind.String ||
            macElement.ValueKind != JsonValueKind.String)
        {
            error = "The secure envelope is incomplete.";
            return false;
        }

        byte[] body;
        byte[] suppliedMac;
        try
        {
            body = Encoding.UTF8.GetBytes(bodyElement.GetString()!);
            suppliedMac = Convert.FromBase64String(macElement.GetString()!);
        }
        catch (FormatException)
        {
            error = "The secure envelope contains invalid base64 data.";
            return false;
        }

        lock (_gate)
        {
            var key = _key;
            var sessionId = _sessionId;
            if (key is null || sessionId is null ||
                !string.Equals(sessionElement.GetString(), sessionId, StringComparison.Ordinal) ||
                sequence <= _inboundSequence)
            {
                error = "The secure envelope belongs to an invalid or replayed session.";
                return false;
            }

            var expectedMac = ComputeMac(key, sessionId, sequence, body);
            if (suppliedMac.Length != expectedMac.Length ||
                !CryptographicOperations.FixedTimeEquals(suppliedMac, expectedMac))
            {
                error = "The secure envelope authentication code is invalid.";
                return false;
            }

            try
            {
                message = JsonSerializer.Deserialize<ProtocolMessage>(body, JsonDefaults.Options);
            }
            catch (JsonException)
            {
                error = "The authenticated Praxis message is invalid.";
                return false;
            }

            if (message is null || message.V != 1 || string.IsNullOrWhiteSpace(message.Id) ||
                message.Id.Length > 128 || string.IsNullOrWhiteSpace(message.Type) || message.Type.Length > 128)
            {
                message = null;
                error = "The authenticated Praxis message is unsupported.";
                return false;
            }

            _inboundSequence = sequence;
            return true;
        }
    }

    private static byte[] ComputeMac(byte[] key, string sessionId, ulong sequence, byte[] body)
    {
        var sessionBytes = Encoding.UTF8.GetBytes(sessionId);
        var input = new byte[sessionBytes.Length + 1 + sizeof(ulong) + body.Length];
        sessionBytes.CopyTo(input, 0);
        input[sessionBytes.Length] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(input.AsSpan(sessionBytes.Length + 1, sizeof(ulong)), sequence);
        body.CopyTo(input, sessionBytes.Length + 1 + sizeof(ulong));
        return PairingCrypto.Hmac(key, input);
    }

    private void ResetLocked()
    {
        if (_key is not null)
            CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _sessionId = null;
        _outboundSequence = 0;
        _inboundSequence = 0;
    }
}
