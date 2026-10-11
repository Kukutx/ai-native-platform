using AiNative.Domain.Common;
using AiNative.Domain.Tenancy;

namespace AiNative.UnitTests.Domain;

public sealed class OrganizationTests
{
    [Fact]
    public void PlaceUnder_builds_materialized_path_and_depth()
    {
        var root = new Organization { Name = "总部" };
        root.PlaceUnder(null);
        var team = new Organization { Name = "研发" };
        team.PlaceUnder(root);

        team.Path.Should().Be($"/{root.Id}/{team.Id}/");
        team.Depth.Should().Be(1);
        team.ParentId.Should().Be(root.Id);
    }

    [Fact]
    public void PlaceUnder_rejects_cycles()
    {
        var root = new Organization { Name = "总部" };
        root.PlaceUnder(null);
        var child = new Organization { Name = "研发" };
        child.PlaceUnder(root);

        var act = () => root.PlaceUnder(child);

        act.Should().Throw<DomainException>().Which.Code.Should().Be("organization.cycle");
    }
}
