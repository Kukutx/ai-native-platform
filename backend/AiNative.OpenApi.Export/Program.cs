using System.Text;
using AiNative.Api.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

// 在内存中启动 API 宿主并导出 OpenAPI 文档，不需要数据库，也不需要真实端口。
// 用法：dotnet run --project backend/AiNative.OpenApi.Export -- <输出路径>
var outputPath = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("AINATIVE_OPENAPI_EXPORT_PATH");
if (string.IsNullOrWhiteSpace(outputPath))
{
    Console.Error.WriteLine("Usage: AiNative.OpenApi.Export <output-path>");
    return 2;
}

await using var factory = new WebApplicationFactory<ApiControllerBase>()
    .WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("OpenApiExport");
        builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=unused");
        builder.UseSetting("Auth:SigningKey", "openapi-export-signing-key-not-used-0123456789");
        builder.UseSetting("Scheduler:Enabled", "false");
        builder.UseSetting("Database:MigrateOnStartup", "false");
    });

using var client = factory.CreateClient();
using var response = await client.GetAsync(new Uri("/swagger/v1/swagger.json", UriKind.Relative)).ConfigureAwait(false);
response.EnsureSuccessStatusCode();
var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
if (!json.Contains("\"openapi\"", StringComparison.Ordinal) || !json.Contains("\"paths\"", StringComparison.Ordinal))
{
    throw new InvalidOperationException("OpenAPI export returned an invalid document.");
}

var fullPath = Path.GetFullPath(outputPath);
Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
await File.WriteAllTextAsync(fullPath, json.ReplaceLineEndings("\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
Console.WriteLine($"OpenAPI document written to {fullPath}");
return 0;
