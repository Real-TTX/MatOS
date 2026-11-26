using MatOS.Services.Instances;

namespace MatOS.Services.Tests;

public class NameRulesTests
{
    [Theory]
    [InlineData("anna")]
    [InlineData("abc")]
    [InlineData("a-b")]
    [InlineData("a--b")]
    [InlineData("anna-2")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void Valid_names_pass(string name) => Assert.Null(NameRules.Check(name));

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("0123456789abcdef0123456789abcdefg")]
    [InlineData("-anna")]
    [InlineData("anna-")]
    [InlineData("xn--nna")]
    [InlineData("ab--c")]
    [InlineData("an_na")]
    [InlineData("an.na")]
    [InlineData("änna")]
    [InlineData("an na")]
    public void Invalid_names_are_refused(string name) => Assert.Equal(NameProblem.Invalid, NameRules.Check(name));

    [Theory]
    [InlineData("www")]
    [InlineData("matos")]
    [InlineData("services")]
    [InlineData("localhost")]
    [InlineData("ns1")]
    public void Reserved_names_are_refused(string name) => Assert.Equal(NameProblem.Reserved, NameRules.Check(name));

    [Fact]
    public void Upper_case_is_lowered_and_space_trimmed() => Assert.Equal("anna", NameRules.Normalize("  Anna "));

    [Fact]
    public void Upper_case_input_is_valid_after_normalizing() => Assert.Null(NameRules.Check(NameRules.Normalize("ANNA")));
}
