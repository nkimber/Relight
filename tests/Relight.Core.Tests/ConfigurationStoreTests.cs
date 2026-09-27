using Relight.Core;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class ConfigurationStoreTests
{
    [Fact]
    public void Invalid_external_edit_shows_last_good_and_suspends_automatic_actions()
    {
        using var directory = new TestDirectory();
        var store = new ConfigurationStore(directory.Path);
        StoredConfiguration first = store.Initialize(RelightConfiguration.Empty);
        RelightConfiguration populated = WithProfile();
        StoredConfiguration saved = store.Save(first, populated);
        Assert.Equal(2, saved.Revision);

        string file = Path.Combine(directory.Path, "configuration.json");
        File.WriteAllText(file, "{ invalid external edit");
        StoredConfiguration fallback = store.Load();
        Assert.True(fallback.FromLastGoodBackup);
        Assert.False(fallback.AutomaticActionsAllowed);
        Assert.Empty(fallback.Configuration.Profiles);
        Assert.NotNull(fallback.Diagnostic);
        Assert.Throws<ConfigurationUnavailableException>(() => store.Save(fallback, populated));
        Assert.Equal("{ invalid external edit", File.ReadAllText(file));
    }

    [Fact]
    public void Stale_revision_or_same_revision_external_edit_cannot_be_overwritten()
    {
        using var directory = new TestDirectory();
        var store = new ConfigurationStore(directory.Path);
        StoredConfiguration first = store.Initialize(RelightConfiguration.Empty);
        StoredConfiguration second = store.Save(first, WithProfile());
        Assert.Throws<StaleConfigurationException>(() => store.Save(first, WithProfile()));

        string file = Path.Combine(directory.Path, "configuration.json");
        string text = File.ReadAllText(file);
        File.WriteAllText(file, text.Replace("\"revision\": 2", "\"revision\" : 2"));
        Assert.Throws<StaleConfigurationException>(() => store.Save(second, WithProfile()));
        Assert.Single(store.Load().Configuration.Profiles);
    }

    [Fact]
    public void Invalid_save_and_missing_configuration_preserve_existing_evidence()
    {
        using var directory = new TestDirectory();
        var store = new ConfigurationStore(directory.Path);
        Assert.Throws<ConfigurationUnavailableException>(() => store.Load());
        StoredConfiguration original = store.Initialize(RelightConfiguration.Empty);
        string file = Path.Combine(directory.Path, "configuration.json");
        string before = File.ReadAllText(file);

        RelightConfiguration invalid = WithProfile() with
        {
            Profiles = [WithProfile().Profiles[0] with
                { Policy = RecoveryPolicy.Default with { MaximumAutomaticAttempts = 21 } }]
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(original, invalid));
        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal(1, store.Load().Revision);
        Assert.Throws<InvalidOperationException>(() => store.Initialize(RelightConfiguration.Empty));
    }

    [Fact]
    public void Duplicate_enabled_target_is_rejected_but_distinct_disabled_copy_is_allowed()
    {
        using var directory = new TestDirectory();
        var store = new ConfigurationStore(directory.Path);
        StoredConfiguration first = store.Initialize(RelightConfiguration.Empty);
        ProfileConfiguration one = WithProfile().Profiles[0];
        ProfileConfiguration copy = one with { Id = Guid.NewGuid(), Name = "Copy" };
        Assert.Throws<ArgumentException>(() => store.Save(first,
            new([one, copy], GlobalConfiguration.Default)));
        StoredConfiguration saved = store.Save(first,
            new([one, copy with { Enabled = false }], GlobalConfiguration.Default));
        Assert.Equal(2, saved.Configuration.Profiles.Count);
    }

    [Fact]
    public void Argument_selectors_must_prove_disjoint_targets_before_both_can_be_enabled()
    {
        using var directory = new TestDirectory();
        var store = new ConfigurationStore(directory.Path);
        StoredConfiguration first = store.Initialize(RelightConfiguration.Empty);
        ProfileConfiguration one = WithProfile().Profiles[0] with
        {
            Target = WithProfile().Profiles[0].Target with { RequiredArgument = "--main" }
        };
        ProfileConfiguration two = one with
        {
            Id = Guid.NewGuid(),
            Name = "Other role",
            Target = one.Target with { RequiredArgument = "--helper" }
        };
        Assert.Throws<ArgumentException>(() => store.Save(first,
            new([one, two], GlobalConfiguration.Default)));

        two = two with { Target = two.Target with { ExcludedArgument = "--main" } };
        StoredConfiguration saved = store.Save(first,
            new([one, two], GlobalConfiguration.Default));
        Assert.Equal(2, saved.Configuration.Profiles.Count);
    }

    private static RelightConfiguration WithProfile() => new(
        [new ProfileConfiguration(Guid.NewGuid(), "Disposable app", true,
            new(TargetKind.Executable, @"C:\Windows\System32\notepad.exe", []),
            RecoveryPolicy.Default)],
        GlobalConfiguration.Default);

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-config-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
