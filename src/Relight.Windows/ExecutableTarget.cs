namespace Relight.Windows;

/// <summary>
/// Executable launch data and independently validated process identity. Optional
/// command-line selectors are used only when needed to distinguish same-path roles.
/// </summary>
public sealed record ExecutableTarget(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    string? RequiredArgument = null,
    string? ExcludedArgument = null)
{
    public string CanonicalPath => Path.GetFullPath(
        Environment.ExpandEnvironmentVariables(ExecutablePath));

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath))
            throw new ArgumentException("Executable path is required.");
        if (!Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(ExecutablePath)) ||
            !string.Equals(Path.GetExtension(CanonicalPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A fully qualified executable path is required.");
        if (CanonicalPath.Split(Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries).Any(segment =>
                string.Equals(segment, "WindowsApps", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                "Packaged applications require an installed-app adapter; a versioned WindowsApps executable path is not a durable launch identity.");
        if (Arguments is null || Arguments.Any(value => value is null || value.Contains('\0')))
            throw new ArgumentException("Launch arguments are invalid.");
        if (WorkingDirectory is { Length: > 0 } directory &&
            !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(directory)))
            throw new ArgumentException("Working directory must be fully qualified.");
        if (RequiredArgument is { Length: 0 } || ExcludedArgument is { Length: 0 })
            throw new ArgumentException("Argument selectors cannot be empty.");
    }
}
