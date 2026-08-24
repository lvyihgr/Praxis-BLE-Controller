using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Praxis.Core.Hosting;
using Praxis.Core.Protocol;
using Praxis.Core.Security;

internal static class SecuritySelfTests
{
    public static Task CryptographicVectorsMatchAsync()
    {
        var derived = PairingCrypto.DeriveCodeKey(
            "123456",
            Encoding.UTF8.GetBytes("salt"),
            1);
        var expected = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes("123456"),
            Encoding.UTF8.GetBytes("salt"),
            1,
            HashAlgorithmName.SHA256,
            32);
        Assert(derived.SequenceEqual(expected), "PBKDF2-HMAC-SHA256 test vector mismatch.");

        var hmac = PairingCrypto.Hmac(
            Encoding.UTF8.GetBytes("key"),
            Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog"));
        Assert(Convert.ToHexString(hmac).Equals(
            "F7BC83F430538424B13298E6AA6FB143EF4D59A14946175997479DBC2D1A3CD8",
            StringComparison.Ordinal), "HMAC-SHA256 test vector mismatch.");
        return Task.CompletedTask;
    }

    public static Task SecureEnvelopeRejectsTamperAndReplayAsync()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var sender = new SecureSession();
        var receiver = new SecureSession();
        sender.Activate(key, "session-test");
        receiver.Activate(key, "session-test");
        var inner = ProtocolMessage.Create("ping", new { value = 7 }, "command-1");
        var wrapped = sender.Wrap(inner);
        var json = JsonSerializer.SerializeToUtf8Bytes(wrapped, JsonDefaults.CompactOptions);
        Assert(receiver.TryUnwrap(json, out var restored, out _), "Authenticated envelope did not round-trip.");
        Assert(restored?.Type == "ping" && restored.Id == "command-1", "Authenticated message changed during round-trip.");
        Assert(wrapped.Payload.GetProperty("body").GetString()?.Contains("\"type\":\"ping\"") == true,
            "Secure envelope should preserve compact UTF-8 JSON without base64 expansion.");
        Assert(!receiver.TryUnwrap(json, out _, out _), "Replayed envelope was accepted.");

        var tamperedJson = JsonSerializer.SerializeToUtf8Bytes(
            wrapped with
            {
                Payload = JsonSerializer.SerializeToElement(new
                {
                    sessionId = "session-test",
                    sequence = 2,
                    body = wrapped.Payload.GetProperty("body").GetString(),
                    mac = Convert.ToBase64String(new byte[32])
                }, JsonDefaults.Options)
            },
            JsonDefaults.CompactOptions);
        Assert(!receiver.TryUnwrap(tamperedJson, out _, out _), "Tampered envelope was accepted.");
        return Task.CompletedTask;
    }

    public static async Task PairingAndResumeRoundTripAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "praxis-pairing-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtime = new PraxisRuntimeOptions { DataDirectory = root, ServerName = "Test", Port = 0, EnableTrayIcon = false };
            var store = new PairingStore(runtime, NullLogger<PairingStore>.Instance);
            await store.InitializeAsync();
            var manager = new PairingManager(store, NullLogger<PairingManager>.Instance);
            var challenge = manager.Begin("ipad-test", "Test iPad");
            Assert(challenge.Type == "pairing.challenge", "First connection did not request pairing.");

            var clientNonce = Convert.ToBase64String(Enumerable.Range(1, 24).Select(value => (byte)value).ToArray());
            var serverNonce = challenge.Payload.GetProperty("serverNonce").GetString()!;
            var serverId = challenge.Payload.GetProperty("serverId").GetString()!;
            var salt = Convert.FromBase64String(challenge.Payload.GetProperty("salt").GetString()!);
            var key = PairingCrypto.DeriveCodeKey(manager.PairingCode, salt);
            var proof = PairingCrypto.Hmac(key, "pair-v1", serverId, "ipad-test", serverNonce, clientNonce);
            var request = ProtocolMessage.Create("pairing.request", new
            {
                deviceId = "ipad-test",
                clientNonce,
                proof = Convert.ToBase64String(proof)
            });
            var paired = await manager.ProcessAsync(request, CancellationToken.None);
            Assert(paired.Activation is not null && paired.Response.Type == "pairing.completed", "Pairing did not activate a secure session.");

            var resume = manager.Begin("ipad-test", "Test iPad");
            Assert(resume.Type == "pairing.resume", "Trusted device did not use automatic resume.");
            var rootSecret = PairingCrypto.Hmac(key, "root-v1", serverId, "ipad-test", serverNonce, clientNonce);
            var resumeServerNonce = resume.Payload.GetProperty("serverNonce").GetString()!;
            var resumeClientNonce = Convert.ToBase64String(Enumerable.Range(31, 24).Select(value => (byte)value).ToArray());
            var resumeProof = PairingCrypto.Hmac(rootSecret, "resume-client-v1", serverId, "ipad-test", resumeServerNonce, resumeClientNonce);
            var resumed = await manager.ProcessAsync(ProtocolMessage.Create("pairing.resumeProof", new
            {
                deviceId = "ipad-test",
                clientNonce = resumeClientNonce,
                proof = Convert.ToBase64String(resumeProof)
            }), CancellationToken.None);
            Assert(resumed.Activation is not null && resumed.Response.Type == "pairing.resumed", "Trusted session did not resume.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
