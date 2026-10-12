using System.Security.Cryptography;
using System.Text;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Agents;
using AiNative.Application.Ai;
using AiNative.Application.Analytics;
using AiNative.Application.Common;
using AiNative.Application.Crm;
using AiNative.Application.Erp;
using AiNative.Application.Projects;
using AiNative.Application.Modules;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Auth;
using AiNative.Domain.Agents;
using AiNative.Domain.Identity;
using AiNative.Domain.Modules;
using AiNative.Domain.Tenancy;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AiNative.Application.Platform;

public sealed record RegisterCommand(RegisterRequest Request) : ICommand<AuthResponse>;

public sealed record LoginCommand(LoginRequest Request) : ICommand<AuthResponse>;

/// <summary>
/// 切换到用户加入的另一个租户，重新签发令牌。
/// </summary>
public sealed record SwitchTenantCommand(Guid UserId, Guid TenantId) : ICommand<AuthResponse>;

/// <summary>
/// 修改密码：之前签发的所有登录令牌随之失效，返回当前空间的新令牌，本设备不用重新登录。
/// </summary>
public sealed record ChangePasswordCommand(Guid UserId, Guid TenantId, ChangePasswordRequest Request) : ICommand<AuthResponse>;

public sealed record GetSessionQuery(Guid UserId, Guid TenantId, Guid MembershipId) : IQuery<SessionDto>;

public sealed record GetAuthOptionsQuery : IQuery<AuthOptionsDto>;

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(x => x.Request.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
        RuleFor(x => x.Request.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Request.TenantName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Request.TimeZone).MaximumLength(64);
    }
}

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Request.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Request.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.Request.CurrentPassword).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Request.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128)
            .NotEqual(x => x.Request.CurrentPassword).WithMessage("新密码不能和当前密码相同。");
    }
}

internal sealed class AuthHandlers(
    IAppDbContext db,
    IPasswordHasher passwords,
    ITokenService tokens,
    IModuleCatalog modules,
    IEnumerable<ITenantInitializer> initializers,
    IModelGateway models,
    IOptions<RegistrationOptions> registration,
    LoginThrottle throttle,
    IMemoryCache cache,
    IPostCommitActions postCommit,
    IClusterBus cluster,
    TimeProvider clock) :
    IRequestHandler<RegisterCommand, AuthResponse>,
    IRequestHandler<GetAuthOptionsQuery, AuthOptionsDto>,
    IRequestHandler<LoginCommand, AuthResponse>,
    IRequestHandler<SwitchTenantCommand, AuthResponse>,
    IRequestHandler<ChangePasswordCommand, AuthResponse>,
    IRequestHandler<GetSessionQuery, SessionDto>
{
    // 邮箱不存在时也做一次同样耗时的哈希校验，不让响应时间透露邮箱是否注册过。
    private static string? _decoyHash;

    public async Task<AuthResponse> HandleAsync(RegisterCommand request, CancellationToken cancellationToken)
    {
        if (!registration.Value.AllowRegistration)
        {
            throw new ForbiddenException("这个站点已关闭自助注册，请联系管理员把你加入已有空间。", "auth.registration_disabled");
        }

        var r = request.Request;
        var email = User.NormalizeEmail(r.Email);
        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException("auth.email_taken", "该邮箱已注册，请直接登录。");
        }

        var user = new User { Email = email, DisplayName = r.DisplayName.Trim(), PasswordHash = passwords.Hash(r.Password), LastLoginAt = clock.GetUtcNow() };
        var tenant = new Tenant { Name = r.TenantName.Trim(), Slug = Slugify(r.TenantName), TimeZone = TimeZones.Normalize(r.TimeZone) ?? TimeZones.Utc };
        var root = new Organization { TenantId = tenant.Id, Name = tenant.Name };
        root.PlaceUnder(null);

        var roles = DefaultRoles.All
            .Select(d => new Role { TenantId = tenant.Id, Key = d.Key, Name = d.Name, Description = d.Description, Features = [.. d.Features], IsSystem = true })
            .ToList();
        var membership = new Membership
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            OrganizationId = root.Id,
            IsOwner = true,
            RoleIds = [roles.Single(x => x.Key == DefaultRoles.Admin).Id],
        };

        // 每个新空间自带一名首席助理，开箱即可给它派活；招聘新同事需要人工审批。
        var chief = new Agent
        {
            TenantId = tenant.Id,
            Name = "小智",
            Title = "首席助理",
            Instructions = "负责理解管理者的目标，把它拆解成可执行的任务，委派给合适的同事并跟进结果。团队缺少某类岗位时，可以申请招聘新的智能体。较大的目标建成项目，用里程碑跟踪进度。被问到经营数据时先查经营分析里的指标，不要凭印象回答。重要的结论和资料整理进知识库，方便团队以后查阅。反复出现的固定流程可以申请设置成自动化工作流。",
            ModelProvider = models.Default.Provider,
            Model = models.Default.Model,
            Permissions =
            [
                .. AgentDefaults.Permissions, Features.Agents.Manage, Features.Knowledge.Manage,
                Features.Workflows.View, Features.Workflows.Manage, Features.Workflows.Run,
                CrmFeatures.View, CrmFeatures.Manage, ErpFeatures.View, ErpFeatures.Sales, ErpFeatures.Purchase,
                ProjectFeatures.View, ProjectFeatures.Manage, AnalyticsFeatures.Manage,
            ],
            MonthlyBudgetUsd = 20,
        };

        db.Users.Add(user);
        db.Tenants.Add(tenant);
        db.Organizations.Add(root);
        db.Roles.AddRange(roles);
        db.Memberships.Add(membership);
        db.Agents.Add(chief);
        db.TenantModules.AddRange(modules.Modules.Where(m => !m.IsCore).Select(m => new TenantModule { TenantId = tenant.Id, ModuleKey = m.Key, Enabled = m.EnabledByDefault }));
        foreach (var initializer in initializers)
        {
            await initializer.InitializeAsync(tenant.Id, cancellationToken).ConfigureAwait(false);
        }

        var token = tokens.Issue(user.Id, tenant.Id, membership.Id, user.TokenVersion);
        var summary = new TenantSummaryDto(tenant.Id, tenant.Name, tenant.Slug, true, tenant.TimeZone);
        var defaults = modules.ResolveEnabled(new Dictionary<string, bool>());
        List<string> enabled = [.. modules.Modules.Where(m => defaults.Contains(m.Key)).Select(m => m.Key)];
        var session = new SessionDto(
            new UserDto(user.Id, user.Email, user.DisplayName),
            summary,
            [summary],
            [FeatureMatcher.All],
            enabled,
            DisabledFeatures(enabled));
        return new AuthResponse(token.AccessToken, token.ExpiresAt, session);
    }

    public Task<AuthOptionsDto> HandleAsync(GetAuthOptionsQuery request, CancellationToken cancellationToken)
        => Task.FromResult(new AuthOptionsDto(registration.Value.AllowRegistration));

    public async Task<AuthResponse> HandleAsync(LoginCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Request.Email);
        throttle.EnsureAllowed(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken).ConfigureAwait(false);
        var valid = passwords.Verify(user?.PasswordHash ?? DecoyHash(), request.Request.Password);
        if (user is null || !valid)
        {
            throttle.RecordFailure(email);
            throw new UnauthorizedException("邮箱或密码不正确。");
        }

        throttle.Reset(email);
        user.LastLoginAt = clock.GetUtcNow();
        return await IssueAsync(user, request.Request.TenantId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthResponse> HandleAsync(SwitchTenantCommand request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnauthorizedException();
        return await IssueAsync(user, request.TenantId, cancellationToken, switching: true).ConfigureAwait(false);
    }

    public async Task<AuthResponse> HandleAsync(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnauthorizedException();
        if (!passwords.Verify(user.PasswordHash, request.Request.CurrentPassword))
        {
            throw new ValidationFailedException(new Dictionary<string, string[]>
            {
                [nameof(ChangePasswordRequest.CurrentPassword)] = ["当前密码不正确。"],
            });
        }

        // 以读到的版本为条件更新：并发的两次修改只有一次成功，另一次的令牌不会意外保持有效。
        var hash = passwords.Hash(request.Request.NewPassword);
        var updated = await db.Users
            .Where(u => u.Id == user.Id && u.TokenVersion == user.TokenVersion)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.PasswordHash, hash).SetProperty(u => u.TokenVersion, user.TokenVersion + 1), cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            throw new ConflictException("auth.password_changed", "密码刚被修改过，请重新登录后再试。");
        }

        // 各个空间里缓存的权限带着旧的令牌版本，一并清掉，旧令牌在所有实例上立即失效。
        var membershipIds = await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == user.Id).Select(m => m.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        AccessResolver.InvalidateAfterCommit(cache, postCommit, cluster, membershipIds);
        user.TokenVersion++;
        return await IssueAsync(user, request.TenantId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionDto> HandleAsync(GetSessionQuery request, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnauthorizedException();
        var membership = await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == request.MembershipId && m.UserId == user.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedException();
        return await BuildSessionAsync(user, membership, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthResponse> IssueAsync(User user, Guid? tenantId, CancellationToken cancellationToken, bool switching = false)
    {
        var memberships = await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == user.Id && m.Status == MembershipStatus.Active)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var membership = tenantId is { } id ? memberships.FirstOrDefault(m => m.TenantId == id) : memberships.FirstOrDefault();
        if (membership is null && switching)
        {
            // 已登录的用户切换到不是成员的空间：身份没问题，只是无权访问，返回 403（客户端遇到 401 会直接登出）。
            throw new ForbiddenException("你不是该空间的成员。", "auth.not_a_member");
        }

        if (membership is null)
        {
            throw new UnauthorizedException(tenantId is null ? "该账号还没有加入任何空间。" : "你不是该空间的成员。");
        }

        var token = tokens.Issue(user.Id, membership.TenantId, membership.Id, user.TokenVersion);
        return new AuthResponse(token.AccessToken, token.ExpiresAt, await BuildSessionAsync(user, membership, cancellationToken).ConfigureAwait(false));
    }

    private async Task<SessionDto> BuildSessionAsync(User user, Membership current, CancellationToken cancellationToken)
    {
        var memberships = await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == user.Id && m.Status == MembershipStatus.Active)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var tenantIds = memberships.Select(m => m.TenantId).ToList();
        var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, cancellationToken).ConfigureAwait(false);
        var summaries = memberships
            .Where(m => tenants.ContainsKey(m.TenantId))
            .Select(m => new TenantSummaryDto(m.TenantId, tenants[m.TenantId].Name, tenants[m.TenantId].Slug, m.IsOwner, tenants[m.TenantId].TimeZone))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<string> features;
        if (current.IsOwner)
        {
            features = [FeatureMatcher.All];
        }
        else
        {
            var roleIds = current.RoleIds;
            var lists = await db.Roles.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == current.TenantId && roleIds.Contains(r.Id)).Select(r => r.Features)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            features = [.. lists.SelectMany(f => f).Distinct(StringComparer.Ordinal)];
        }

        var overrides = await db.TenantModules.IgnoreQueryFilters().AsNoTracking().Where(m => m.TenantId == current.TenantId)
            .ToDictionaryAsync(m => m.ModuleKey, m => m.Enabled, cancellationToken).ConfigureAwait(false);
        var resolved = modules.ResolveEnabled(overrides);
        var enabled = modules.Modules.Where(m => resolved.Contains(m.Key)).Select(m => m.Key).ToList();

        return new SessionDto(
            new UserDto(user.Id, user.Email, user.DisplayName),
            summaries.First(t => t.Id == current.TenantId),
            summaries,
            features,
            enabled,
            DisabledFeatures(enabled));
    }

    private string DecoyHash() => _decoyHash ??= passwords.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));

    private List<string> DisabledFeatures(List<string> enabledModules)
        => [.. modules.AllFeatures.Where(f => !enabledModules.Contains(f.ModuleKey)).Select(f => f.Key)];

    private static string Slugify(string name)
    {
        var ascii = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                ascii.Append(ch);
            }
            else if (ascii.Length > 0 && ascii[^1] != '-')
            {
                ascii.Append('-');
            }
        }

        var prefix = ascii.ToString().Trim('-');
        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        return string.IsNullOrEmpty(prefix) ? $"space-{suffix}" : $"{prefix[..Math.Min(prefix.Length, 40)]}-{suffix}";
    }
}
