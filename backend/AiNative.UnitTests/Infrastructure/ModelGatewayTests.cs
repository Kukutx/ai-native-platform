using AiNative.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiNative.UnitTests.Infrastructure;

public sealed class ModelGatewayTests
{
    private static ModelGateway Create(AiOptions options)
        => new(Options.Create(options), [new AnthropicChatClientFactory(), new OpenAiChatClientFactory(), new DemoChatClientFactory()], NullLogger<ModelGateway>.Instance);

    [Fact]
    public void Falls_back_to_demo_when_default_provider_has_no_key()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            var gateway = Create(new AiOptions
            {
                DefaultProvider = "anthropic",
                Providers = { ["anthropic"] = new ModelProviderOptions { Kind = "anthropic" } },
            });

            gateway.Default.Provider.Should().Be(ModelGateway.DemoProvider);
            gateway.HasProvider("anthropic").Should().BeFalse();
            gateway.GetClient("demo", "demo").Should().BeOfType<DemoChatClient>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous);
        }
    }

    [Fact]
    public void Uses_configured_provider_and_caches_clients()
    {
        var gateway = Create(new AiOptions
        {
            DefaultProvider = "local",
            DefaultModel = "qwen3",
            Providers = { ["local"] = new ModelProviderOptions { Kind = "openai", BaseUrl = "http://localhost:11434/v1" } },
        });

        gateway.Default.Should().Be(new AiNative.Application.Ai.ModelSelection("local", "qwen3"));
        gateway.GetClient("local", "qwen3").Should().BeSameAs(gateway.GetClient("LOCAL", "qwen3"));
    }

    [Fact]
    public void Estimates_cost_from_builtin_and_configured_prices()
    {
        var gateway = Create(new AiOptions { Pricing = { ["my-model"] = new ModelPrice { InputPerMillion = 1m, OutputPerMillion = 2m } } });

        gateway.EstimateCostUsd("anthropic", "claude-opus-5-5", 1_000_000, 100_000).Should().Be(6m);
        gateway.EstimateCostUsd("x", "my-model", 500_000, 500_000).Should().Be(1.5m);
        gateway.EstimateCostUsd("x", "unknown", 1_000, 1_000).Should().Be(0m);
    }

    [Fact]
    public async Task Demo_client_comments_on_focus_task_then_summarizes()
    {
        using var client = new DemoChatClient();
        var taskId = Guid.NewGuid();
        var tools = new ChatOptions { Tools = [AIFunctionFactory.CreateDeclaration("tasks_comment", "comment", System.Text.Json.JsonDocument.Parse("{}").RootElement)] };
        List<ChatMessage> messages = [new(ChatRole.System, "sys"), new(ChatRole.User, $"focus_task_id: {taskId}")];

        var first = await client.GetResponseAsync(messages, tools, TestContext.Current.CancellationToken);
        var call = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        call.Name.Should().Be("tasks_comment");
        call.Arguments!["taskId"].Should().Be(taskId.ToString());

        messages.AddRange(first.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "ok")]));
        var second = await client.GetResponseAsync(messages, tools, TestContext.Current.CancellationToken);
        second.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Should().BeEmpty();
        second.Text.Should().NotBeNullOrWhiteSpace();
    }
}
