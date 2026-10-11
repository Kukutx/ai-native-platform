using AiNative.Domain.Crm;

namespace AiNative.UnitTests.Domain;

public sealed class CrmDealTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    private static CrmStage Stage(CrmStageKind kind) => new() { Id = Guid.NewGuid(), Name = kind.ToString(), Kind = kind };

    [Fact]
    public void Closing_records_the_time_and_reopening_clears_it()
    {
        var deal = new CrmDeal { Title = "年度采购" };

        deal.MoveTo(Stage(CrmStageKind.Open), Now);
        deal.Status.Should().Be(CrmDealStatus.Open);
        deal.ClosedAt.Should().BeNull();

        deal.MoveTo(Stage(CrmStageKind.Won), Now);
        deal.Status.Should().Be(CrmDealStatus.Won);
        deal.ClosedAt.Should().Be(Now);

        deal.MoveTo(Stage(CrmStageKind.Open), Now.AddDays(1));
        deal.Status.Should().Be(CrmDealStatus.Open);
        deal.ClosedAt.Should().BeNull();
    }

    [Fact]
    public void Moving_between_stages_of_the_same_kind_keeps_the_close_time()
    {
        var deal = new CrmDeal { Title = "年度采购" };
        deal.MoveTo(Stage(CrmStageKind.Lost), Now);

        deal.MoveTo(Stage(CrmStageKind.Lost), Now.AddDays(3));

        deal.ClosedAt.Should().Be(Now);
    }

    [Fact]
    public void Created_directly_in_a_closed_stage_counts_as_closed_now()
    {
        var deal = new CrmDeal { Title = "老客户续约" };

        deal.MoveTo(Stage(CrmStageKind.Won), Now);

        deal.Status.Should().Be(CrmDealStatus.Won);
        deal.ClosedAt.Should().Be(Now);
    }
}
