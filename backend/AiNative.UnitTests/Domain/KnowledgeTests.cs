using AiNative.Domain.Common;
using AiNative.Domain.Identity;
using AiNative.Domain.Knowledge;

namespace AiNative.UnitTests.Domain;

public sealed class KnowledgeTests
{
    [Fact]
    public void Memories_are_owned_by_their_author_unless_shared()
    {
        var agent = Actor.ForAgent(Guid.NewGuid());
        var user = Actor.ForUser(Guid.NewGuid());

        MemoryEntry.OwnedBy(agent, shared: false).Should().Be((MemoryScope.Agent, agent.Id));
        MemoryEntry.OwnedBy(user, shared: false).Should().Be((MemoryScope.User, user.Id));
        MemoryEntry.OwnedBy(user, shared: true).Should().Be((MemoryScope.Tenant, (Guid?)null));
    }

    [Fact]
    public void Private_memories_are_visible_only_to_their_owner()
    {
        var agent = Actor.ForAgent(Guid.NewGuid());
        var other = Actor.ForAgent(Guid.NewGuid());
        var (scope, owner) = MemoryEntry.OwnedBy(agent, shared: false);
        var memory = new MemoryEntry { Scope = scope, OwnerId = owner, Content = "x", ContentHash = "h" };
        var shared = new MemoryEntry { Scope = MemoryScope.Tenant, Content = "y", ContentHash = "h2" };

        memory.IsVisibleTo(agent).Should().BeTrue();
        memory.IsVisibleTo(other).Should().BeFalse();
        shared.IsVisibleTo(other).Should().BeTrue();
    }

    [Fact]
    public void Document_content_change_is_detected_by_hash()
    {
        var document = new KnowledgeDocument { Title = "制度" };

        document.SetContent("内容", "h1").Should().BeTrue();
        document.SetContent("内容", "h1").Should().BeFalse();
        document.SetContent("新内容", "h2").Should().BeTrue();
        document.Content.Should().Be("新内容");
    }

    [Fact]
    public void Api_key_cannot_be_revoked_twice_and_expires()
    {
        var now = DateTimeOffset.UtcNow;
        var key = new ApiKey { Name = "k", Prefix = "ank_x", SecretHash = "h", ExpiresAt = now.AddDays(1) };

        key.IsActive(now).Should().BeTrue();
        key.IsActive(now.AddDays(2)).Should().BeFalse();
        key.Revoke(now);
        key.IsActive(now).Should().BeFalse();
        var again = () => key.Revoke(now);
        again.Should().Throw<DomainException>();
    }
}
