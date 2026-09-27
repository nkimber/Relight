using Relight.Core;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class ChatGptPackagedDiscoveryTests
{
    [Fact]
    public void Helpers_alone_do_not_hide_absent_main_process()
    {
        Detection found = ChatGptPackagedDiscovery.Interpret(new(
            [new(11, 100, true), new(12, 101, true)], null), 3);

        Assert.Equal(DetectionKind.Absent, found.Kind);
    }

    [Fact]
    public void Single_main_has_session_package_pid_and_start_identity()
    {
        Detection found = ChatGptPackagedDiscovery.Interpret(new(
            [new(11, 100, true), new(42, 200, false)], null), 3);

        Assert.Equal(DetectionKind.Present, found.Kind);
        Assert.Equal("3|OpenAI.Codex_2p2nqsd0c76g0|42|200", found.Identity);
    }

    [Fact]
    public void Ambiguous_or_unreadable_candidate_never_means_absent()
    {
        Detection ambiguous = ChatGptPackagedDiscovery.Interpret(new(
            [new(42, 200, false), new(43, 201, false)], null), 3);
        Detection unreadable = ChatGptPackagedDiscovery.Interpret(new(
            [new(11, 100, true)], "A candidate could not be verified."), 3);

        Assert.Equal(DetectionKind.Unavailable, ambiguous.Kind);
        Assert.Equal(DetectionKind.Unavailable, unreadable.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("OpenAI.Codex")]
    [InlineData("!App")]
    [InlineData("OpenAI.Codex!")]
    [InlineData("OpenAI.Codex!App!Other")]
    [InlineData("OpenAI.Codex!App\\Unsafe")]
    public void Launcher_rejects_invalid_application_ids(string value)
    {
        Assert.Throws<ArgumentException>(() => new PackagedApplicationLauncher(value));
    }
}
