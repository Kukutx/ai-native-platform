using AiNative.Domain.Common;

namespace AiNative.Domain.Modules;

/// <summary>
/// 租户对某个业务模块的启用状态。没有记录时按模块的默认启用设置处理。
/// </summary>
public sealed class TenantModule : TenantEntity
{
    public required string ModuleKey { get; set; }

    public bool Enabled { get; set; }
}
