using AiNative.Api.Hosting;
using AiNative.Application;
using AiNative.Infrastructure;
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

await app.MigrateDatabaseIfConfiguredAsync().ConfigureAwait(false);
app.UsePlatformApi();

await app.RunAsync().ConfigureAwait(false);
