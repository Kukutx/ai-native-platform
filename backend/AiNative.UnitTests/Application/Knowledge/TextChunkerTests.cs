using AiNative.Application.Knowledge.Text;

namespace AiNative.UnitTests.Application.Knowledge;

public sealed class TextChunkerTests
{
    [Fact]
    public void Short_text_is_one_chunk()
        => TextChunker.Split("一段很短的说明。").Should().Equal("一段很短的说明。");

    [Fact]
    public void Empty_text_has_no_chunks()
        => TextChunker.Split("  \n ").Should().BeEmpty();

    [Fact]
    public void Chunks_carry_their_markdown_heading_path()
    {
        var text = """
            # 差旅报销

            ## 住宿标准
            一线城市每晚不超过 600 元，其他城市每晚不超过 400 元。超出部分需要提前申请，由部门负责人审批后才能报销，否则自行承担。

            ## 报销时限
            出差结束后 15 个工作日内提交报销单，附上发票和行程单。逾期提交需要部门负责人特批，超过三个月不再受理。
            """;

        var chunks = TextChunker.Split(text, maxChars: 100, overlapChars: 0);

        chunks.Should().Contain(c => c.StartsWith("差旅报销 > 住宿标准", StringComparison.Ordinal) && c.Contains("600 元", StringComparison.Ordinal));
        chunks.Should().Contain(c => c.StartsWith("差旅报销 > 报销时限", StringComparison.Ordinal) && c.Contains("15 个工作日", StringComparison.Ordinal));
    }

    [Fact]
    public void Long_paragraphs_are_split_within_the_size_limit_with_overlap()
    {
        var text = string.Concat(Enumerable.Range(1, 60).Select(i => $"这是第 {i} 句话，用来测试切分。"));

        var chunks = TextChunker.Split(text, maxChars: 200, overlapChars: 40);

        chunks.Should().HaveCountGreaterThan(3);
        chunks.Should().OnlyContain(c => c.Length <= 260, "切分后单段不应明显超过上限（含重叠）");
        string.Concat(chunks).Should().Contain("第 60 句话", "末尾内容不能丢");
        for (var i = 1; i < chunks.Count; i++)
        {
            var tail = chunks[i - 1][^10..];
            chunks[i].Should().Contain(tail, "相邻片段之间保留重叠，避免句子被切断后检索不到");
        }
    }
}
