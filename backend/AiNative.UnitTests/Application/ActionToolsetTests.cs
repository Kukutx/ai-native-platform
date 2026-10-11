using AiNative.Application.Actions;

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
}
