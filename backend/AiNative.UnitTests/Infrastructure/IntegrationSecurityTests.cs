using System.Text.Json;
using AiNative.Application.Integrations;
using AiNative.Infrastructure.Common;
using AiNative.Infrastructure.Integrations;
using AiNative.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiNative.UnitTests.Infrastructure;

public sealed class IntegrationSecurityTests
{
    private const string SigningKey = "unit-tests-signing-key-0123456789abcdef";
    private static readonly string MasterKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public void Secrets_round_trip_and_are_bound_to_tenant_and_name()
    {
        var protector = Protector(MasterKey);
        var cipher = protector.Protect(Tenant, "GITHUB_TOKEN", "ghp_秘密");

        protector.Unprotect(Tenant, "GITHUB_TOKEN", cipher).Should().Be("ghp_秘密");
        protector.Protect(Tenant, "GITHUB_TOKEN", "ghp_秘密").Should().NotEqual(cipher, "每次加密用新的随机数");
        FluentActions.Invoking(() => protector.Unprotect(Guid.CreateVersion7(), "GITHUB_TOKEN", cipher)).Should().Throw<SecretUnavailableException>();
        FluentActions.Invoking(() => protector.Unprotect(Tenant, "OTHER", cipher)).Should().Throw<SecretUnavailableException>();

        cipher[^1] ^= 0xFF;
        FluentActions.Invoking(() => protector.Unprotect(Tenant, "GITHUB_TOKEN", cipher)).Should().Throw<SecretUnavailableException>();
    }

    [Fact]
    public void Rotated_master_keys_still_decrypt_old_secrets()
    {
        // 起初没有单独的主密钥（由签名密钥派生），后来设置了主密钥，再后来又换了一次。
        var derived = Protector(null).Protect(Tenant, "A", "一");
        var first = Protector(MasterKey).Protect(Tenant, "B", "二");
        var newKey = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());
        var rotated = Protector(newKey, MasterKey);

        rotated.Unprotect(Tenant, "A", derived).Should().Be("一");
        rotated.Unprotect(Tenant, "B", first).Should().Be("二");
        FluentActions.Invoking(() => Protector(newKey).Unprotect(Tenant, "B", first)).Should().Throw<SecretUnavailableException>()
            .Which.Message.Should().Contain("主密钥");
    }

    [Theory]
    [InlineData("短")]
    [InlineData("AAAA")]
    public void Malformed_master_keys_are_rejected_at_startup(string key)
        => new SecretsOptionsValidator().Validate(null, new SecretsOptions { MasterKey = key }).Failed.Should().BeTrue();

    [Fact]
    public async Task Mcp_servers_on_private_networks_are_refused_by_default()
    {
        await using var gateway = new McpGateway(
            Options.Create(new IntegrationOptions()),
            Options.Create(new OutboundNetworkOptions()),
            NullLoggerFactory.Instance,
            TimeProvider.System);

        var act = () => gateway.ListToolsAsync(
            new McpEndpoint(Guid.NewGuid(), new Uri("http://127.0.0.1:9/mcp"), new Dictionary<string, string>()),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<McpGatewayException>()).Which.Message.Should().Contain("内网");
    }

    private static AesGcmSecretProtector Protector(string? masterKey, params string[] previous)
        => new(
            Options.Create(new SecretsOptions { MasterKey = masterKey, PreviousMasterKeys = [.. previous] }),
            Options.Create(new AuthOptions { SigningKey = SigningKey }));
}
