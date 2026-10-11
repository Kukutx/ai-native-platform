using AiNative.Domain.Common;

namespace AiNative.Domain.Identity;

/// <summary>
/// 租户自定义角色：一组功能权限键（例如 "agents.manage"、"tasks.*"、"*"）。
/// 功能键由各模块声明，角色在数据库中自由组合，不需要改代码。
/// </summary>
public sealed class Role : TenantEntity
{
    public required string Key { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public List<string> Features { get; set; } = [];

    /// <summary>
    /// 注册租户时生成的内置角色，不允许删除（可以修改权限）。
    /// </summary>
    public bool IsSystem { get; set; }
}
