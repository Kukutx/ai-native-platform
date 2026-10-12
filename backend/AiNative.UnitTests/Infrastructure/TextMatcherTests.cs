using AiNative.Infrastructure.Persistence;

namespace AiNative.UnitTests.Infrastructure;

public sealed class TextMatcherTests
{
    [Theory]
    [InlineData("星火", "%星火%")]
    [InlineData("  星火 ", "%星火%")]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData(@"c:\x", @"%c:\\x%")]
    public void Contains_pattern_escapes_like_wildcards(string term, string expected)
        => PostgresTextMatcher.ContainsPattern(term).Should().Be(expected);
}
