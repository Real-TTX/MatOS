using MatOS.Core.Configuration;
using MatOS.Core.Entities;
using MatOS.Core.Users;

namespace MatOS.Tests;

/// <summary>
/// The tabs Security and Password of the account (<see cref="AccountSecurity"/>): the second step lives in MatIdentity, so only a person who signs in
/// through it gets a link to its page; the password form of MatOS is offered only where there is a password of MatOS to change.
/// </summary>
public class AccountSecurityTests
{
    private static readonly IdentitySignIn Connected = new() { Authority = "https://id.example.com/" };

    private static User Person(string? subject, string hash = "pbkdf2$hash") => new() { Username = "anna@example.com", ExternalSubject = subject, PasswordHash = hash };

    [Fact]
    public void Without_the_single_sign_on_there_is_no_second_step_and_the_password_is_MatOS_s_own()
    {
        var security = AccountSecurity.Of(null, Person("sub-anna"));
        Assert.Equal(AccountSignIn.NoSingleSignOn, security.SignIn);
        Assert.Null(security.SecurityUrl);
        Assert.Null(security.PasswordUrl);
        Assert.True(security.OffersLocalPassword);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("id.example.com")]
    [InlineData("ftp://id.example.com")]
    [InlineData("https://id.example.com/?x=1")]
    public void An_address_of_MatIdentity_that_cannot_be_linked_counts_as_none(string authority)
    {
        var security = AccountSecurity.Of(new IdentitySignIn { Authority = authority }, Person("sub-anna"));
        Assert.Equal(AccountSignIn.NoSingleSignOn, security.SignIn);
        Assert.Null(security.SecurityUrl);
    }

    [Fact]
    public void A_local_account_beside_the_single_sign_on_gets_no_link_and_keeps_its_password()
    {
        var security = AccountSecurity.Of(Connected, Person(null));
        Assert.Equal(AccountSignIn.LocalAccount, security.SignIn);
        Assert.Null(security.SecurityUrl);
        Assert.Null(security.PasswordUrl);
        Assert.True(security.OffersLocalPassword);
    }

    [Fact]
    public void A_person_of_the_single_sign_on_is_sent_to_its_own_pages()
    {
        var security = AccountSecurity.Of(Connected, Person("sub-anna"));
        Assert.Equal(AccountSignIn.SingleSignOn, security.SignIn);
        Assert.Equal("https://id.example.com/Account/Security?brand=matos", security.SecurityUrl);
        Assert.Equal("https://id.example.com/Account/Profile?tab=password&brand=matos", security.PasswordUrl);
    }

    [Theory]
    [InlineData("http://192.168.1.10:8102", "http://192.168.1.10:8102/Account/Security?brand=matos")]
    [InlineData("https://example.com/id/", "https://example.com/id/Account/Security?brand=matos")]
    public void The_link_follows_the_address_of_MatIdentity(string authority, string expected)
        => Assert.Equal(expected, AccountSecurity.Of(new IdentitySignIn { Authority = authority }, Person("sub-anna")).SecurityUrl);

    [Fact]
    public void The_pages_are_asked_for_in_the_look_of_MatOS_s_own_client()
    {
        // An install of Mattiverse keeps its client id, and its brand with it (SelfBrand).
        var renamed = AccountSecurity.Of(new IdentitySignIn { Authority = "https://id.example.com", ClientId = "mattiverse" }, Person("sub-anna"));
        Assert.Equal("https://id.example.com/Account/Security?brand=mattiverse", renamed.SecurityUrl);
        Assert.Equal("https://id.example.com/Account/Profile?tab=password&brand=mattiverse", renamed.PasswordUrl);

        // Without a client id there is no brand to name: MatIdentity's own pages.
        var none = AccountSecurity.Of(new IdentitySignIn { Authority = "https://id.example.com", ClientId = " " }, Person("sub-anna"));
        Assert.Equal("https://id.example.com/Account/Security", none.SecurityUrl);
        Assert.Equal("https://id.example.com/Account/Profile?tab=password", none.PasswordUrl);
    }

    [Fact]
    public void A_person_of_the_single_sign_on_without_a_password_of_MatOS_gets_no_form()
    {
        // FindOrCreateExternalAsync makes them without one: the form would ask for a current password that does not exist.
        Assert.False(AccountSecurity.Of(Connected, Person("sub-anna", hash: string.Empty)).OffersLocalPassword);
        // The person who set MatOS up keeps theirs as the way in while MatIdentity does not answer.
        Assert.True(AccountSecurity.Of(Connected, Person("sub-anna")).OffersLocalPassword);
    }
}
