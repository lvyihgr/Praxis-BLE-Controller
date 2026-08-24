using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Praxis.Core.Protocol;

namespace Praxis.Core.Configuration;

public static class ConfigurationSnapshot
{
    public static string ComputeHash(PraxisConfiguration configuration) =>
        Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(configuration, JsonDefaults.CompactOptions)))
            .ToLowerInvariant();

    public static JsonObject CreatePayload(PraxisConfiguration configuration, string? hash = null)
    {
        var payload = JsonSerializer.SerializeToNode(configuration, JsonDefaults.Options)?.AsObject()
                      ?? throw new InvalidOperationException("Praxis configuration could not be serialized.");
        payload["configurationHash"] = hash ?? ComputeHash(configuration);
        return payload;
    }
}
