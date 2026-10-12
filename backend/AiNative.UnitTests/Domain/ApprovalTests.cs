using AiNative.Domain.Common;
using AiNative.Domain.Governance;

namespace AiNative.UnitTests.Domain;

public sealed class ApprovalTests
{
    [Fact]
    public void Decide_records_decision_once()
    {
        var approval = new Approval { ActionKey = "agents.hire", InputJson = "{}" };
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        approval.Decide(approve: true, user, "可以", now);

        approval.Status.Should().Be(ApprovalStatus.Approved);
        approval.DecidedByUserId.Should().Be(user);
        approval.DecidedAt.Should().Be(now);
        approval.Invoking(a => a.Decide(false, user, null, now)).Should().Throw<DomainException>()
            .Which.Code.Should().Be("approval.already_decided");
    }
}
