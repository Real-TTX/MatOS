using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MatOS.Services.Instances;

/// <summary>
/// The key of an installation: 32 random bytes, base64url, with the prefix <c>mos_</c> (so a leaked key is recognisable in logs and
/// by secret scanners). The service keeps only its SHA-256: a stolen database opens no tunnel. A plain hash is enough because the key
/// has 256 bits of entropy; a slow password hash would only cost time on every tunnel stream.
/// </summary>
public static class InstanceKeys
{
    public const string Prefix = "mos_";

    /// <summary>Longer input is not a key; bounding it keeps a hostile header from being hashed at length.</summary>
    private const int MaxKeyLength = 128;

    public static string Create() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool LooksLikeKey(string? key) =>
        key is not null && key.Length <= MaxKeyLength && key.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Compares in constant time, so the answer of <c>/nic/update</c> does not tell how much of a guessed key was right.</summary>
    public static bool Matches(string key, string hash) =>
        LooksLikeKey(key) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(key)), Encoding.ASCII.GetBytes(hash));
}
