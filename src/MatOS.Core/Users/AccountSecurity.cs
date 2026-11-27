using MatOS.Core.Configuration;
using MatOS.Core.Entities;

namespace MatOS.Core.Users;

/// <summary>How a person signs in to MatOS, for the tabs Security and Password of their account.</summary>
public enum AccountSignIn
{
    /// <summary>There is no single sign-on: MatIdentity is not installed, or MatOS is not connected to it. MatOS signs people in itself, without a second step.</summary>
    NoSingleSignOn,

    /// <summary>The single sign-on is there, but this is an account of MatOS itself (not linked to a person of MatIdentity), with MatOS's own password.</summary>
    LocalAccount,

    /// <summary>The person signs in through the single sign-on (MatIdentity): its password and its second step.</summary>
    SingleSignOn,
}

/// <summary>
/// The second step and the password of one person (docs/ARCHITECTURE.md "MatIdentity, the single sign-on", "The second step"). The second step lives in
/// MatIdentity and only there: MatOS never builds one of its own. A person who signs in through it changes it, and their sign-in password, on MatIdentity's
/// own account pages (<see cref="SecurityUrl"/>, <see cref="PasswordUrl"/>, in MatOS's look through <c>?brand=</c>), opened in a new tab; MatOS's own password of such a person is only the way in
/// while MatIdentity does not answer.
/// </summary>
/// <param name="SignIn">How the person signs in.</param>
/// <param name="SecurityUrl">MatIdentity's page of the second step (authenticator app, recovery codes, passkeys), for <see cref="AccountSignIn.SingleSignOn"/> only.</param>
/// <param name="PasswordUrl">MatIdentity's page of the sign-in password, for <see cref="AccountSignIn.SingleSignOn"/> only.</param>
/// <param name="HasLocalPassword">Whether the account has a password of MatOS itself (a person who first came through MatIdentity has none).</param>
public sealed record AccountSecurity(AccountSignIn SignIn, string? SecurityUrl, string? PasswordUrl, bool HasLocalPassword)
{
    /// <summary>MatIdentity's page of the second step, below its public address.</summary>
    public const string SecurityPath = "/Account/Security";

    /// <summary>MatIdentity's tab of the password, below its public address.</summary>
    public const string PasswordPath = "/Account/Profile?tab=password";

    /// <summary>
    /// Whether the account page offers the form of MatOS's own password: for every account but one that signs in through MatIdentity and has no password of
    /// MatOS (it would ask for a current password that does not exist).
    /// </summary>
    public bool OffersLocalPassword => SignIn != AccountSignIn.SingleSignOn || HasLocalPassword;

    /// <summary>
    /// The state of <paramref name="user"/>: <paramref name="identity"/> is MatOS's sign-in through MatIdentity (<see cref="SystemConfig.Identity"/>, null
    /// while MatOS is not connected to it). An address of MatIdentity that is not an absolute http(s) address counts as no single sign-on: nothing to link to.
    /// </summary>
    public static AccountSecurity Of(IdentitySignIn? identity, User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var hasPassword = !string.IsNullOrEmpty(user.PasswordHash);
        if (BaseOf(identity?.Authority) is not { } authority)
        {
            return new AccountSecurity(AccountSignIn.NoSingleSignOn, null, null, hasPassword);
        }

        if (string.IsNullOrWhiteSpace(user.ExternalSubject))
        {
            return new AccountSecurity(AccountSignIn.LocalAccount, null, null, hasPassword);
        }

        // MatOS's brand (SelfBrand): MatIdentity shows the page in MatOS's look; one without that brand, or an older one, ignores it and shows its own account.
        var clientId = identity!.ClientId?.Trim();
        var brand = string.IsNullOrEmpty(clientId) ? null : "brand=" + Uri.EscapeDataString(clientId);
        return new AccountSecurity(
            AccountSignIn.SingleSignOn,
            authority + SecurityPath + (brand is null ? string.Empty : "?" + brand),
            authority + PasswordPath + (brand is null ? string.Empty : "&" + brand),
            hasPassword);
    }

    /// <summary>The public address of MatIdentity without a trailing slash, or null when it is not an absolute http(s) address.</summary>
    private static string? BaseOf(string? authority)
        => Uri.TryCreate(authority?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
}
