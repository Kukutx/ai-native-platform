using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AiNative.Infrastructure.Ai;

/// <summary>
/// 按协议创建 IChatClient。新增协议只需注册一个新的工厂。
/// </summary>
public interface IChatClientFactory
{
    string Kind { get; }

    bool IsConfigured(ModelProviderOptions options);

    IChatClient Create(ModelProviderOptions options, string model);
}

/// <summary>
/// Claude：官方 Anthropic SDK，自带 Microsoft.Extensions.AI 适配（自适应思考、推理块签名回传）。
/// </summary>
internal sealed class AnthropicChatClientFactory : IChatClientFactory
{
    public string Kind => "anthropic";

    public bool IsConfigured(ModelProviderOptions options)
        => !string.IsNullOrWhiteSpace(options.ApiKey) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    public IChatClient Create(ModelProviderOptions options, string model)
    {
        IAnthropicClient client = new AnthropicClient
        {
            ApiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : options.ApiKey,
        };
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            var baseUrl = options.BaseUrl;
            client = client.WithOptions(o => o with { BaseUrl = baseUrl });
        }

        return client.AsIChatClient(model);
    }
}

/// <summary>
/// OpenAI 及兼容协议。BaseUrl 指向兼容端点即可接入国内外各家模型或本地 Ollama。
/// </summary>
internal sealed class OpenAiChatClientFactory : IChatClientFactory
{
    public string Kind => "openai";

    public bool IsConfigured(ModelProviderOptions options)
        => !string.IsNullOrWhiteSpace(options.ApiKey) || !string.IsNullOrWhiteSpace(options.BaseUrl);

    public IChatClient Create(ModelProviderOptions options, string model)
    {
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            clientOptions.Endpoint = new Uri(options.BaseUrl);
        }

        // 本地端点（如 Ollama）不校验密钥，但客户端要求非空。
        var key = string.IsNullOrWhiteSpace(options.ApiKey) ? "not-required" : options.ApiKey;
        return new OpenAIClient(new ApiKeyCredential(key), clientOptions).GetChatClient(model).AsIChatClient();
    }
}

internal sealed class DemoChatClientFactory : IChatClientFactory
{
    public string Kind => "demo";

    public bool IsConfigured(ModelProviderOptions options) => true;

    public IChatClient Create(ModelProviderOptions options, string model) => new DemoChatClient();
}
