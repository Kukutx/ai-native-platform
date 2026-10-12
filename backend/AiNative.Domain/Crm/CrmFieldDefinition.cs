using AiNative.Domain.Common;

namespace AiNative.Domain.Crm;

/// <summary>
/// 自定义字段挂在哪类记录上。
/// </summary>
public enum CrmRecordType
{
    Company = 0,
    Contact = 1,
    Deal = 2,
}

public enum CrmFieldType
{
    /// <summary>
    /// 一行文字。
    /// </summary>
    Text = 0,

    Number = 1,

    /// <summary>
    /// 日期，存为 yyyy-MM-dd。
    /// </summary>
    Date = 2,

    /// <summary>
    /// 从选项里选一个。
    /// </summary>
    Select = 3,

    /// <summary>
    /// 从选项里选多个。
    /// </summary>
    MultiSelect = 4,

    /// <summary>
    /// 是或否。
    /// </summary>
    Checkbox = 5,
}

/// <summary>
/// 空间自己定义的客户、联系人或商机字段，例如「客户等级」「所属区域」「合同到期日」。
/// 值存在记录的 CustomFields 里，按字段标识取用；标识和类型创建后不能改，免得已有的值失去意义。
/// </summary>
public sealed class CrmFieldDefinition : TenantEntity
{
    public CrmRecordType RecordType { get; set; }

    /// <summary>
    /// 字段标识，小写字母开头，由小写字母、数字和下划线组成，例如 region。
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// 界面上显示的名称，例如「所属区域」。
    /// </summary>
    public required string Label { get; set; }

    public CrmFieldType Type { get; set; }

    /// <summary>
    /// 单选、多选的选项。
    /// </summary>
    public List<string> Options { get; set; } = [];

    public bool Required { get; set; }

    public int Position { get; set; }

    public string Description { get; set; } = string.Empty;
}
