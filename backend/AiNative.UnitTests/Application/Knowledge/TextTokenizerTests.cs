using AiNative.Application.Knowledge.Text;

namespace AiNative.UnitTests.Application.Knowledge;

public sealed class TextTokenizerTests
{
    [Fact]
    public void Chinese_is_split_into_bigrams_and_latin_into_lowercase_words()
        => TextTokenizer.Tokenize("报销制度 v2：OpenAI API").Should().Equal("报销", "销制", "制度", "v2", "openai", "api");

    [Fact]
    public void Single_cjk_character_is_kept()
        => TextTokenizer.Tokenize("在 A 栋").Should().Equal("在", "a", "栋");

    [Fact]
    public void Full_width_characters_are_normalized()
        => TextTokenizer.Tokenize("ＡＢＣ１２３").Should().Equal("abc123");

    [Fact]
    public void Tsquery_quotes_lexemes_and_escapes_single_quotes()
        => TextTokenizer.ToTsQuery("it's 住宿").Should().Be("'it' | 's' | '住宿'");

    [Fact]
    public void Tsquery_is_null_when_nothing_searchable()
        => TextTokenizer.ToTsQuery("？！…").Should().BeNull();

    [Fact]
    public void Search_text_contains_distinct_tokens()
        => TextTokenizer.ToSearchText("测试测试 test TEST").Should().Be("测试 试测 test");
}
