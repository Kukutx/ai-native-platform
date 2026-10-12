namespace AiNative.Application.Access;

/// <summary>
/// 声明请求所需的功能权限。请求管道统一检查（同时要求功能所属模块已启用），
/// HTTP、实时通道、智能体动作走的是同一道检查，不会出现某个入口漏判。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RequireFeatureAttribute(string feature) : Attribute
{
    public string Feature { get; } = feature;
}
