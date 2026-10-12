using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Platform;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Platform;

/// <summary>
/// 空间设置变化时推送的实体类型，客户端据此刷新会话（空间名称、时区）。
/// </summary>
public static class TenantRealtime
{
    public const string Tenant = "tenant";
}

[RequireFeature(Features.Platform.SettingsManage)]
public sealed record UpdateTenantSettingsCommand(UpdateTenantSettingsRequest Request) : ICommand<TenantSettingsDto>;

public sealed class UpdateTenantSettingsCommandValidator : AbstractValidator<UpdateTenantSettingsCommand>
{
    public UpdateTenantSettingsCommandValidator()
    {
        RuleFor(x => x.Request.Name).NotEmpty().WithMessage("请填写空间名称。").Text(100, "空间名称");
        RuleFor(x => x.Request.TimeZone).NotEmpty().WithMessage("请选择时区。")
            .Must(id => TimeZones.Normalize(id) is not null).WithMessage("无法识别的时区，请使用 IANA 名称，例如 Asia/Shanghai。");
    }
}

internal sealed class TenantSettingsHandler(IAppDbContext db, ITenantContext tenant, TenantCalendar calendar, IChangeFeed changes)
    : IRequestHandler<UpdateTenantSettingsCommand, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(UpdateTenantSettingsCommand request, CancellationToken cancellationToken)
    {
        var current = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenant.TenantId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("tenant", tenant.TenantId);
        current.Name = request.Request.Name.Trim();
        current.TimeZone = TimeZones.Normalize(request.Request.TimeZone)!;
        calendar.Reset();
        changes.Publish(TenantRealtime.Tenant, current.Id, RealtimeChangeKind.Updated);
        return new TenantSettingsDto(current.Id, current.Name, current.Slug, current.TimeZone);
    }
}
