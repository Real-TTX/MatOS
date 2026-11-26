using MatOS.Services.Instances;

namespace MatOS.Services.Tests;

public class InstanceKeysTests
{
    [Fact]
    public void A_key_has_the_prefix_and_32_bytes_of_base64url()
    {
        var key = InstanceKeys.Create();
        Assert.StartsWith("mos_", key);
        Assert.Equal(4 + 43, key.Length);
        Assert.Matches("^mos_[A-Za-z0-9_-]{43}$", key);
    }

    [Fact]
    public void Keys_differ() => Assert.NotEqual(InstanceKeys.Create(), InstanceKeys.Create());

    [Fact]
    public void The_hash_is_lower_case_sha256_hex()
    {
        // SHA-256 of "mos_test", computed independently.
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("mos_test"u8));
        Assert.Equal(expected, InstanceKeys.Hash("mos_test"));
        Assert.Equal(64, InstanceKeys.Hash("mos_test").Length);
    }

    [Fact]
    public void Matches_only_the_right_key()
    {
        var key = InstanceKeys.Create();
        var hash = InstanceKeys.Hash(key);
        Assert.True(InstanceKeys.Matches(key, hash));
        Assert.False(InstanceKeys.Matches(InstanceKeys.Create(), hash));
        Assert.False(InstanceKeys.Matches(key[4..], InstanceKeys.Hash(key[4..])));
        Assert.False(InstanceKeys.Matches("", hash));
    }

    [Fact]
    public void Overlong_input_is_not_a_key() => Assert.False(InstanceKeys.LooksLikeKey("mos_" + new string('a', 200)));
}
