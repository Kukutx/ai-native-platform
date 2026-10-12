using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Ai;
using AiNative.Application.Modules;
using AiNative.Contracts.Platform;

namespace AiNative.Application.Platform;

/// <summary>
/// 所有模块声明的功能权限（角色编辑器用）。
/// </summary>
[RequireFeature(Features.Platform.RolesManage)]
public sealed record ListFeaturesQuery : IQuery<IReadOnlyList<FeatureDto>>;

/// <summary>
/// 可选的模型提供方与模型（智能体配置用）。
/// </summary>
[RequireFeature(Features.Agents.View)]
public sealed record GetModelCatalogQuery : IQuery<ModelCatalogDto>;

internal sealed class CatalogQueryHandlers(IModuleCatalog catalog, IModelGateway models) :
    IRequestHandler<ListFeaturesQuery, IReadOnlyList<FeatureDto>>,
    IRequestHandler<GetModelCatalogQuery, ModelCatalogDto>
{
    public Task<IReadOnlyList<FeatureDto>> HandleAsync(ListFeaturesQuery request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<FeatureDto>>([.. catalog.AllFeatures.Select(f => new FeatureDto(f.Key, f.Name, f.ModuleKey))]);

    public Task<ModelCatalogDto> HandleAsync(GetModelCatalogQuery request, CancellationToken cancellationToken)
        => Task.FromResult(models.Describe());
}
