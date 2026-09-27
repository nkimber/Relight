using System.Diagnostics;
using Relight.Engine;

namespace Relight.Windows;

public sealed class ExecutableLauncher : IProcessLauncher
{
    private readonly ExecutableTarget _target;

    public ExecutableLauncher(ExecutableTarget target)
    {
        target.Validate();
        _target = target;
    }

    public Task LaunchAsync(Guid operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string executable = _target.CanonicalPath;
        if (!File.Exists(executable))
            throw new FileNotFoundException("The configured target executable is missing.", executable);
        string workingDirectory = _target.WorkingDirectory is { Length: > 0 } configured
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured))
            : Path.GetDirectoryName(executable)!;
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException("The configured working directory is missing.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in _target.Arguments)
            start.ArgumentList.Add(Environment.ExpandEnvironmentVariables(argument));

        using Process launched = Process.Start(start) ??
            throw new InvalidOperationException("Windows did not start the target process.");
        // Dispose only Relight's handle. The target must survive the supervisor.
        return Task.CompletedTask;
    }
}
