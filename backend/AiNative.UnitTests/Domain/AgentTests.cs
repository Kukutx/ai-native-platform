using AiNative.Domain.Agents;
using AiNative.Domain.Common;

namespace AiNative.UnitTests.Domain;

public sealed class AgentTests
{
    [Fact]
    public void AssignManager_rejects_reporting_cycles()
    {
        var ceo = NewAgent("CEO");
        var cto = NewAgent("CTO", reportsToId: ceo.Id);
        var dev = NewAgent("Dev", reportsToId: cto.Id);
        var lines = new Dictionary<Guid, Guid?> { [ceo.Id] = null, [cto.Id] = ceo.Id, [dev.Id] = cto.Id };

        var act = () => ceo.AssignManager(dev, lines);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void AssignManager_accepts_valid_manager()
    {
        var ceo = NewAgent("CEO");
        var dev = NewAgent("Dev");

        dev.AssignManager(ceo, new Dictionary<Guid, Guid?> { [ceo.Id] = null, [dev.Id] = null });

        dev.ReportsToId.Should().Be(ceo.Id);
    }

    [Fact]
    public void Terminated_agent_cannot_resume_or_work()
    {
        var agent = NewAgent("A", AgentStatus.Terminated);

        agent.CanWork.Should().BeFalse();
        agent.Invoking(a => a.Resume()).Should().Throw<DomainException>();
    }

    [Fact]
    public void Pause_and_resume_round_trip()
    {
        var agent = NewAgent("A");

        agent.Pause("预算用完");
        agent.CanWork.Should().BeFalse();
        agent.PauseReason.Should().Be("预算用完");

        agent.Resume();
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.PauseReason.Should().BeNull();
    }

    private static Agent NewAgent(string name, AgentStatus status = AgentStatus.Idle, Guid? reportsToId = null) => new()
    {
        Name = name,
        Title = name,
        ModelProvider = "demo",
        Model = "demo",
        Status = status,
        ReportsToId = reportsToId,
    };
}
