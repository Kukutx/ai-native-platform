namespace AiNative.Contracts.Realtime;

/// <summary>
/// 实体变化通知：客户端据此让对应查询失效重取，不在推送里携带完整数据。
/// </summary>
public sealed record RealtimeChangeDto(string Entity, Guid Id, string Kind);

public static class RealtimeMethods
{
    public const string Change = "change";
    public const string RunEvent = "runEvent";
}
