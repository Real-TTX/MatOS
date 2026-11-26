namespace MatOS.Tests;

/// <summary>
/// The session cookie keeps what it carries when MatOS writes it again (a new theme, wallpaper or password): "stay signed in" and the id token of a sign-in
/// through MatIdentity, which the sign-out sends as <c>id_token_hint</c>. Without it MatIdentity asks on a page of its own (docs/ENGINES.md "MatIdentity").
/// Checked on the source of the web project: every sign-in of the cookie passes its properties, and a refresh goes through <c>SessionCookie.RefreshAsync</c>.
/// </summary>
public class SessionCookieTests
{
    [Fact]
    public void Every_write_of_the_session_cookie_passes_its_properties()
    {
        var web = WebUiTests.WebRoot();
        var found = 0;
        foreach (var file in Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(file);
            for (var at = source.IndexOf("SignInAsync(", StringComparison.Ordinal); at >= 0; at = source.IndexOf("SignInAsync(", at + 1, StringComparison.Ordinal))
            {
                var arguments = Arguments(source, at + "SignInAsync(".Length);
                if (arguments.Count == 0 || !arguments[0].Contains("AuthConstants.CookieScheme", StringComparison.Ordinal))
                {
                    continue;
                }

                found++;
                Assert.True(arguments.Count >= 3, $"{Path.GetFileName(file)}: the session cookie is written without its properties (use SessionCookie.RefreshAsync).");
            }
        }

        Assert.True(found >= 3, "The sign-ins of the session cookie were not found.");
    }

    [Fact]
    public void The_theme_the_wallpaper_and_the_password_refresh_the_cookie_with_what_it_carries()
    {
        var web = WebUiTests.WebRoot();
        Assert.Contains("SessionCookie.RefreshAsync(http, user)", File.ReadAllText(Path.Combine(web, "Infrastructure", "ApiEndpoints.cs")));
        var profile = File.ReadAllText(Path.Combine(web, "Pages", "Account", "Profile.cshtml.cs"));
        Assert.Equal(2, profile.Split("SessionCookie.RefreshAsync(HttpContext, user)").Length - 1);

        var helper = File.ReadAllText(Path.Combine(web, "Infrastructure", "SessionCookie.cs"));
        Assert.Contains("properties.StoreTokens(tokens)", helper);
        Assert.Contains("IsPersistent = current?.IsPersistent", helper);
    }

    /// <summary>The top-level arguments of a call, from just after its opening parenthesis.</summary>
    private static List<string> Arguments(string source, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var begin = start;
        for (var i = start; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' or '}' or ']' when depth > 0:
                    depth--;
                    break;
                case ')':
                    arguments.Add(source[begin..i].Trim());
                    return arguments;
                case ',' when depth == 0:
                    arguments.Add(source[begin..i].Trim());
                    begin = i + 1;
                    break;
            }
        }

        return arguments;
    }
}
