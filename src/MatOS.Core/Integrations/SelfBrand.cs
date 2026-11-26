using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MatOS.Core.Integrations;

/// <summary>
/// The look MatOS gives its own sign-in at MatIdentity (MatIdentity docs/ARCHITECTURE.md "Branded sign-in"; docs/ARCHITECTURE.md "MatOS's sign-in looks like MatOS"):
/// the instance name, the accent, the planet of the logo and the default wallpaper, and <c>hideIdentity</c>, so a person who signs in to MatOS sees MatOS only.
/// </summary>
public sealed record SelfBrand(string Name, string Accent, byte[] Logo, string LogoType, byte[]? Background, string? BackgroundType);

/// <summary>Where the pictures of <see cref="SelfBrand"/> come from: the web project registers its accent and its files under <c>wwwroot</c> (read once).</summary>
public sealed class SelfBrandAssets
{
    private readonly Lazy<byte[]?> _logo;
    private readonly Lazy<byte[]?> _background;

    public SelfBrandAssets(string accent, string? logoPath, string? backgroundPath)
    {
        Accent = accent;
        _logo = new Lazy<byte[]?>(() => Read(logoPath));
        _background = new Lazy<byte[]?>(() => Read(backgroundPath));
    }

    public string Accent { get; }

    /// <summary>The brand for the instance name <paramref name="name"/>, or null when the logo cannot be read (then MatOS sends no brand).</summary>
    public SelfBrand? For(string? name)
    {
        if (_logo.Value is not { Length: > 0 } logo)
        {
            return null;
        }

        var background = _background.Value is { Length: > 0 } b ? b : null;
        return new SelfBrand(IdentityBranding.NameOf(name), Accent, logo, "image/svg+xml", background, background is null ? null : "image/jpeg");
    }

    private static byte[]? Read(string? path) => path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
}

/// <summary>
/// The rules of the brand MatOS sends (pure, unit-tested): whether MatIdentity knows brands at all, and what has to be sent so that its brand of MatOS is
/// <see cref="SelfBrand"/>. A MatIdentity that knows brands answers the field <c>branding</c> (an object or null) for a client; an older one does not have
/// it, and MatOS sends nothing (that is the gate: <c>/api/v1/status</c> stays at <c>apiVersion: 1</c>). The pictures are sent only when MatIdentity has
/// another one: the <c>?v=</c> of its addresses is the first 16 hex digits of the SHA-256 of the bytes.
/// </summary>
public static class IdentityBranding
{
    /// <summary>The longest name MatIdentity takes.</summary>
    public const int MaxName = 60;

    /// <summary>The instance name as a brand name: trimmed, at most <see cref="MaxName"/> characters, MatOS when empty.</summary>
    public static string NameOf(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "MatOS" : name.Trim();
        return trimmed.Length > MaxName ? trimmed[..MaxName] : trimmed;
    }

    /// <summary>Whether the client as MatIdentity answered it (<c>GET /api/v1/clients/{id}</c>) carries the field <c>branding</c>: only then does MatIdentity know brands.</summary>
    public static bool Supported(JsonNode? client) => client is JsonObject o && o.ContainsKey("branding");

    /// <summary>The version MatIdentity gives a picture: the first 16 hex digits of its SHA-256.</summary>
    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data))[..16];

    /// <summary>
    /// The <c>branding</c> of a PUT that makes MatIdentity's brand (<paramref name="current"/>, the field <c>branding</c> of its answer, null when there is none)
    /// <paramref name="brand"/>, or null when it is that already. A picture MatIdentity has in the same version is left out (missing keeps it); one MatOS has no
    /// more is removed (null).
    /// </summary>
    public static JsonObject? Payload(SelfBrand brand, JsonNode? current)
    {
        var same = current is JsonObject c
            && (string?)c["name"] == brand.Name
            && string.Equals((string?)c["accent"], brand.Accent, StringComparison.OrdinalIgnoreCase)
            && c["hideIdentity"] is JsonValue hide && hide.TryGetValue<bool>(out var hidden) && hidden;
        var logoSame = HasPicture(current, "logo", brand.Logo, brand.LogoType);
        var backgroundSame = brand.Background is null
            ? current is not JsonObject || current["hasBackground"] is not JsonValue has || !has.TryGetValue<bool>(out var hasBackground) || !hasBackground
            : HasPicture(current, "background", brand.Background, brand.BackgroundType!);
        if (same && logoSame && backgroundSame)
        {
            return null;
        }

        var payload = new JsonObject
        {
            ["name"] = brand.Name,
            ["accent"] = brand.Accent,
            ["hideIdentity"] = true,
        };
        if (!logoSame)
        {
            payload["logo"] = Picture(brand.LogoType, brand.Logo);
        }

        if (!backgroundSame)
        {
            payload["background"] = brand.Background is null ? null : Picture(brand.BackgroundType!, brand.Background);
        }

        return payload;
    }

    private static JsonObject Picture(string contentType, byte[] data) => new() { ["contentType"] = contentType, ["data"] = Convert.ToBase64String(data) };

    /// <summary>Whether MatIdentity has this picture already: the same content type and the version of its address (<c>logoUrl</c>, <c>backgroundUrl</c>).</summary>
    private static bool HasPicture(JsonNode? current, string part, byte[] data, string contentType)
    {
        if (current is not JsonObject c || (string?)c[part + "ContentType"] != contentType || (string?)c[part + "Url"] is not { } url)
        {
            return false;
        }

        var at = url.IndexOf("v=", StringComparison.Ordinal);
        if (at < 0 || (at > 0 && url[at - 1] is not ('?' or '&')))
        {
            return false;
        }

        var version = url[(at + 2)..];
        var end = version.IndexOf('&', StringComparison.Ordinal);
        return string.Equals(end < 0 ? version : version[..end], Hash(data), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What became of the brand of MatOS at MatIdentity.</summary>
public enum BrandingOutcome
{
    /// <summary>This MatIdentity does not know brands (an older one): nothing was sent.</summary>
    Unsupported,

    /// <summary>MatIdentity has the brand already: nothing was sent.</summary>
    Unchanged,

    /// <summary>The brand was sent.</summary>
    Sent,
}
