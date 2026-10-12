using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiNative.Application.Access;
using AiNative.Application.Actions;
using AiNative.Application.Modules;
using AiNative.IntegrationTests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.IntegrationTests;

/// <summary>
/// 平台动作与模块声明的契约：动作同时服务于界面、智能体工具调用、对话、工作流和 MCP，
/// 一处写错（权限键拼错、工具名超长或重名、输入类型生成不了 Schema）会让它在某个入口静默失效。
/// 用真实宿主的容器取全部已注册的动作和模块，新加的动作自动纳入检查。
/// </summary>
public sealed partial class ActionContractTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    /// <summary>
    /// 模型服务对工具名的通用限制（Anthropic、OpenAI 一致）。
    /// </summary>
    private const int MaxToolNameLength = 64;

    [Fact]
    public void Every_action_has_a_well_formed_unique_key_and_a_model_safe_tool_name()
    {
        var actions = BuiltInActions();

        actions.Should().NotBeEmpty();
        actions.Select(a => a.Key).Should().OnlyHaveUniqueItems();
        foreach (var action in actions)
        {
            action.Key.Should().MatchRegex(ActionKeyPattern(), $"{action.GetType().Name} 的键应形如 module.verb_noun");
            var toolName = ActionToolset.ToToolName(action.Key);
            toolName.Should().MatchRegex(ToolNamePattern(), $"{action.Key} 转换成的工具名只能含字母、数字、下划线和连字符");
            toolName.Length.Should().BeLessThanOrEqualTo(MaxToolNameLength, $"{action.Key} 转换成的工具名超出模型的长度限制");
        }

        // 工具名是键把点换成下划线得到的，不同的键可能撞成同一个工具名（a.b_c 与 a_b.c）。
        actions.Select(a => ActionToolset.ToToolName(a.Key)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_action_belongs_to_a_module_and_requires_a_declared_feature()
    {
        var catalog = Factory.Services.GetRequiredService<IModuleCatalog>();
        var features = catalog.AllFeatures.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var action in BuiltInActions())
        {
            catalog.Find(action.ModuleKey).Should().NotBeNull($"{action.Key} 所属的模块 {action.ModuleKey} 应已注册");
            features.Should().Contain(action.RequiredFeature, $"{action.Key} 需要的权限应由某个模块声明");
            action.Title.Should().NotBeNullOrWhiteSpace($"{action.Key} 需要标题（界面与 MCP 展示）");
            action.Description.Should().NotBeNullOrWhiteSpace($"{action.Key} 需要写给模型看的说明");
        }
    }

    [Fact]
    public void Every_action_input_type_produces_an_object_schema_usable_as_a_tool_declaration()
    {
        using var scope = Factory.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IActionInvoker>();

        foreach (var action in BuiltInActions())
        {
            var schema = invoker.GetInputSchema(action);

            schema.ValueKind.Should().Be(JsonValueKind.Object, action.Key);
            schema.TryGetProperty("type", out var type).Should().BeTrue($"{action.Key} 的输入 Schema 应声明 type");
            type.GetString().Should().Be("object", $"{action.Key} 的输入必须是对象，模型的工具参数只接受对象");
            var declaration = AIFunctionFactory.CreateDeclaration(ActionToolset.ToToolName(action.Key), action.Description, schema);
            declaration.Name.Should().Be(ActionToolset.ToToolName(action.Key));
        }
    }

    [Fact]
    public void Built_in_action_keys_are_not_claimed_by_dynamic_action_sources()
    {
        // 查找动作时先问动态来源（外部 MCP 工具）认不认这个键，认了就不会再找内置动作。
        using var scope = Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetServices<IDynamicActionSource>().ToList();

        sources.Should().NotBeEmpty();
        foreach (var action in BuiltInActions())
        {
            sources.Should().NotContain(s => s.Owns(action.Key), $"内置动作 {action.Key} 会被动态来源截走");
        }
    }

    [Fact]
    public void Every_required_feature_on_requests_is_declared_by_a_module()
    {
        var features = Factory.Services.GetRequiredService<IModuleCatalog>().AllFeatures.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var requirements = new[] { typeof(IPlatformAction).Assembly, typeof(global::AiNative.Api.Controllers.ApiControllerBase).Assembly, typeof(global::AiNative.Infrastructure.DependencyInjection).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetCustomAttributes<RequireFeatureAttribute>().Select(attribute => (Type: type, attribute.Feature)))
            .ToList();

        requirements.Should().NotBeEmpty();
        foreach (var (type, feature) in requirements)
        {
            features.Should().Contain(feature, $"{type.FullName} 上的 [RequireFeature] 引用了没有任何模块声明的权限");
        }
    }

    [Fact]
    public void Modules_declare_unique_features_and_events_that_reference_their_own_module()
    {
        var catalog = Factory.Services.GetRequiredService<IModuleCatalog>();
        var features = catalog.AllFeatures.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        catalog.Modules.Select(m => m.Key).Should().OnlyHaveUniqueItems();
        catalog.AllFeatures.Select(f => f.Key).Should().OnlyHaveUniqueItems();
        catalog.AllEvents.Select(e => e.Key).Should().OnlyHaveUniqueItems();
        foreach (var module in catalog.Modules)
        {
            module.Features.Should().OnlyContain(f => f.ModuleKey == module.Key, $"模块 {module.Key} 声明的权限应归属它自己");
            module.Events.Should().OnlyContain(e => e.ModuleKey == module.Key, $"模块 {module.Key} 声明的事件应归属它自己");
            module.DependsOn.Should().OnlyContain(key => catalog.Find(key) != null, $"模块 {module.Key} 依赖的模块应已注册");
            foreach (var definition in module.Events)
            {
                definition.Key.Should().MatchRegex(ActionKeyPattern(), $"事件键 {definition.Key} 应形如 module.something_happened");
                features.Should().Contain(definition.RequiredFeature, $"事件 {definition.Key} 要求的权限应由某个模块声明");
                definition.Description.Should().NotBeNullOrWhiteSpace($"事件 {definition.Key} 需要说明载荷字段");
            }
        }
    }

    private List<IPlatformAction> BuiltInActions()
    {
        using var scope = Factory.Services.CreateScope();
        return [.. scope.ServiceProvider.GetServices<IPlatformAction>()];
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex ActionKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ToolNamePattern();
}
