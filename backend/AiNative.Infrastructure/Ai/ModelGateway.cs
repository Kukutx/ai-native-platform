using System.Collections.Concurrent;
using AiNative.Application.Ai;
using AiNative.Application.Common;
using AiNative.Contracts.Platform;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Ai;

/// <summary>
/// 多模型网关实现：按配置的提供方创建并缓存 IChatClient，统一计价。
/// 默认提供方没有配置密钥时自动回退到演示模型，保证开箱可用。
/// </summary>
internal sealed class ModelGateway : IModelGateway
{
    public const string DemoProvider = "demo";

    private static readonly IReadOnlyDictionary<string, ModelPrice> BuiltInPrices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5-1"] = new() { InputPerMillion = 10m, OutputPerMillion = 50m },
        ["claude-opus-5-5"] = new() { InputPerMillion = 4m, OutputPerMillion = 20m },
        ["claude-opus-5"] = new() { InputPerMillion = 5m, OutputPerMillion = 25m },
        ["claude-sonnet-5-5"] = new() { InputPerMillion = 2m, OutputPerMillion = 10m },
        ["claude-haiku-5-5"] = new() { InputPerMillion = 0.10m, OutputPerMillion = 0.50m },
    };

    private readonly AiOptions _options;
    private readonly Dictionary<string, IChatClientFactory> _factories;
    private readonly Dictionary<string, ModelProviderOptions> _providers;
    private readonly ConcurrentDictionary<(string Provider, string Model), IChatClient> _clients = new();

    public ModelGateway(IOptions<AiOptions> options, IEnumerable<IChatClientFactory> factories, ILogger<ModelGateway> logger)
    {
        _options = options.Value;
        _factories = factories.ToDictionary(f => f.Kind, StringComparer.OrdinalIgnoreCase);
        _providers = new Dictionary<string, ModelProviderOptions>(_options.Providers, StringComparer.OrdinalIgnoreCase);
        _providers.TryAdd(DemoProvider, new ModelProviderOptions { Kind = "demo", Models = ["demo"] });

        Default = IsConfigured(_options.DefaultProvider)
            ? new ModelSelection(_options.DefaultProvider, _options.DefaultModel)
            : new ModelSelection(DemoProvider, "demo");
        if (Default.Provider == DemoProvider)
        {
            logger.LogWarning("Default model provider {Provider} is not configured; falling back to the demo model.", _options.DefaultProvider);
        }

        // 没有单价的模型花费恒为 0，月度预算对它不起作用。免费的本地模型可以把单价显式配成 0 来消除提醒。
        var models = _providers.Where(p => p.Key != DemoProvider && IsConfigured(p.Key))
            .SelectMany(p => p.Value.Models.Select(m => (Provider: p.Key, Model: m)))
            .Append((Provider: Default.Provider, Model: Default.Model))
            .Where(m => m.Provider != DemoProvider)
            .Distinct();
        foreach (var (provider, model) in models)
        {
            if (!HasPrice(model))
            {
                logger.LogWarning(
                    "Model {Model} of provider {Provider} has no price; its usage is recorded as $0 and monthly budgets will not limit it. Set Ai:Pricing:{Model}:InputPerMillion and OutputPerMillion (0 for free local models).",
                    model, provider, model);
            }
        }
    }

    public ModelSelection Default { get; }

    public bool HasProvider(string provider) => _providers.ContainsKey(provider) && IsConfigured(provider);

    public IChatClient GetClient(string provider, string model)
    {
        if (!HasProvider(provider))
        {
            throw new ConflictException("model.provider_unavailable", $"模型提供方 {provider} 未配置或缺少密钥。");
        }

        return _clients.GetOrAdd((provider.ToLowerInvariant(), model), key =>
        {
            var options = _providers[key.Provider];
            if (!_factories.TryGetValue(options.Kind, out var factory))
            {
                throw new ConflictException("model.kind_unsupported", $"不支持的模型协议 {options.Kind}。");
            }

            return factory.Create(options, key.Model);
        });
    }

    public decimal EstimateCostUsd(string provider, string model, long inputTokens, long outputTokens)
    {
        if (!TryGetPrice(model, out var price))
        {
            return 0m;
        }

        return Math.Round((inputTokens * price.InputPerMillion + outputTokens * price.OutputPerMillion) / 1_000_000m, 6);
    }

    public ModelCatalogDto Describe() => new(
        Default.Provider,
        Default.Model,
        [.. _providers.Select(p => new ModelProviderDto(p.Key, p.Value.Kind, p.Value.Models, IsConfigured(p.Key)))]);

    private bool HasPrice(string model) => TryGetPrice(model, out _);

    private bool TryGetPrice(string model, out ModelPrice price)
        => _options.Pricing.TryGetValue(model, out price!) || BuiltInPrices.TryGetValue(model, out price!);

    private bool IsConfigured(string provider)
        => _providers.TryGetValue(provider, out var options)
           && _factories.TryGetValue(options.Kind, out var factory)
           && factory.IsConfigured(options);
}
