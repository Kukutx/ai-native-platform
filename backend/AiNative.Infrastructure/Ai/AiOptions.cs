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

    /// <summary>
    /// 知识库与记忆检索用的向量模型。
    /// </summary>
    public EmbeddingOptions Embeddings { get; set; } = new();
}

public sealed class EmbeddingOptions
{
    /// <summary>
    /// 使用 Providers 里的哪个提供方（协议须为 openai，即 OpenAI 或兼容端点，如 Ollama、vLLM、各家云服务）。
    /// 为空时使用内置的离线演示向量（只反映字面相似）。Anthropic 不提供向量接口。
    /// </summary>
    public string? Provider { get; set; }

    public string Model { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// 请求时要求模型输出的维度（仅部分模型支持，例如 OpenAI text-embedding-3 系列）；为空则不传。
    /// 无论模型输出多少维，入库前都会统一到 1024 维。
    /// </summary>
    public int? RequestDimensions { get; set; }

    public int BatchSize { get; set; } = 64;

    /// <summary>
    /// 向量检索的相关性门槛（余弦距离，0 最相似、2 完全相反），超过的候选不参与排序。不同模型的距离分布不同，可按需调整。
    /// </summary>
    public double MaxDistance { get; set; } = 0.8;
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
