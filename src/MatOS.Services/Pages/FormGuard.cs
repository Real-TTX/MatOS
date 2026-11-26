using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MatOS.Services.Pages;

/// <summary>Why a form was refused.</summary>
public enum FormProblem
{
    None,
    Expired,
    Invalid,
    Used,
    NoWork,
}

/// <summary>
/// The guards of the public page's forms, without cookies and without a third party: a token the service signed (it carries the time it
/// was issued and can be used once), a proof of work on it (<see cref="Configuration.ServicesOptions.ProofOfWorkBits"/>) so registering
/// names in bulk costs a script real time, and the same-origin check that stands in for an antiforgery cookie.
/// </summary>
public sealed class FormGuard(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private const int PayloadLength = 8 + 16;
    private const int MacLength = 16;

    // A restart forgets it: forms that were open then are refused once with "the page was too old", which is all it costs.
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    public string Issue()
    {
        var token = new byte[PayloadLength + MacLength];
        BinaryPrimitives.WriteInt64BigEndian(token, time.GetUtcNow().ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(token.AsSpan(8, 16));
        HMACSHA256.HashData(_secret, token.AsSpan(0, PayloadLength)).AsSpan(0, MacLength).CopyTo(token.AsSpan(PayloadLength));
        return Base64Url.EncodeToString(token);
    }

    /// <summary>Checks the token and the work on it; a good token is used up.</summary>
    public FormProblem Redeem(string? token, string? nonce, int bits)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64)
        {
            return FormProblem.Invalid;
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(token);
        }
        catch (FormatException)
        {
            return FormProblem.Invalid;
        }

        if (bytes.Length != PayloadLength + MacLength)
        {
            return FormProblem.Invalid;
        }

        var mac = HMACSHA256.HashData(_secret, bytes.AsSpan(0, PayloadLength)).AsSpan(0, MacLength);
        if (!CryptographicOperations.FixedTimeEquals(mac, bytes.AsSpan(PayloadLength)))
        {
            return FormProblem.Invalid;
        }

        var issued = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(bytes));
        var now = time.GetUtcNow();
        if (now - issued > Lifetime || issued - now > TimeSpan.FromMinutes(1))
        {
            return FormProblem.Expired;
        }

        if (!HasWork(token, nonce, bits))
        {
            return FormProblem.NoWork;
        }

        Sweep(now);
        return _used.TryAdd(token, issued + Lifetime) ? FormProblem.None : FormProblem.Used;
    }

    /// <summary>Whether SHA-256 of <c>token:nonce</c> starts with <paramref name="bits"/> zero bits (what the page's script searches for).</summary>
    public static bool HasWork(string token, string? nonce, int bits)
    {
        if (bits <= 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(nonce) || nonce.Length > 20 || !nonce.All(char.IsAsciiDigit))
        {
            return false;
        }

        return LeadingZeroBits(SHA256.HashData(Encoding.UTF8.GetBytes(token + ":" + nonce))) >= bits;
    }

    /// <summary>Finds a nonce (the tests do the browser's part with it).</summary>
    public static string Solve(string token, int bits)
    {
        for (long n = 0; ; n++)
        {
            var nonce = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (HasWork(token, nonce, bits))
            {
                return nonce;
            }
        }
    }

    public static int LeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        var zeros = 0;
        foreach (var b in hash)
        {
            if (b == 0)
            {
                zeros += 8;
                continue;
            }

            return zeros + System.Numerics.BitOperations.LeadingZeroCount((uint)b) - 24;
        }

        return zeros;
    }

    /// <summary>
    /// Whether a POST comes from a page of this very host: <c>Origin</c> (or, without it, <c>Referer</c>) must name the host the request is
    /// for. Browsers send <c>Origin</c> with every POST, so a form on another site cannot submit here in somebody's name.
    /// </summary>
    public static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            origin = request.Headers.Referer.ToString();
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https"
               && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private void Sweep(DateTimeOffset now)
    {
        if (_used.Count < 1000)
        {
            return;
        }

        foreach (var (token, until) in _used)
        {
            if (until < now)
            {
                _used.TryRemove(token, out _);
            }
        }
    }
}
