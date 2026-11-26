using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MatOS.Core.Auth;

/// <summary>
/// A logout token of MatIdentity (OpenID Connect Back-Channel Logout 1.0) once it has been checked: whose sessions end. <see cref="Subject"/> alone means every
/// session of the person (MatIdentity signed them out everywhere). The same checks as MatPlatform's <c>MatIdentityLogoutToken</c> (MatDesign
/// <c>src/MatPlatform</c>); MatOS does not use MatPlatform, so they are written here once more.
/// </summary>
public sealed record LogoutToken(string? Subject, string? SessionId)
{
    /// <summary>The member of the <c>events</c> claim that makes a JWT a logout token.</summary>
    public const string EventType = "http://schemas.openid.net/event/backchannel-logout";

    /// <summary>The <c>typ</c> of the header MatIdentity writes; an ID token or an access token is refused.</summary>
    public const string TokenType = "logout+jwt";

    /// <summary>How old a logout token may be (its <c>iat</c>); its <c>jti</c> is remembered for as long.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    /// <summary>How far the clocks of MatIdentity and MatOS may differ.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Checks <paramref name="token"/>: the signature with the keys of the discovery document (<paramref name="configuration"/>, the one of the OpenID
    /// Connect handler; read again once for an unknown key), <c>typ</c>, <c>iss</c> = the issuer of the document, <c>aud</c> contains
    /// <paramref name="clientId"/>, <c>iat</c> within <see cref="MaxAge"/>, <c>exp</c> when present, the <c>events</c> claim with <see cref="EventType"/>,
    /// <c>sub</c> or <c>sid</c>, no <c>nonce</c>, and a <c>jti</c> <paramref name="replay"/> has not seen (remembered only when all else holds).
    /// </summary>
    public static async Task<LogoutTokenCheck> ValidateAsync(
        string? token,
        IConfigurationManager<OpenIdConnectConfiguration> configuration,
        string clientId,
        LogoutReplayCache replay,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentNullException.ThrowIfNull(replay);

        if (string.IsNullOrWhiteSpace(token))
        {
            return LogoutTokenCheck.Refused("The form field logout_token is missing.");
        }

        OpenIdConnectConfiguration document;
        try
        {
            document = await configuration.GetConfigurationAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LogoutTokenCheck.Refused("The keys of MatIdentity could not be read: " + ex.Message);
        }

        if (string.IsNullOrEmpty(document?.Issuer))
        {
            return LogoutTokenCheck.Refused("The discovery document of MatIdentity names no issuer.");
        }

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = await handler.ValidateTokenAsync(token, Parameters(document, clientId));
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // MatIdentity may have a new key: read the document once more.
            configuration.RequestRefresh();
            document = await configuration.GetConfigurationAsync(ct);
            if (!string.IsNullOrEmpty(document?.Issuer))
            {
                result = await handler.ValidateTokenAsync(token, Parameters(document, clientId));
            }
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return LogoutTokenCheck.Refused("The token is not valid: " + (result.Exception?.Message ?? "unknown reason"));
        }

        if (!string.IsNullOrEmpty(jwt.Typ)
            && !string.Equals(jwt.Typ, TokenType, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(jwt.Typ, "application/" + TokenType, StringComparison.OrdinalIgnoreCase))
        {
            return LogoutTokenCheck.Refused($"The token is of the type {jwt.Typ}, not {TokenType}.");
        }

        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
        var root = payload.RootElement;

        if (!root.TryGetProperty("iat", out var iatValue) || iatValue.ValueKind != JsonValueKind.Number || !iatValue.TryGetInt64(out var iat))
        {
            return LogoutTokenCheck.Refused("The token has no iat.");
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat);
        if (issuedAt > now + ClockSkew)
        {
            return LogoutTokenCheck.Refused("The token was issued in the future (iat).");
        }

        if (issuedAt < now - MaxAge - ClockSkew)
        {
            return LogoutTokenCheck.Refused("The token is too old (iat).");
        }

        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Object
            || !events.TryGetProperty(EventType, out var logoutEvent) || logoutEvent.ValueKind != JsonValueKind.Object)
        {
            return LogoutTokenCheck.Refused("The token has no events claim with " + EventType + ".");
        }

        if (root.TryGetProperty("nonce", out _))
        {
            return LogoutTokenCheck.Refused("A logout token must not have a nonce.");
        }

        var subject = Text(root, "sub");
        var sessionId = Text(root, "sid");
        if (subject is null && sessionId is null)
        {
            return LogoutTokenCheck.Refused("The token names neither sub nor sid.");
        }

        if (Text(root, "jti") is not { } jti)
        {
            return LogoutTokenCheck.Refused("The token has no jti.");
        }

        if (!replay.TryRemember(jwt.Issuer + " " + jti, now + MaxAge + ClockSkew + ClockSkew, now))
        {
            return LogoutTokenCheck.Refused("The token was used before (jti).");
        }

        return new LogoutTokenCheck(new LogoutToken(subject, sessionId), null);
    }

    private static TokenValidationParameters Parameters(OpenIdConnectConfiguration document, string clientId) => new()
    {
        ValidIssuer = document.Issuer,
        ValidateIssuer = true,
        ValidAudience = clientId,
        ValidateAudience = true,
        IssuerSigningKeys = document.SigningKeys,
        RequireSignedTokens = true,
        // exp is checked when MatIdentity writes it (it does: two minutes); iat by the window above.
        ValidateLifetime = true,
        RequireExpirationTime = false,
        ClockSkew = ClockSkew,
        ValidateTokenReplay = false,
    };

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;
}

/// <summary>The outcome of <see cref="LogoutToken.ValidateAsync"/>: the token, or why it was refused.</summary>
public sealed record LogoutTokenCheck(LogoutToken? Token, string? Error)
{
    public bool IsValid => Token is not null;

    public static LogoutTokenCheck Refused(string error) => new(null, error);
}

/// <summary>The <c>jti</c> of the logout tokens already used, in memory, each until the token could no longer pass the <c>iat</c> window (a singleton).</summary>
public sealed class LogoutReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>Remembers <paramref name="id"/> until <paramref name="until"/>; false when it is already remembered (a replay).</summary>
    public bool TryRemember(string id, DateTimeOffset until, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        foreach (var (key, expiry) in _seen)
        {
            if (expiry <= now)
            {
                _seen.TryRemove(key, out _);
            }
        }

        return _seen.TryAdd(id, until);
    }

    /// <summary>How many ids are remembered.</summary>
    public int Count => _seen.Count;
}
