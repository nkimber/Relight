using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Relight.Services;

internal sealed class InstalledVersionMonitor(string dataDirectory, string currentExecutable)
{
    private readonly string _versions = Path.GetFullPath(Path.Combine(dataDirectory, "Versions")) +
        Path.DirectorySeparatorChar;
    private readonly string _pointer = Path.Combine(dataDirectory, "installed-version.json");
    private readonly string _current = Path.GetFullPath(currentExecutable);

    public async Task<string?> FindUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_pointer)) return null;
        await using FileStream stream = new(_pointer, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument document = await JsonDocument.ParseAsync(stream,
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("ExecutablePath", out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The installed-version pointer has no executable path.");
        string? path = value.GetString();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The installed-version path is invalid.");
        path = Path.GetFullPath(path);
        if (!path.StartsWith(_versions, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(path), "Relight.exe",
                StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("The installed-version executable is unavailable.");
        return string.Equals(path, _current, StringComparison.OrdinalIgnoreCase) ? null : path;
    }
}
