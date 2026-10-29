using Relight.ViewModels;

namespace Relight.Core.Tests;

public sealed class StorageNavigationTests
{
    [Fact]
    public void StorageIsIndependentTopLevelPageAndSettingsRemainsSeparate()
    {
        var model = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
        model.StorageCommand.Execute(null);
        Assert.True(model.IsStorage);
        Assert.False(model.IsMonitoring);
        Assert.False(model.IsSettings);
        Assert.False(model.IsDetailPage);
        Assert.Equal("Storage", model.Heading);
        model.SettingsCommand.Execute(null);
        Assert.True(model.IsSettings);
        Assert.False(model.IsStorage);
        Assert.True(model.IsDetailPage);
        model.IsStorage = true;
        Assert.Equal("Storage", model.Heading);
        model.MonitoringCommand.Execute(null);
        Assert.False(model.IsStorage);
        Assert.True(model.IsMonitoring);
    }

    [Fact]
    public async Task ReviewedWorkCanBeCanceledAndPreviewedAgainWithoutClosingWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "relight-workspace-cancel-" + Guid.NewGuid().ToString("N"));
        string files = Path.Combine(root, "files"); Directory.CreateDirectory(files);
        using var gate = new SemaphoreSlim(1);
        var model = new ReviewedCleanupViewModel(Path.Combine(root, "data"), gate);
        try
        {
            await model.LoadAsync(); model.Editor.Root = files;
            await gate.WaitAsync();
            var preview = model.PreviewAsync();
            Assert.True(model.CanCancel); Assert.False(model.IsIdle);
            model.Cancel(); await preview;
            Assert.True(model.IsIdle); Assert.False(model.CanCancel);
            gate.Release();
            await model.PreviewAsync();
            Assert.True(model.CanApproveAutomatic);
            Assert.False(File.Exists(Path.Combine(root, "data", "Storage", "cleanup-rules.json")));
        }
        finally { model.Close(); Directory.Delete(root, true); }
    }
}
