using AiNative.Application.Platform;

namespace AiNative.UnitTests.Application;

public sealed class ApiKeyTokensTests
{
    [Fact]
    public void Generated_token_has_prefix_and_verifies_against_its_hash()
    {
        var (token, prefix, hash) = ApiKeyTokens.Generate();

        token.Should().StartWith(prefix + "_").And.HaveLength(56);
        prefix.Should().MatchRegex("^ank_[0-9A-Za-z]{8}$");
        ApiKeyTokens.PrefixOf(token).Should().Be(prefix);
        ApiKeyTokens.Verify(token, hash).Should().BeTrue();
        ApiKeyTokens.Verify(token + "x", hash).Should().BeFalse();
    }

    [Fact]
    public void Generated_tokens_are_unique()
        => Enumerable.Range(0, 50).Select(_ => ApiKeyTokens.Generate().Token).Should().OnlyHaveUniqueItems();

    [Theory]
    [InlineData("eyJhbGciOi.jwt")]
    [InlineData("ank_short_secret")]
    [InlineData("ank_ABCDEFGH")]
    public void Malformed_tokens_have_no_prefix(string token)
        => ApiKeyTokens.PrefixOf(token).Should().BeNull();
}
