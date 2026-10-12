namespace AiNative.Application.Common;

/// <summary>
/// 分页参数的统一规范化：页码至少为 1，每页条数限制在 1 到上限之间，
/// 页码再限制在偏移量 (page - 1) * pageSize 不会溢出的范围内（超大页码直接得到空页，而不是 500）。
/// </summary>
public static class Paging
{
    public const int DefaultMaxPageSize = 200;

    public static (int Page, int PageSize) Normalize(int page, int pageSize, int maxPageSize = DefaultMaxPageSize)
    {
        var size = Math.Clamp(pageSize, 1, maxPageSize);
        return (Math.Clamp(page, 1, int.MaxValue / size), size);
    }
}
