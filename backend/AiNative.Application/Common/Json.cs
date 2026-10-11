using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiNative.Application.Common;

/// <summary>
/// 平台统一的 JSON 约定：camelCase、枚举用字符串。HTTP、实时通道、动作输入输出与 jsonb 列都用这一套。
/// </summary>
public static class PlatformJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static JsonElement Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
