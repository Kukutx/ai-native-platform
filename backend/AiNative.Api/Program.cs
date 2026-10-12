using AiNative.Api.Hosting;
using AiNative.Application;
using AiNative.Infrastructure;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddPlatformApi(builder.Configuration);

var app = builder.Build();

try
{
    await app.MigrateDatabaseIfConfiguredAsync().ConfigureAwait(false);
    app.UsePlatformApi();
    await app.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (OptionsValidationException ex)
{
    // 配置错误（例如缺少签名密钥）直接说明原因并以非零码退出，不再抛出未处理异常刷一屏堆栈。
    await Console.Error.WriteLineAsync($"配置有误，无法启动：{string.Join("；", ex.Failures)}").ConfigureAwait(false);
    return 1;
}
