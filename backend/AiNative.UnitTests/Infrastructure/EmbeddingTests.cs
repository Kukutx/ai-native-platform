using AiNative.Application.Knowledge;
using AiNative.Infrastructure.Knowledge;

namespace AiNative.UnitTests.Infrastructure;

public sealed class EmbeddingTests
{
    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();

    [Fact]
    public void Fit_pads_short_vectors_and_normalizes()
    {
        var vector = EmbeddingVector.Fit([3f, 4f]);

        vector.Should().HaveCount(EmbeddingVector.Dimensions);
        vector[0].Should().BeApproximately(0.6f, 1e-6f);
        vector[1].Should().BeApproximately(0.8f, 1e-6f);
        vector.Skip(2).Should().OnlyContain(v => v == 0);
    }

    [Fact]
    public void Fit_truncates_long_vectors()
    {
        var source = Enumerable.Repeat(1f, EmbeddingVector.Dimensions * 2).ToArray();

        var vector = EmbeddingVector.Fit(source);

        vector.Should().HaveCount(EmbeddingVector.Dimensions);
        Cosine(vector, vector).Should().BeApproximately(1, 1e-5);
    }

    [Fact]
    public void Fit_keeps_zero_vector()
        => EmbeddingVector.Fit(new float[8]).Should().OnlyContain(v => v == 0);

    [Fact]
    public void Sql_literal_matches_pgvector_text_format()
        => EmbeddingVector.ToSql([0.5f, -1.25f, 0f, 1e-8f]).Should().Be("[0.5,-1.25,0,9.99999994E-09]");

    [Fact]
    public void Demo_embedder_is_deterministic_and_unit_length()
    {
        var a = DemoEmbedder.Embed("出差住宿标准");
        var b = DemoEmbedder.Embed("出差住宿标准");

        a.Should().Equal(b);
        Cosine(a, a).Should().BeApproximately(1, 1e-5);
    }

    [Fact]
    public void Demo_embedder_ranks_lexical_overlap_higher()
    {
        var query = DemoEmbedder.Embed("住宿每晚多少钱");
        var related = DemoEmbedder.Embed("一线城市住宿每晚不超过 600 元");
        var unrelated = DemoEmbedder.Embed("季度销售目标与客户回访计划");

        Cosine(query, related).Should().BeGreaterThan(Cosine(query, unrelated));
    }
}
