using AiNative.Api.Controllers;
using AiNative.Infrastructure.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.IntegrationTests.Infrastructure;

/// <param name="settings">测试类额外的配置，覆盖下面的默认值。</param>
public sealed class ApiFactory(string connectionString, IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<ApiControllerBase>
{
    public const string Provider = "scripted";
    public const string Model = "scripted-model";

    /// <summary>
    /// 每百万输入 token 的价格，预算测试据此构造超支。
    /// </summary>
    public const decimal InputPricePerMillion = 10m;

    public ScriptedModel Script { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.UseSetting("Auth:SigningKey", "integration-tests-signing-key-0123456789abcdef");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Scheduler:PollInterval", "00:00:00.200");
        builder.UseSetting("Scheduler:TimerSweepInterval", "01:00:00");
        builder.UseSetting("Workflows:PollInterval", "00:00:00.200");
        builder.UseSetting("Workflows:ScheduleSweepInterval", "00:00:00.200");
        builder.UseSetting("Ai:DefaultProvider", Provider);
        builder.UseSetting("Ai:DefaultModel", Model);
        builder.UseSetting($"Ai:Providers:{Provider}:Kind", Provider);
        builder.UseSetting($"Ai:Providers:{Provider}:Models:0", Model);
        builder.UseSetting($"Ai:Pricing:{Model}:InputPerMillion", InputPricePerMillion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting($"Ai:Pricing:{Model}:OutputPerMillion", "0");
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services => services.AddSingleton<IChatClientFactory>(new ScriptedChatClientFactory(Script)));
    }
}
