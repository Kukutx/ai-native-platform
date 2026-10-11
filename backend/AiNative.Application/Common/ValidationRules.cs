using FluentValidation;

namespace AiNative.Application.Common;

/// <summary>
/// 各模块共用的校验规则，错误提示统一措辞。
/// </summary>
internal static class ValidationRules
{
    public static IRuleBuilderOptions<T, string> Text<T>(this IRuleBuilder<T, string> rule, int max, string field)
        => rule.MaximumLength(max).WithMessage($"{field}最多 {max} 个字。");
}
