using AiNative.Application.Common;

namespace AiNative.UnitTests.Application;

public sealed class PagingTests
{
    [Theory]
    [InlineData(1, 20, 1, 20)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, 1_000, 1, 200)]
    [InlineData(int.MaxValue, 200, int.MaxValue / 200, 200)]
    [InlineData(int.MaxValue, 1, int.MaxValue, 1)]
    public void Paging_is_normalized_without_offset_overflow(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var (normalizedPage, normalizedSize) = Paging.Normalize(page, pageSize);

        (normalizedPage, normalizedSize).Should().Be((expectedPage, expectedSize));
        ((long)(normalizedPage - 1) * normalizedSize).Should().BeLessThanOrEqualTo(int.MaxValue);
    }
}
