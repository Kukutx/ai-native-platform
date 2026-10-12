using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AiNative.Api.Hosting;

/// <summary>
/// 查询参数里的枚举与 JSON 保持同一种写法（in_progress），同时兼容 InProgress。
/// </summary>
internal sealed class SnakeCaseEnumModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        var type = Nullable.GetUnderlyingType(context.Metadata.ModelType) ?? context.Metadata.ModelType;
        return type.IsEnum ? new SnakeCaseEnumModelBinder(type) : null;
    }
}

internal sealed class SnakeCaseEnumModelBinder(Type enumType) : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value, value);
        if (Enum.TryParse(enumType, value.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed)
            && Enum.IsDefined(enumType, parsed))
        {
            bindingContext.Result = ModelBindingResult.Success(parsed);
        }
        else
        {
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, $"无效的取值：{value}");
        }

        return Task.CompletedTask;
    }
}
