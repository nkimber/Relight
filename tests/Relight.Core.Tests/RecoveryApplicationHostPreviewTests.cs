using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class RecoveryApplicationHostPreviewTests
{
    [Fact]
    public async Task Shared_preview_initializes_only_an_explicit_isolated_directory()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-shared-preview-{Guid.NewGuid():N}");
        try
        {
            await using var host = await RecoveryApplicationHost
                .OpenSharedSessionPreviewAsync(directory);
            Assert.NotNull(host.Configuration);
            Assert.Empty(host.Configuration.Configuration.Profiles);
            Assert.True(File.Exists(Path.Combine(directory, "configuration.json")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Shared_preview_rejects_normal_or_relative_data_directory()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            _ = RecoveryApplicationHost.OpenSharedSessionPreviewAsync(
                RecoveryApplicationHost.DefaultDataDirectory);
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = RecoveryApplicationHost.OpenSharedSessionPreviewAsync("relative-path");
        });
    }
}
