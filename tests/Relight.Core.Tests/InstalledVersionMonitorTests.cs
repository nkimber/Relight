using Relight.Services;

namespace Relight.Core.Tests;

public sealed class InstalledVersionMonitorTests
{
    [Fact]
    public async Task Pointer_selects_only_a_complete_version_inside_the_install_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "RelightVersionTest-" + Guid.NewGuid());
        try
        {
            string current = Path.Combine(root, "Versions", "old", "Relight.exe");
            string next = Path.Combine(root, "Versions", "new", "Relight.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            Directory.CreateDirectory(Path.GetDirectoryName(next)!);
            await File.WriteAllTextAsync(current, "old");
            await File.WriteAllTextAsync(next, "new");
            var monitor = new InstalledVersionMonitor(root, current);
            string pointer = Path.Combine(root, "installed-version.json");

            Assert.Null(await monitor.FindUpdateAsync());
            await File.WriteAllTextAsync(pointer,
                System.Text.Json.JsonSerializer.Serialize(new { ExecutablePath = current }));
            Assert.Null(await monitor.FindUpdateAsync());
            await File.WriteAllTextAsync(pointer,
                System.Text.Json.JsonSerializer.Serialize(new { ExecutablePath = next }));
            Assert.Equal(next, await monitor.FindUpdateAsync());

            string outside = Path.Combine(root, "other", "Relight.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
            await File.WriteAllTextAsync(outside, "other");
            await File.WriteAllTextAsync(pointer,
                System.Text.Json.JsonSerializer.Serialize(new { ExecutablePath = outside }));
            await Assert.ThrowsAsync<InvalidDataException>(() => monitor.FindUpdateAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
