using System.Text.Json;

namespace AiNative.Contracts.Actions;

public sealed record ActionDescriptorDto(
    string Key,
    string ModuleKey,
    string Title,
    string Description,
    string RequiredFeature,
    bool IsMutation,
    bool RequiresApprovalForAgents,
    JsonElement InputSchema);

public sealed record InvokeActionResponse(string Status, JsonElement? Output, string? Error, Guid? ApprovalId);
