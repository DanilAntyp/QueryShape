using System.Security.Cryptography;
using System.Text;

namespace QueryShape.Normalization;

/// <summary>Short, stable hashes used in snapshots and telemetry.</summary>
public static class Fingerprint
{
    /// <summary>Number of hex characters kept from the SHA-256 digest.</summary>
    public const int Length = 12;

    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="text"/>, first 12 lowercase hex characters.</summary>
    public static string Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return Convert.ToHexString(hash[..(Length / 2)]).ToLowerInvariant();
    }
}
