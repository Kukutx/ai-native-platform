using System.ClientModel;
using AiNative.Application.Knowledge;
using AiNative.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace AiNative.Infrastructure.Knowledge;

/// <summary>
/// 向量化服务：配置了 OpenAI 兼容的向量模型时调用它，否则使用离线演示向量。输出统一为 1024 维单位向量。
/// </summary>
internal sealed class EmbeddingService : IEmbeddingService, IDisposable
{
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _generator;
    private readonly EmbeddingOptions _options;

    public EmbeddingService(IOptions<AiOptions> options, ILogger<EmbeddingService> logger)
    {
        _options = options.Value.Embeddings;
        var providerName = _options.Provider;
        if (string.IsNullOrWhiteSpace(providerName))
        {
            ModelId = DemoEmbedder.ModelId;
            return;
        }

        if (!options.Value.Providers.TryGetValue(providerName, out var provider)
            || !string.Equals(provider.Kind, "openai", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Embedding provider {Provider} is missing or not OpenAI-compatible; falling back to demo embeddings.", providerName);
            ModelId = DemoEmbedder.ModelId;
            return;
        }

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            clientOptions.Endpoint = new Uri(provider.BaseUrl);
        }

        var key = string.IsNullOrWhiteSpace(provider.ApiKey) ? "not-required" : provider.ApiKey;
        _generator = new OpenAIClient(new ApiKeyCredential(key), clientOptions).GetEmbeddingClient(_options.Model).AsIEmbeddingGenerator();
        // 请求维度会改变向量空间，也算模型标识的一部分：只改维度同样会触发旧向量重建。
        ModelId = _options.RequestDimensions is { } dimensions ? $"{providerName}/{_options.Model}@{dimensions}" : $"{providerName}/{_options.Model}";
    }

    public string ModelId { get; }

    public bool IsDemo => _generator is null;

    /// <summary>
    /// 向量检索的相关性门槛。演示向量只看字面重合，门槛放宽到"有任何重合"即可。
    /// </summary>
    public double MaxDistance => IsDemo ? 0.98 : _options.MaxDistance;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (_generator is null)
        {
            return [.. texts.Select(DemoEmbedder.Embed)];
        }

        var results = new List<float[]>(texts.Count);
        var generationOptions = new EmbeddingGenerationOptions { Dimensions = _options.RequestDimensions };
        foreach (var batch in texts.Chunk(Math.Max(1, _options.BatchSize)))
        {
            var embeddings = await _generator.GenerateAsync(batch, generationOptions, cancellationToken).ConfigureAwait(false);
            results.AddRange(embeddings.Select(e => EmbeddingVector.Fit(e.Vector.Span)));
        }

        return results;
    }

    public void Dispose() => _generator?.Dispose();
}
