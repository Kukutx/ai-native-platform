using AiNative.Contracts.Platform;
using Microsoft.Extensions.AI;

namespace AiNative.Application.Ai;

public sealed record ModelSelection(string Provider, string Model);

/// <summary>
/// 多模型网关：按"提供方 + 模型"拿到统一的 IChatClient。上层只依赖 Microsoft.Extensions.AI 抽象，
/// 换模型、加提供方都不影响智能体运行时与对话服务。
/// </summary>
public interface IModelGateway
{
    ModelSelection Default { get; }

    bool HasProvider(string provider);

    IChatClient GetClient(string provider, string model);

    decimal EstimateCostUsd(string provider, string model, long inputTokens, long outputTokens);

    ModelCatalogDto Describe();
}
