using AiNative.Infrastructure;
using AiNative.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AiNative.UnitTests.Infrastructure;

public sealed class DeploymentSettingsTests
{
    private const string RandomKey = "f3Jq8yS0pX2vN6bL1cR9tW4zK7mD5hA0eG2uY8iO3=";

    [Fact]
    public void Signing_key_must_be_present_and_long_enough()
    {
        var validator = new AuthOptionsValidator(new TestEnvironment(Environments.Production));

        validator.Validate(null, new AuthOptions()).FailureMessage.Should().Contain("Auth__SigningKey");
        validator.Validate(null, new AuthOptions { SigningKey = "short" }).FailureMessage.Should().Contain("太短");
        validator.Validate(null, new AuthOptions { SigningKey = RandomKey }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Repository_development_key_is_rejected_outside_development()
    {
        const string developmentKey = "dev-only-signing-key-change-me-0123456789abcdef";

        new AuthOptionsValidator(new TestEnvironment(Environments.Development))
            .Validate(null, new AuthOptions { SigningKey = developmentKey }).Succeeded.Should().BeTrue();
        new AuthOptionsValidator(new TestEnvironment(Environments.Production))
            .Validate(null, new AuthOptions { SigningKey = developmentKey }).FailureMessage.Should().Contain("开发密钥");
        new AuthOptionsValidator(new TestEnvironment(Environments.Staging))
            .Validate(null, new AuthOptions { SigningKey = developmentKey }).Failed.Should().BeTrue();
    }

    [Fact]
    public void Connection_string_disables_gss_encryption_unless_configured()
    {
        Connection("Host=db;Database=ainative;Username=app;Password=secret").Should().Contain("GSS Encryption Mode=Disable");
        Connection("Host=db;Database=ainative;Username=app;Password=secret;GSS Encryption Mode=Require").Should().Contain("Require").And.NotContain("Disable");
        Connection("Host=db;GssEncryptionMode=Prefer").Should().NotContain("Disable");

        var missing = () => Connection(null);
        missing.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionStrings__Postgres*");
    }

    private static string Connection(string? value)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = value })
            .Build()
            .GetPlatformConnectionString();

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "AiNative.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
