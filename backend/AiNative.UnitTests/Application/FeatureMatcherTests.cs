using AiNative.Application.Access;

namespace AiNative.UnitTests.Application;

public sealed class FeatureMatcherTests
{
    [Theory]
    [InlineData("*", "tasks.manage", true)]
    [InlineData("tasks.manage", "tasks.manage", true)]
    [InlineData("tasks.*", "tasks.manage", true)]
    [InlineData("tasks.*", "tasks", false)]
    [InlineData("tasks.*", "tasksx.manage", false)]
    [InlineData("tasks.view", "tasks.manage", false)]
    [InlineData("platform.*", "platform.members.view", true)]
    public void Matches_supports_exact_module_and_global_wildcards(string grant, string feature, bool expected)
        => FeatureMatcher.Matches(grant, feature).Should().Be(expected);

    [Fact]
    public void IsGranted_checks_any_grant()
        => FeatureMatcher.IsGranted(["agents.view", "tasks.*"], "tasks.comment").Should().BeTrue();
}
