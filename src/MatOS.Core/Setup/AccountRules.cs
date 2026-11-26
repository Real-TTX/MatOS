using System.Text.RegularExpressions;

namespace MatOS.Core.Setup;

/// <summary>
/// The rules for the account of the first start (<c>/Setup</c>): the e-mail address is the user name, and the password is one that MatIdentity takes as well
/// (its <c>PasswordPolicy</c>: 8 to 128 characters, none of the passwords tried first, not containing the user name or the e-mail address). So the same e-mail
/// and password sign in to MatIdentity later (<c>OwnerHandover</c>), and the person never needs to know anything new.
/// </summary>
public static partial class AccountRules
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    /// <summary>The ones tried first (the list of MatIdentity's PasswordPolicy).</summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passwort", "passwort1", "12345678", "123456789", "1234567890", "qwertyui", "qwertyuiop", "qwertz123",
        "iloveyou", "sunshine", "princess", "football", "baseball", "welcome1", "welcome123", "admin123", "administrator", "letmein1", "abc12345",
        "11111111", "00000000", "87654321", "qwerty123", "1q2w3e4r", "1qaz2wsx", "zaq12wsx", "superman", "starwars", "dragon12", "monkey12", "changeme",
        "hallo123", "schatz123", "matidentity", "matos123", "mattiverse",
    };

    /// <summary>The e-mail address as it is kept (trimmed, lower case), or null when it is not one.</summary>
    public static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        return value is { Length: > 0 and <= 254 } && EmailPattern().IsMatch(value) ? value : null;
    }

    /// <summary>Why the password cannot be used, in plain words, or null when it can.</summary>
    public static string? PasswordProblem(string? password, string? email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            return $"Use at least {MinLength} characters.";
        }

        if (password.Length > MaxLength)
        {
            return $"Use at most {MaxLength} characters.";
        }

        if (Common.Contains(password))
        {
            return "This is one of the passwords tried first. Choose another one.";
        }

        var address = email?.Trim();
        var local = address?.Split('@')[0];
        if ((!string.IsNullOrEmpty(address) && password.Contains(address, StringComparison.OrdinalIgnoreCase))
            || (local is { Length: >= 4 } && password.Contains(local, StringComparison.OrdinalIgnoreCase)))
        {
            return "The password must not contain your e-mail address.";
        }

        return null;
    }

    [GeneratedRegex(@"^[a-z0-9.!#$%&*+/=?^_`{|}~-]+@[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex EmailPattern();
}
