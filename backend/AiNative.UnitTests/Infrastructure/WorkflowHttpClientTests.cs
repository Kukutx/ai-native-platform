using System.Net;
using AiNative.Application.Workflows;
using AiNative.Infrastructure.Common;
using AiNative.Infrastructure.Workflows;
using Microsoft.Extensions.Options;

namespace AiNative.UnitTests.Infrastructure;

public sealed class WorkflowHttpClientTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Private_and_special_addresses_are_not_public(string address)
        => OutboundHttp.IsPublic(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string address)
        => OutboundHttp.IsPublic(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("http://127.0.0.1:9/")]
    [InlineData("http://localhost:9/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task Requests_to_private_networks_are_refused_by_default(string url)
    {
        using var client = new WorkflowHttpClient(Options.Create(new OutboundNetworkOptions()));

        var act = () => client.SendAsync(
            new WorkflowHttpRequest("GET", new Uri(url), new Dictionary<string, string>(), null, TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<WorkflowHttpException>()).Which.Message.Should().Contain("内网");
    }

    [Fact]
    public void Step_paths_sort_in_execution_order()
    {
        string[] paths = ["10", "2.10.0", "2", "2.1.0", "2.9.0", "2.1.0.0.0", "1"];

        paths.Order(StepPathComparer.Instance).Should().Equal("1", "2", "2.1.0", "2.1.0.0.0", "2.9.0", "2.10.0", "10");
    }
}
