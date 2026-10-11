using AiNative.Application.Access;
using AiNative.Application.Modules;

namespace AiNative.UnitTests.Application;

public sealed class ModuleCatalogTests
{
    private sealed class TestModule(string key, bool isCore = false, bool enabledByDefault = true, params string[] dependsOn) : IPlatformModule
    {
        public string Key => key;

        public string Name => key;

        public string Description => key;

        public bool IsCore => isCore;

        public bool EnabledByDefault => enabledByDefault;

        public IReadOnlyList<FeatureDefinition> Features => [];

        public IReadOnlyList<string> DependsOn => dependsOn;
    }

    private static readonly ModuleCatalog Catalog = new(
    [
        new TestModule("platform", isCore: true),
        new TestModule("crm"),
        new TestModule("erp", dependsOn: "crm"),
        new TestModule("mrp", dependsOn: "erp"),
        new TestModule("beta", enabledByDefault: false),
        new TestModule("loop-a", dependsOn: "loop-b"),
        new TestModule("loop-b", dependsOn: "loop-a"),
        new TestModule("orphan", dependsOn: "missing"),
    ]);

    [Fact]
    public void Defaults_apply_when_nothing_is_set_and_broken_dependencies_stay_off()
        => Catalog.ResolveEnabled(new Dictionary<string, bool>()).Should().BeEquivalentTo("platform", "crm", "erp", "mrp");

    [Fact]
    public void A_disabled_dependency_turns_off_everything_built_on_it()
    {
        var enabled = Catalog.ResolveEnabled(new Dictionary<string, bool> { ["crm"] = false, ["beta"] = true, ["platform"] = false });

        enabled.Should().BeEquivalentTo("platform", "beta");
    }
}
