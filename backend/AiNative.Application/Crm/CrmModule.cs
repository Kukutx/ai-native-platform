using AiNative.Application.Access;
using AiNative.Application.Modules;

namespace AiNative.Application.Crm;

public static class CrmFeatures
{
    public const string View = "crm.view";
    public const string Manage = "crm.manage";
    public const string Configure = "crm.configure";
}

public static class CrmEventKeys
{
    public const string CompanyCreated = "crm.company_created";
    public const string ContactCreated = "crm.contact_created";
    public const string DealCreated = "crm.deal_created";
    public const string DealStageChanged = "crm.deal_stage_changed";
    public const string ActivityLogged = "crm.activity_logged";
}

/// <summary>
/// 客户管理：客户、联系人、商机与跟进记录，按空间配置的销售阶段推进商机。
/// </summary>
internal sealed class CrmModule : IPlatformModule
{
    public const string ModuleKey = "crm";

    public string Key => ModuleKey;

    public string Name => "客户管理";

    public string Description => "客户、联系人、商机与跟进记录：销售阶段看板与成交预测；智能体可以查询和录入客户信息，商机推进、赢单可以触发自动化工作流。";

    public bool IsCore => false;

    public bool EnabledByDefault => true;

    public IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(CrmFeatures.View, "查看客户、联系人、商机与跟进记录", ModuleKey),
        new(CrmFeatures.Manage, "新建、编辑、删除客户、联系人与商机，记录跟进", ModuleKey),
        new(CrmFeatures.Configure, "配置销售阶段和自定义字段", ModuleKey),
    ];

    public IReadOnlyList<PlatformEventDefinition> Events { get; } =
    [
        new(
            CrmEventKeys.CompanyCreated,
            "新客户",
            ModuleKey,
            CrmFeatures.View,
            "新建客户时触发（表格导入的不触发）。载荷：trigger.company 为客户（id、name、industry、website、phone、address、description、tags、owner、customFields 等，customFields 的键为自定义字段标识），trigger.actor 为创建者（kind、id、name）。"),
        new(
            CrmEventKeys.ContactCreated,
            "新联系人",
            ModuleKey,
            CrmFeatures.View,
            "新建联系人时触发（表格导入的不触发）。载荷：trigger.contact 为联系人（id、name、companyId、companyName、title、email、phone、tags、owner、customFields 等），trigger.actor 为创建者。"),
        new(
            CrmEventKeys.DealCreated,
            "新商机",
            ModuleKey,
            CrmFeatures.View,
            "新建商机时触发。载荷：trigger.deal 为商机（id、title、companyId、companyName、contactName、stageName、status、amount、currency、expectedCloseDate、owner、customFields 等），trigger.actor 为创建者。"),
        new(
            CrmEventKeys.DealStageChanged,
            "商机阶段变化",
            ModuleKey,
            CrmFeatures.View,
            "商机移到另一个销售阶段时触发（包括赢单、输单）。载荷：trigger.deal 为变化后的商机，trigger.from / trigger.to 为变化前后的阶段（id、name、probability、kind：open 进行中 / won 赢单 / lost 输单），trigger.note 为推进说明（可能为空），trigger.actor 为操作者。只关心赢单可以用过滤条件 trigger.to.kind == 'won'。"),
        new(
            CrmEventKeys.ActivityLogged,
            "新跟进记录",
            ModuleKey,
            CrmFeatures.View,
            "记录电话、会议、邮件或备注时触发（阶段变化的自动记录不触发）。载荷：trigger.activity 为跟进记录（kind：note / call / meeting / email，content、occurredAt、companyId、companyName、contactId、contactName、dealId、dealTitle），trigger.actor 为记录者。"),
    ];
}

/// <summary>
/// 客户管理推送给客户端的实体变化类型。
/// </summary>
public static class CrmRealtime
{
    public const string Company = "crm_company";
    public const string Contact = "crm_contact";
    public const string Deal = "crm_deal";
    public const string Activity = "crm_activity";
    public const string Stage = "crm_stage";
    public const string Field = "crm_field";
}
