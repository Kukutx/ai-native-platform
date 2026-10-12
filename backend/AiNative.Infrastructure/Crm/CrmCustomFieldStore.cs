using AiNative.Application.Crm;
using AiNative.Application.Tenancy;
using AiNative.Domain.Crm;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Infrastructure.Crm;

/// <summary>
/// 在数据库里直接改 custom_fields（jsonb），一条语句处理整类记录。
/// 表名来自 <see cref="Table"/> 的固定映射，其余值全部走参数，所以这里可以用原始 SQL。
/// </summary>
#pragma warning disable EF1002
internal sealed class CrmCustomFieldStore(AppDbContext db, ITenantContext tenant) : ICrmCustomFieldStore
{
    public Task RemoveValuesAsync(CrmRecordType recordType, string key, CancellationToken cancellationToken)
        => db.Database.ExecuteSqlRawAsync(
            $"UPDATE {Table(recordType)} SET custom_fields = custom_fields - {{0}} WHERE tenant_id = {{1}} AND custom_fields ? {{0}}",
            [key, tenant.TenantId],
            cancellationToken);

    public Task RemoveOptionsAsync(CrmRecordType recordType, string key, bool multiple, IReadOnlyCollection<string> options, CancellationToken cancellationToken)
    {
        var removed = options.ToArray();
        var sql = multiple
            ? $$"""
               UPDATE {{Table(recordType)}} AS t
               SET custom_fields = (
                   SELECT CASE WHEN count(*) = 0 THEN t.custom_fields - {0}
                               ELSE jsonb_set(t.custom_fields, ARRAY[{0}], jsonb_agg(item ORDER BY position)) END
                   FROM jsonb_array_elements_text(t.custom_fields -> {0}) WITH ORDINALITY AS items(item, position)
                   WHERE NOT (item = ANY({2})))
               WHERE t.tenant_id = {1} AND jsonb_typeof(t.custom_fields -> {0}) = 'array' AND (t.custom_fields -> {0}) ?| {2}
               """
            : $"UPDATE {Table(recordType)} SET custom_fields = custom_fields - {{0}} WHERE tenant_id = {{1}} AND custom_fields ->> {{0}} = ANY({{2}})";
        return db.Database.ExecuteSqlRawAsync(sql, [key, tenant.TenantId, removed], cancellationToken);
    }

    private static string Table(CrmRecordType recordType) => recordType switch
    {
        CrmRecordType.Company => "crm_companies",
        CrmRecordType.Contact => "crm_contacts",
        CrmRecordType.Deal => "crm_deals",
        _ => throw new ArgumentOutOfRangeException(nameof(recordType)),
    };
}
#pragma warning restore EF1002
