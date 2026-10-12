using System.Text.Json;
using AiNative.Application.Actions;
using AiNative.Application.Common;

namespace AiNative.UnitTests.Application;

public sealed class ActionToolsetTests
{
    [Theory]
    [InlineData("tasks.create", "tasks_create")]
    [InlineData("tasks.update_status", "tasks_update_status")]
    public void Tool_names_are_model_safe(string key, string expected)
        => ActionToolset.ToToolName(key).Should().Be(expected);

    [Fact]
    public void Pending_approval_is_explained_to_the_model()
    {
        var text = ActionToolset.FormatForModel(new ActionInvocationResult(ActionInvocationStatus.PendingApproval, ApprovalId: Guid.NewGuid()));

        text.Should().Contain("审批");
    }

    [Fact]
    public void Oversized_results_are_truncated_but_stay_valid_json()
    {
        var output = JsonSerializer.SerializeToElement(new { rows = Enumerable.Range(0, 5_000).Select(i => $"第 {i} 行 😀") });
        var text = ActionToolset.FormatForModel(new ActionInvocationResult(ActionInvocationStatus.Succeeded, Output: output));

        text.Length.Should().BeLessThanOrEqualTo(ActionToolset.MaxModelOutputChars);
        var parsed = PlatformJson.Parse(text);
        parsed.GetProperty("status").GetString().Should().Be("ok");
        parsed.GetProperty("truncated").GetBoolean().Should().BeTrue();
        parsed.GetProperty("message").GetString().Should().Contain("缩小范围");
    }

    [Fact]
    public void Small_results_are_passed_through()
    {
        var text = ActionToolset.FormatForModel(new ActionInvocationResult(ActionInvocationStatus.Succeeded, Output: JsonSerializer.SerializeToElement(new { id = 1 })));

        PlatformJson.Parse(text).TryGetProperty("truncated", out _).Should().BeFalse();
    }
}
