using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class DiskSpaceTests
{
    private const long GiB = 1_073_741_824;

    [Fact]
    public void ThresholdsUseEitherLimitAndExactBoundaryDoesNotTrigger()
    {
        var policy = new DiskSpaceDrivePolicy("C:\\");
        var alert = new DiskSpaceAlert();
        Assert.Equal(DiskSpaceLevel.Healthy, alert.Evaluate(policy, 20 * GiB, 100 * GiB));
        Assert.Equal(DiskSpaceLevel.Warning, alert.Evaluate(policy, 19 * GiB, 100 * GiB));
        Assert.Equal(DiskSpaceLevel.Warning, alert.Evaluate(policy, 90 * GiB, 1000 * GiB));
        Assert.Equal(DiskSpaceLevel.Critical, alert.Evaluate(policy, 4 * GiB, 100 * GiB));
        Assert.Equal(DiskSpaceLevel.Critical, alert.Evaluate(policy, 40 * GiB, 1000 * GiB));
        Assert.Equal(DiskSpaceLevel.Unavailable, alert.Evaluate(policy, 0, 0));
    }

    [Fact]
    public void UnknownDoesNotRearmAndRecoveryRequiresMargin()
    {
        var policy = new DiskSpaceDrivePolicy("C:\\");
        var alert = new DiskSpaceAlert();
        Assert.True(alert.Observe(DiskSpaceLevel.Warning));
        Assert.False(alert.Observe(DiskSpaceLevel.Warning));
        Assert.False(alert.Observe(DiskSpaceLevel.Unavailable));
        Assert.False(alert.Observe(DiskSpaceLevel.Warning));
        Assert.Equal(DiskSpaceLevel.Warning, alert.Evaluate(policy, 20 * GiB, 100 * GiB));
        Assert.Equal(DiskSpaceLevel.Healthy, alert.Evaluate(policy, 22 * GiB, 100 * GiB));
        alert.Observe(DiskSpaceLevel.Healthy);
        Assert.True(alert.Observe(DiskSpaceLevel.Warning));
        Assert.True(alert.Observe(DiskSpaceLevel.Critical));
        Assert.False(alert.Observe(DiskSpaceLevel.Critical));
        alert.Observe(DiskSpaceLevel.Warning);
        Assert.False(alert.Observe(DiskSpaceLevel.Critical));
    }

    [Fact]
    public void FailedDriveDoesNotHideOtherReadingsOrCauseRepeatedNotice()
    {
        bool fail = false;
        var monitor = new DiskSpaceMonitor(root => root == "D:\\" || fail
            ? throw new IOException("Unavailable test drive") : (4 * GiB, 100 * GiB));
        var settings = new DiskSpaceSettings { Drives = [new("C:\\"), new("D:\\")] };
        var first = monitor.Sample(settings);
        Assert.True(first[0].Notify);
        Assert.Equal(DiskSpaceLevel.Unavailable, first[1].Level);
        Assert.False(first[1].Notify);
        fail = true;
        Assert.All(monitor.Sample(settings), r => Assert.Equal(DiskSpaceLevel.Unavailable, r.Level));
        fail = false;
        Assert.False(monitor.Sample(settings)[0].Notify);
        Assert.Empty(monitor.Sample(settings with { Enabled = false }));
    }

    [Fact]
    public void MutingNotificationsRetainsDetection()
    {
        var monitor = new DiskSpaceMonitor(_ => (4 * GiB, 100 * GiB));
        var readings = monitor.Sample(new() { NotificationsEnabled = false });
        Assert.Equal(DiskSpaceLevel.Critical, Assert.Single(readings).Level);
        Assert.False(readings[0].Notify);
    }

    [Fact]
    public void InvalidSettingsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new DiskSpaceSettings { CheckIntervalSeconds = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new DiskSpaceDrivePolicy("C:\\temp").Validate());
        Assert.Throws<ArgumentException>(() => new DiskSpaceDrivePolicy("C:\\", WarningPercent: double.NaN).Validate());
        Assert.Throws<ArgumentException>(() => new DiskSpaceSettings { Drives = [new("C:\\"), new("c:\\")] }.Validate());
        Assert.Throws<ArgumentException>(() => new DiskSpaceDrivePolicy("C:\\", CriticalGigabytes: 20).Validate());
    }

    [Fact]
    public void SettingsRoundTripAndStaleWriterCannotOverwriteThem()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Relight-storage-test-" + Guid.NewGuid());
        try
        {
            var store = new ConfigurationStore(directory);
            var initial = store.Initialize(RelightConfiguration.Empty);
            Assert.True(initial.Configuration.Settings.Storage.Enabled);
            var storage = new DiskSpaceSettings { CheckIntervalSeconds = 25, ReminderIntervalMinutes = 120, ScanEntryLimit = 1000000, Drives = [new("D:\\")] };
            var next = initial.Configuration with { Settings = initial.Configuration.Settings with { Storage = storage } };
            store.Save(initial, next);
            var loaded = store.Load().Configuration.Settings.Storage;
            Assert.Equal(25, loaded.CheckIntervalSeconds);
            Assert.Equal(120, loaded.ReminderIntervalMinutes);
            Assert.Equal(1000000, loaded.ScanEntryLimit);
            Assert.Equal("D:\\", Assert.Single(loaded.Drives).Root);
            Assert.Throws<StaleConfigurationException>(() => store.Save(initial, initial.Configuration));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void NativeSystemDriveReadingIsValid()
    {
        var reading = Assert.Single(new DiskSpaceMonitor().Sample(new()));
        Assert.Null(reading.Problem);
        Assert.NotEqual(DiskSpaceLevel.Unavailable, reading.Level);
        Assert.True(reading.TotalBytes > 0);
        Assert.True(reading.AvailableBytes >= 0);
    }

    [Fact]
    public void LegacyConfigurationLoadsDefaultsAndInvalidStorageUsesLastGood()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Relight-storage-test-" + Guid.NewGuid());
        try
        {
            var store = new ConfigurationStore(directory);
            store.Initialize(RelightConfiguration.Empty);
            string path = Path.Combine(directory, "configuration.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["configuration"]!["settings"]!.AsObject().Remove("storage");
            File.WriteAllText(path, json.ToJsonString());
            var legacy = store.Load();
            Assert.Equal(60, legacy.Configuration.Settings.Storage.CheckIntervalSeconds);
            Assert.Equal(0, legacy.Configuration.Settings.Storage.ReminderIntervalMinutes);
            Assert.Equal(100000, legacy.Configuration.Settings.Storage.ScanEntryLimit);
            store.Save(legacy, legacy.Configuration);
            json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["configuration"]!["settings"]!["storage"]!["checkIntervalSeconds"] = 0;
            File.WriteAllText(path, json.ToJsonString());
            var fallback = store.Load();
            Assert.True(fallback.FromLastGoodBackup);
            Assert.False(fallback.AutomaticActionsAllowed);
            Assert.Equal(60, fallback.Configuration.Settings.Storage.CheckIntervalSeconds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HostSavesStoragePreferencesWithoutChangingRecoveryConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Relight-storage-host-" + Guid.NewGuid());
        try
        {
            await using (var host = await RecoveryApplicationHost.OpenSharedSessionPreviewAsync(directory))
            {
                var before = host.Configuration!.Configuration;
                await host.SetDiskSpaceSettingsAsync(new() { CheckIntervalSeconds = 15, ReminderIntervalMinutes = 30, Enabled = false });
                Assert.Equal(before.Profiles, host.Configuration!.Configuration.Profiles);
                Assert.Equal(before.Settings.StartAtSignIn, host.Configuration.Configuration.Settings.StartAtSignIn);
                Assert.False(host.Configuration.Configuration.Settings.Storage.Enabled);
                await Assert.ThrowsAsync<ArgumentException>(() => host.SetDiskSpaceSettingsAsync(
                    new() { CheckIntervalSeconds = 1 }));
                Assert.Equal(15, host.Configuration.Configuration.Settings.Storage.CheckIntervalSeconds);
            }
            await using var reopened = await RecoveryApplicationHost.OpenSharedSessionPreviewAsync(directory);
            Assert.False(reopened.Configuration!.Configuration.Settings.Storage.Enabled);
            Assert.Equal(15, reopened.Configuration.Configuration.Settings.Storage.CheckIntervalSeconds);
            Assert.Equal(30, reopened.Configuration.Configuration.Settings.Storage.ReminderIntervalMinutes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
