using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiNative.Application.Common;

/// <summary>
/// 平台统一的 JSON 约定：camelCase、枚举用字符串。HTTP、实时通道、动作输入输出与 jsonb 列都用这一套。
/// </summary>
public static class PlatformJson
{
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>
    /// 把平台约定应用到框架自带的选项上（MVC、SignalR），保证所有出口序列化结果一致。
    /// </summary>
    public static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.PropertyNameCaseInsensitive = true;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        // 不转义中文：接口体积更小，发给模型的工具结果也不会因 \uXXXX 浪费 token。输出只用于 JSON 上下文，不嵌入 HTML。
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(options);
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
