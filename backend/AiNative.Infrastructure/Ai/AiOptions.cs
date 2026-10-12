namespace AiNative.Infrastructure.Ai;

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public string DefaultProvider { get; set; } = "anthropic";

    public string DefaultModel { get; set; } = "claude-opus-5-5";

    /// <summary>
    /// 提供方名称 → 配置。名称由使用者自定（例如 "anthropic"、"deepseek"、"ollama"），Kind 决定协议。
    /// </summary>
    public Dictionary<string, ModelProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 模型单价（美元 / 百万 token），覆盖内置价格表。
    /// </summary>
    public Dictionary<string, ModelPrice> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelProviderOptions
{
    /// <summary>
    /// anthropic：官方 Anthropic SDK；openai：OpenAI 及兼容协议（DeepSeek、通义、Ollama、vLLM 等）；demo：内置演示模型。
    /// </summary>
    public string Kind { get; set; } = "openai";

    public string? ApiKey { get; set; }

    public string? BaseUrl { get; set; }

    public List<string> Models { get; set; } = [];
}

public sealed class ModelPrice
{
    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }
}
