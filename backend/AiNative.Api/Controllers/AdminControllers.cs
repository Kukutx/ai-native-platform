using AiNative.Application.Abstractions;
using AiNative.Application.Governance;
using AiNative.Application.Platform;
using AiNative.Contracts.Platform;
using Microsoft.AspNetCore.Mvc;

namespace AiNative.Api.Controllers;

[Route("api/members")]
public sealed class MembersController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<MemberDto>> List() => SendAsync(new ListMembersQuery());

    [HttpPost]
    public Task<MemberDto> Add(AddMemberRequest request) => SendAsync(new AddMemberCommand(request));

    [HttpPut("{membershipId:guid}")]
    public Task<MemberDto> Update(Guid membershipId, UpdateMemberRequest request) => SendAsync(new UpdateMemberCommand(membershipId, request));

    [HttpDelete("{membershipId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid membershipId)
    {
        await SendAsync(new RemoveMemberCommand(membershipId)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/roles")]
public sealed class RolesController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<RoleDto>> List() => SendAsync(new ListRolesQuery());

    [HttpGet("features")]
    public Task<IReadOnlyList<FeatureDto>> Features() => SendAsync(new ListFeaturesQuery());

    [HttpPost]
    public Task<RoleDto> Create(SaveRoleRequest request) => SendAsync(new CreateRoleCommand(request));

    [HttpPut("{id:guid}")]
    public Task<RoleDto> Update(Guid id, SaveRoleRequest request) => SendAsync(new UpdateRoleCommand(id, request));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await SendAsync(new DeleteRoleCommand(id)).ConfigureAwait(false);
        return NoContent();
    }
}

[Route("api/organizations")]
public sealed class OrganizationsController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<OrganizationDto>> List() => SendAsync(new ListOrganizationsQuery());

    [HttpPost]
    public Task<OrganizationDto> Create(CreateOrganizationRequest request) => SendAsync(new CreateOrganizationCommand(request));
}

[Route("api/modules")]
public sealed class ModulesController : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ModuleDto>> List() => SendAsync(new ListModulesQuery());

    [HttpPut("{key}")]
    public Task<ModuleDto> SetEnabled(string key, SetModuleEnabledRequest request) => SendAsync(new SetModuleEnabledCommand(key, request.Enabled));
}

[Route("api/usage")]
public sealed class UsageController : ApiControllerBase
{
    /// <summary>
    /// 本月模型用量与花费（按智能体、按模型）。
    /// </summary>
    [HttpGet]
    public Task<UsageSummaryDto> Summary() => SendAsync(new GetUsageSummaryQuery());
}

[Route("api/models")]
public sealed class ModelsController : ApiControllerBase
{
    [HttpGet]
    public Task<ModelCatalogDto> Catalog() => SendAsync(new GetModelCatalogQuery());
}

[Route("api/tenant")]
public sealed class TenantController : ApiControllerBase
{
    /// <summary>
    /// 修改空间名称和时区。时区影响按天统计、"本月"等日期的计算；当前设置在会话的 tenant 里。
    /// </summary>
    [HttpPut("settings")]
    public Task<TenantSettingsDto> UpdateSettings(UpdateTenantSettingsRequest request) => SendAsync(new UpdateTenantSettingsCommand(request));
}
