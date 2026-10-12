using System.Text.Json;
using AiNative.Domain.Agents;

namespace AiNative.Contracts.Agents;

/// <summary>
/// 执行过程事件；HTTP 查询与实时推送使用同一结构。Data 的形状由 Type 决定。
/// </summary>
public sealed record RunEventDto(Guid RunId, Guid AgentId, int Seq, RunEventType Type, JsonElement Data, DateTimeOffset CreatedAt);
