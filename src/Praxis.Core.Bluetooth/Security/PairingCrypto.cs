using System.Security.Cryptography;
using System.Text;

namespace Praxis.Core.Security;

public static class PairingCrypto
{
    public const int DefaultIterations = 50_000;

    public static byte[] DeriveCodeKey(string code, byte[] salt, int iterations = DefaultIterations)
    {
        if (code.Length != 6 || code.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("Pairing code must contain six digits.", nameof(code));
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(code),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);
    }

    public static byte[] Hmac(byte[] key, params string[] fields) =>
        HMACSHA256.HashData(key, Transcript(fields));

    public static byte[] Hmac(byte[] key, ReadOnlySpan<byte> data) =>
        HMACSHA256.HashData(key, data);

    public static byte[] Transcript(params string[] fields)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    public static string SessionId(params string[] fields)
    {
        var digest = SHA256.HashData(Transcript(fields));
        return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    public static bool VerifyBase64(byte[] expected, string? supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied))
            return false;
        try
        {
            var actual = Convert.FromBase64String(supplied);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
