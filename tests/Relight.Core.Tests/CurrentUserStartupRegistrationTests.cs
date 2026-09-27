using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class CurrentUserStartupRegistrationTests
{
    [Fact]
    public void Enable_disable_and_rollback_use_quoted_current_user_command()
    {
        var registry = new FakeRegistry();
        var registration = new CurrentUserStartupRegistration(
            @"C:\Program Files\Relight\Relight.exe", registry);

        StartupRegistrationChange enabled = registration.Apply(true);
        Assert.Equal("\"C:\\Program Files\\Relight\\Relight.exe\" --tray", registry.Value);
        Assert.True(registration.Inspect().EnabledForThisExecutable);
        enabled.Rollback();
        Assert.Null(registry.Value);

        registration.Apply(true);
        StartupRegistrationChange disabled = registration.Apply(false);
        Assert.Null(registry.Value);
        disabled.Rollback();
        Assert.True(registration.Inspect().EnabledForThisExecutable);
    }

    [Fact]
    public void Moved_relight_registration_can_be_updated_but_unrelated_value_is_preserved()
    {
        var registry = new FakeRegistry
        {
            Value = "\"C:\\Old Folder\\Relight.exe\" --tray"
        };
        var registration = new CurrentUserStartupRegistration(
            @"C:\New Folder\Relight.exe", registry);
        Assert.True(registration.Inspect().RegisteredToAnotherRelightExecutable);
        StartupRegistrationChange change = registration.Apply(true);
        Assert.True(registration.Inspect().EnabledForThisExecutable);
        change.Rollback();
        Assert.Equal("\"C:\\Old Folder\\Relight.exe\" --tray", registry.Value);

        registry.Value = "powershell.exe -File unrelated.ps1";
        Assert.True(registration.Inspect().ConflictingValue);
        Assert.Throws<InvalidOperationException>(() => registration.Apply(true));
        Assert.Throws<InvalidOperationException>(() => registration.Apply(false));
        Assert.Equal("powershell.exe -File unrelated.ps1", registry.Value);
    }

    [Fact]
    public void Rollback_does_not_overwrite_external_change()
    {
        var registry = new FakeRegistry();
        var registration = new CurrentUserStartupRegistration(@"C:\Relight\Relight.exe",
            registry);
        StartupRegistrationChange change = registration.Apply(true);
        registry.Value = "external change";
        Assert.Throws<InvalidOperationException>(change.Rollback);
        Assert.Equal("external change", registry.Value);
    }

    [Fact]
    public void Rejects_a_command_exceeding_the_windows_run_value_limit()
    {
        string longDirectory = @"C:\" + new string('a', 250);
        Assert.Throws<ArgumentException>(() => new CurrentUserStartupRegistration(
            Path.Combine(longDirectory, "Relight.exe"), new FakeRegistry()));
    }

    private sealed class FakeRegistry : IStartupRegistryValue
    {
        public string? Value { get; set; }
        public string? Read() => Value;
        public void Write(string? command) => Value = command;
    }
}
