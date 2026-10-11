using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NJsonSchema;
using NJsonSchema.Generation;

namespace AiNative.Api.Hosting;

/// <summary>
/// 让 OpenAPI 的 required 与 C# 契约一致：位置记录的非空参数（响应 DTO）和 required 成员（请求 DTO）标为必填，
/// 生成的 TypeScript 类型因此不会把所有字段都变成可选。
/// </summary>
internal sealed class RequiredPropertiesSchemaProcessor : ISchemaProcessor
{
    public void Process(SchemaProcessorContext context)
    {
        var type = context.ContextualType.Type;
        if (context.Schema.Properties.Count == 0 || type.IsEnum || !type.IsClass)
        {
            return;
        }

        var constructorParameters = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()?
            .GetParameters()
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (!context.Schema.Properties.TryGetValue(name, out var schema))
            {
                continue;
            }

            var isRequiredMember = property.IsDefined(typeof(RequiredMemberAttribute), inherit: false);
            var isRecordParameter = constructorParameters.Contains(property.Name) && !schema.IsNullable(SchemaType.OpenApi3);
            if (isRequiredMember || isRecordParameter)
            {
                context.Schema.RequiredProperties.Add(name);
            }
        }
    }
}
