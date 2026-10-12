namespace AiNative.Application.Platform;

/// <summary>
/// 自助注册开关（与令牌设置同在 Auth 配置节）。对公网开放的私有部署建好自己的空间后可以关闭，
/// 避免陌生人注册空间、消耗模型额度；之后的成员由管理员在「设置 → 成员」里加入。
/// </summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Auth";

    public bool AllowRegistration { get; set; } = true;
}
