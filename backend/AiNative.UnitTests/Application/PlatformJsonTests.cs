using AiNative.Application.Common;
using AiNative.Domain.Agents;
using AiNative.Domain.Work;

namespace AiNative.UnitTests.Application;

public sealed class PlatformJsonTests
{
    private sealed record Sample(WorkItemStatus Status, string? Note, int Count);

    [Fact]
    public void Serializes_camel_case_snake_case_enums_and_skips_nulls()
    {
        var json = PlatformJson.Serialize(new Sample(WorkItemStatus.InProgress, null, 2));

        json.Should().Be("""{"status":"in_progress","count":2}""");
    }

    [Fact]
    public void Wakeup_items_round_trip()
    {
        var items = new[] { new WakeupItem { Source = WakeupSource.Assignment, Reason = "指派", WorkItemId = Guid.NewGuid() } };

        var restored = System.Text.Json.JsonSerializer.Deserialize<WakeupItem[]>(PlatformJson.Serialize(items), PlatformJson.Options);

        restored.Should().BeEquivalentTo(items);
    }
}
