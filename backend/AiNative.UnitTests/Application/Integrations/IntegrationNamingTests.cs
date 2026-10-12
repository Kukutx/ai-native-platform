using AiNative.Application.Integrations;
using AiNative.Application.Workflows.Expressions;

namespace AiNative.UnitTests.Application.Integrations;

public sealed class IntegrationNamingTests
{
    [Fact]
    public void Tool_slugs_are_valid_unique_and_stable()
    {
        var longName = new string('x', 80);
        string[] names = ["create_issue", "issues/create", "issues.create", "查询", longName, "a-b"];

        var slugs = McpToolNames.AssignSlugs(names);

        slugs["create_issue"].Should().Be("create_issue");
        slugs["a-b"].Should().Be("a-b");
        slugs.Values.Should().OnlyHaveUniqueItems();
        slugs.Values.Should().OnlyContain(s => s.Length <= McpToolNames.MaxSlugLength && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'));
        slugs["issues/create"].Should().NotBe(slugs["issues.create"], "替换字符后撞车的都加上哈希");

        // 同一个工具在不同批次里得到同样的标识，工作流和审批里的引用不会失效。
        McpToolNames.AssignSlugs([longName, "other"])[longName].Should().Be(slugs[longName]);
        McpToolNames.AssignSlugs(names.Reverse())["issues/create"].Should().Be(slugs["issues/create"]);
    }

    [Theory]
    [InlineData("github", true)]
    [InlineData("db2", true)]
    [InlineData("g", false)]
    [InlineData("git_hub", false)]
    [InlineData("GitHub", false)]
    [InlineData("2fa", false)]
    public void Server_keys_cannot_contain_underscores(string key, bool valid)
        => McpToolNames.ServerKeyPattern().IsMatch(key).Should().Be(valid);

    [Fact]
    public void Secret_references_render_without_reinterpreting_other_values()
    {
        var values = new Dictionary<string, string> { ["TOKEN"] = "t0k3n" };

        SecretTemplate.Render("Bearer {{ secrets.TOKEN }} / {{ x }}", values, text => text.Replace("{{ x }}", "{{ secrets.TOKEN }}", StringComparison.Ordinal))
            .Should().Be("Bearer t0k3n / {{ secrets.TOKEN }}", "其余部分渲染出来的引用写法只是普通文字");
        SecretTemplate.References("{{secrets.A}} {{ secrets.B_2 }} {{ secrets.lower }}").Should().Equal("A", "B_2");
        FluentActions.Invoking(() => SecretTemplate.Render("{{ secrets.MISSING }}", values, t => t)).Should().Throw<SecretUnavailableException>();
    }

    [Fact]
    public void Templates_report_which_roots_they_use()
    {
        WorkflowTemplate.UsesRoot("Bearer {{ default(secrets.X, '') }}", "secrets").Should().BeTrue();
        WorkflowTemplate.UsesRoot("{{ trigger.secrets }}", "secrets").Should().BeFalse();
        WorkflowExpression.Parse("steps.a.output && item.x == input.y").Roots.Should().BeEquivalentTo("steps", "item", "input");
    }
}
