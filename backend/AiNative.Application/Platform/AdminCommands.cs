using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Platform;
using AiNative.Domain.Identity;
using AiNative.Domain.Modules;
using AiNative.Domain.Tenancy;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AiNative.Application.Platform;

[RequireFeature(Features.Platform.MembersView)]
public sealed record ListMembersQuery : IQuery<IReadOnlyList<MemberDto>>;

[RequireFeature(Features.Platform.MembersManage)]
public sealed record AddMemberCommand(AddMemberRequest Request) : ICommand<MemberDto>;

[RequireFeature(Features.Platform.MembersManage)]
public sealed record UpdateMemberCommand(Guid MembershipId, UpdateMemberRequest Request) : ICommand<MemberDto>;

[RequireFeature(Features.Platform.MembersManage)]
public sealed record RemoveMemberCommand(Guid MembershipId) : ICommand<Unit>;

[RequireFeature(Features.Platform.MembersView)]
public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleDto>>;

[RequireFeature(Features.Platform.RolesManage)]
public sealed record CreateRoleCommand(SaveRoleRequest Request) : ICommand<RoleDto>;

[RequireFeature(Features.Platform.RolesManage)]
public sealed record UpdateRoleCommand(Guid Id, SaveRoleRequest Request) : ICommand<RoleDto>;

[RequireFeature(Features.Platform.RolesManage)]
public sealed record DeleteRoleCommand(Guid Id) : ICommand<Unit>;

[RequireFeature(Features.Platform.MembersView)]
public sealed record ListOrganizationsQuery : IQuery<IReadOnlyList<OrganizationDto>>;

[RequireFeature(Features.Platform.OrganizationsManage)]
public sealed record CreateOrganizationCommand(CreateOrganizationRequest Request) : ICommand<OrganizationDto>;

public sealed record ListModulesQuery : IQuery<IReadOnlyList<ModuleDto>>;

[RequireFeature(Features.Platform.ModulesManage)]
public sealed record SetModuleEnabledCommand(string Key, bool Enabled) : ICommand<ModuleDto>;

public sealed class AddMemberCommandValidator : AbstractValidator<AddMemberCommand>
{
    public AddMemberCommandValidator()
    {
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(x => x.Request.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Request.Password).MinimumLength(8).MaximumLength(128).When(x => x.Request.Password is not null);
    }
}

public sealed class SaveRoleRequestValidator : AbstractValidator<SaveRoleRequest>
{
    public SaveRoleRequestValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(50).Matches("^[a-z][a-z0-9_-]*$").WithMessage("角色键只能包含小写字母、数字、下划线和连字符。");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleForEach(x => x.Features).NotEmpty().MaximumLength(100);
    }
}

public sealed class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveRoleRequestValidator());
}

public sealed class UpdateRoleCommandValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveRoleRequestValidator());
}

public sealed class CreateOrganizationCommandValidator : AbstractValidator<CreateOrganizationCommand>
{
    public CreateOrganizationCommandValidator() => RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(100);
}

internal sealed class MemberHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    IPasswordHasher passwords,
    IMemoryCache cache,
    IPostCommitActions postCommit,
    IClusterBus cluster) :
    IRequestHandler<ListMembersQuery, IReadOnlyList<MemberDto>>,
    IRequestHandler<AddMemberCommand, MemberDto>,
    IRequestHandler<UpdateMemberCommand, MemberDto>,
    IRequestHandler<RemoveMemberCommand, Unit>
{
    public async Task<IReadOnlyList<MemberDto>> HandleAsync(ListMembersQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.Memberships.AsNoTracking()
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { Membership = m, User = u })
            .OrderBy(x => x.Membership.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(x => ToDto(x.Membership, x.User))];
    }

    public async Task<MemberDto> HandleAsync(AddMemberCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        await EnsureRolesAsync(r.RoleIds, cancellationToken).ConfigureAwait(false);
        await EnsureOrganizationAsync(r.OrganizationId, cancellationToken).ConfigureAwait(false);
        var email = User.NormalizeEmail(r.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            if (string.IsNullOrEmpty(r.Password))
            {
                throw new ValidationFailedException(new Dictionary<string, string[]> { [nameof(r.Password)] = ["新用户需要设置初始密码。"] });
            }

            user = new User { Email = email, DisplayName = r.DisplayName.Trim(), PasswordHash = passwords.Hash(r.Password) };
            db.Users.Add(user);
        }
        else if (await db.Memberships.AnyAsync(m => m.UserId == user.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("member.exists", "该用户已经是成员。");
        }

        var membership = new Membership { UserId = user.Id, OrganizationId = r.OrganizationId, RoleIds = [.. r.RoleIds.Distinct()] };
        db.Memberships.Add(membership);
        return ToDto(membership, user);
    }

    public async Task<MemberDto> HandleAsync(UpdateMemberCommand request, CancellationToken cancellationToken)
    {
        var membership = await FindAsync(request.MembershipId, cancellationToken).ConfigureAwait(false);
        var r = request.Request;
        await EnsureRolesAsync(r.RoleIds, cancellationToken).ConfigureAwait(false);
        await EnsureOrganizationAsync(r.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (membership.IsOwner && r.Status != MembershipStatus.Active)
        {
            throw new ConflictException("member.owner_locked", "不能停用空间所有者。");
        }

        membership.RoleIds = [.. r.RoleIds.Distinct()];
        membership.OrganizationId = r.OrganizationId;
        membership.Status = r.Status;
        AccessResolver.InvalidateAfterCommit(cache, postCommit, cluster, [membership.Id]);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == membership.UserId, cancellationToken).ConfigureAwait(false);
        return ToDto(membership, user);
    }

    public async Task<Unit> HandleAsync(RemoveMemberCommand request, CancellationToken cancellationToken)
    {
        var membership = await FindAsync(request.MembershipId, cancellationToken).ConfigureAwait(false);
        if (membership.IsOwner)
        {
            throw new ConflictException("member.owner_locked", "不能移除空间所有者。");
        }

        if (membership.Id == tenant.MembershipId)
        {
            throw new ConflictException("member.self", "不能移除自己。");
        }

        db.Memberships.Remove(membership);
        AccessResolver.InvalidateAfterCommit(cache, postCommit, cluster, [membership.Id]);
        return Unit.Value;
    }

    private async Task<Membership> FindAsync(Guid id, CancellationToken cancellationToken)
        => await db.Memberships.FirstOrDefaultAsync(m => m.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("member", id);

    private async Task EnsureRolesAsync(IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken)
    {
        var distinct = roleIds.Distinct().ToList();
        var found = await db.Roles.CountAsync(r => distinct.Contains(r.Id), cancellationToken).ConfigureAwait(false);
        if (found != distinct.Count)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["RoleIds"] = ["包含不存在的角色。"] });
        }
    }

    private async Task EnsureOrganizationAsync(Guid? organizationId, CancellationToken cancellationToken)
    {
        if (organizationId is { } id && !await db.Organizations.AnyAsync(o => o.Id == id, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("organization", id);
        }
    }

    private static MemberDto ToDto(Membership m, User u)
        => new(m.Id, u.Id, u.Email, u.DisplayName, m.IsOwner, m.Status, m.OrganizationId, m.RoleIds, m.CreatedAt);
}

internal sealed class RoleHandlers(IAppDbContext db, IMemoryCache cache, IPostCommitActions postCommit, IClusterBus cluster) :
    IRequestHandler<ListRolesQuery, IReadOnlyList<RoleDto>>,
    IRequestHandler<CreateRoleCommand, RoleDto>,
    IRequestHandler<UpdateRoleCommand, RoleDto>,
    IRequestHandler<DeleteRoleCommand, Unit>
{
    public async Task<IReadOnlyList<RoleDto>> HandleAsync(ListRolesQuery request, CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking().OrderByDescending(r => r.IsSystem).ThenBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. roles.Select(ToDto)];
    }

    public async Task<RoleDto> HandleAsync(CreateRoleCommand request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        if (await db.Roles.AnyAsync(x => x.Key == r.Key, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("role.key_taken", $"角色键 {r.Key} 已存在。");
        }

        var role = new Role { Key = r.Key, Name = r.Name.Trim(), Description = r.Description?.Trim(), Features = [.. r.Features.Distinct()] };
        db.Roles.Add(role);
        return ToDto(role);
    }

    public async Task<RoleDto> HandleAsync(UpdateRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await db.Roles.FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken).ConfigureAwait(false)
                   ?? throw new NotFoundException("role", request.Id);
        var r = request.Request;
        if (role.Key != r.Key && await db.Roles.AnyAsync(x => x.Key == r.Key, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("role.key_taken", $"角色键 {r.Key} 已存在。");
        }

        role.Key = role.IsSystem ? role.Key : r.Key;
        role.Name = r.Name.Trim();
        role.Description = r.Description?.Trim();
        role.Features = [.. r.Features.Distinct()];
        var holders = await db.Memberships.Where(m => m.RoleIds.Contains(role.Id)).Select(m => m.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        AccessResolver.InvalidateAfterCommit(cache, postCommit, cluster, holders);
        return ToDto(role);
    }

    public async Task<Unit> HandleAsync(DeleteRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await db.Roles.FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken).ConfigureAwait(false)
                   ?? throw new NotFoundException("role", request.Id);
        if (role.IsSystem)
        {
            throw new ConflictException("role.system", "内置角色不能删除。");
        }

        var holders = await db.Memberships.Where(m => m.RoleIds.Contains(role.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var membership in holders)
        {
            membership.RoleIds = [.. membership.RoleIds.Where(id => id != role.Id)];
        }

        db.Roles.Remove(role);
        AccessResolver.InvalidateAfterCommit(cache, postCommit, cluster, [.. holders.Select(m => m.Id)]);
        return Unit.Value;
    }

    private static RoleDto ToDto(Role r) => new(r.Id, r.Key, r.Name, r.Description, r.Features, r.IsSystem);
}

internal sealed class OrganizationHandlers(IAppDbContext db) :
    IRequestHandler<ListOrganizationsQuery, IReadOnlyList<OrganizationDto>>,
    IRequestHandler<CreateOrganizationCommand, OrganizationDto>
{
    public async Task<IReadOnlyList<OrganizationDto>> HandleAsync(ListOrganizationsQuery request, CancellationToken cancellationToken)
    {
        var orgs = await db.Organizations.AsNoTracking().OrderBy(o => o.Path).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. orgs.Select(ToDto)];
    }

    public async Task<OrganizationDto> HandleAsync(CreateOrganizationCommand request, CancellationToken cancellationToken)
    {
        Organization? parent = null;
        if (request.Request.ParentId is { } parentId)
        {
            parent = await db.Organizations.FirstOrDefaultAsync(o => o.Id == parentId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("organization", parentId);
        }

        var org = new Organization { Name = request.Request.Name.Trim() };
        org.PlaceUnder(parent);
        db.Organizations.Add(org);
        return ToDto(org);
    }

    private static OrganizationDto ToDto(Organization o) => new(o.Id, o.Name, o.ParentId, o.Path, o.Depth);
}

internal sealed class ModuleHandlers(IAppDbContext db, IModuleCatalog catalog, ITenantContext tenant, ITransactionLock locks, IChangeFeed changes) :
    IRequestHandler<ListModulesQuery, IReadOnlyList<ModuleDto>>,
    IRequestHandler<SetModuleEnabledCommand, ModuleDto>
{
    public async Task<IReadOnlyList<ModuleDto>> HandleAsync(ListModulesQuery request, CancellationToken cancellationToken)
    {
        var overrides = await db.TenantModules.AsNoTracking().ToDictionaryAsync(m => m.ModuleKey, m => m.Enabled, cancellationToken).ConfigureAwait(false);
        var enabled = catalog.ResolveEnabled(overrides);
        return [.. catalog.Modules.Select(m => ToDto(m, enabled.Contains(m.Key)))];
    }

    /// <summary>
    /// 启用前依赖要已启用，停用前依赖它的模块要已停用。同一空间的模块设置串行修改，避免并发时绕过依赖检查。
    /// </summary>
    public async Task<ModuleDto> HandleAsync(SetModuleEnabledCommand request, CancellationToken cancellationToken)
    {
        var module = catalog.Find(request.Key) ?? throw new NotFoundException("module", request.Key);
        if (module.IsCore)
        {
            throw new ConflictException("module.core", "核心模块不能停用。");
        }

        await locks.AcquireAsync($"modules:{tenant.TenantId}", cancellationToken).ConfigureAwait(false);
        var rows = await db.TenantModules.ToListAsync(cancellationToken).ConfigureAwait(false);
        var overrides = rows.ToDictionary(m => m.ModuleKey, m => m.Enabled);
        var enabled = catalog.ResolveEnabled(overrides);
        if (request.Enabled)
        {
            var missing = module.DependsOn.Where(key => !enabled.Contains(key)).Select(key => catalog.Find(key)?.Name ?? key).ToList();
            if (missing.Count > 0)
            {
                throw new ConflictException("module.dependency_disabled", $"「{module.Name}」依赖{string.Join("、", missing.Select(n => $"「{n}」"))}，请先启用。");
            }
        }
        else
        {
            var dependents = catalog.Modules.Where(m => m.DependsOn.Contains(module.Key) && enabled.Contains(m.Key)).Select(m => m.Name).ToList();
            if (dependents.Count > 0)
            {
                throw new ConflictException("module.required_by", $"{string.Join("、", dependents.Select(n => $"「{n}」"))}依赖「{module.Name}」，请先停用它们。");
            }
        }

        var row = rows.FirstOrDefault(m => m.ModuleKey == module.Key);
        if (row is null)
        {
            db.TenantModules.Add(new TenantModule { ModuleKey = module.Key, Enabled = request.Enabled });
        }
        else
        {
            row.Enabled = request.Enabled;
        }

        overrides[module.Key] = request.Enabled;
        changes.Publish(RealtimeEntities.Module, Guid.Empty, RealtimeChangeKind.Updated);
        return ToDto(module, catalog.ResolveEnabled(overrides).Contains(module.Key));
    }

    private static ModuleDto ToDto(IPlatformModule m, bool enabled)
        => new(m.Key, m.Name, m.Description, m.IsCore, enabled, [.. m.Features.Select(f => new FeatureDto(f.Key, f.Name, f.ModuleKey))], m.DependsOn);
}
