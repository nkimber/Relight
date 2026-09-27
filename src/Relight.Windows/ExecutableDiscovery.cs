using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Relight.Core;
using Relight.Engine;

namespace Relight.Windows;

public sealed class ExecutableDiscovery : IProcessDiscovery
{
    private readonly ExecutableTarget _target;
    private readonly int _sessionId;
    private readonly string _userSid;

    public ExecutableDiscovery(ExecutableTarget target)
    {
        target.Validate();
        _target = target;
        using Process current = Process.GetCurrentProcess();
        _sessionId = current.SessionId;
        _userSid = WindowsIdentity.GetCurrent().User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
    }

    public Task<Detection> DetectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Detect(cancellationToken), cancellationToken);

    private Detection Detect(CancellationToken cancellationToken)
    {
        string expectedPath = _target.CanonicalPath;
        string name = Path.GetFileNameWithoutExtension(expectedPath);
        var matches = new List<string>();
        try
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (process.HasExited || process.SessionId != _sessionId) continue;
                        string? actualPath = process.MainModule?.FileName;
                        if (actualPath is null)
                            return Detection.Unavailable("A candidate process path could not be read.");
                        if (!string.Equals(Path.GetFullPath(actualPath), expectedPath,
                                StringComparison.OrdinalIgnoreCase)) continue;

                        ProcessMetadata? metadata = ReadProcessMetadata(process.Id);
                        if (metadata is null)
                        {
                            if (process.HasExited) continue;
                            return Detection.Unavailable("A candidate process owner could not be read.");
                        }
                        if (metadata.OwnerSid is null)
                            return Detection.Unavailable("A candidate process owner could not be verified.");
                        if (!string.Equals(metadata.OwnerSid, _userSid, StringComparison.Ordinal))
                            continue;

                        if (_target.RequiredArgument is not null ||
                            _target.ExcludedArgument is not null)
                        {
                            if (metadata.CommandLine is null)
                            {
                                if (process.HasExited) continue;
                                return Detection.Unavailable("A candidate process command line could not be read.");
                            }
                            IReadOnlyList<string> arguments = SplitWindowsCommandLine(metadata.CommandLine);
                            if (_target.RequiredArgument is { } required &&
                                !arguments.Contains(required, StringComparer.OrdinalIgnoreCase)) continue;
                            if (_target.ExcludedArgument is { } excluded &&
                                arguments.Contains(excluded, StringComparer.OrdinalIgnoreCase)) continue;
                        }

                        // Include start time to distinguish a reused PID. Do not expose
                        // command-line contents or application data in this identity.
                        long started = process.StartTime.ToUniversalTime().Ticks;
                        matches.Add($"{_sessionId}|{expectedPath}|{process.Id}|{started}");
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        // The candidate disappeared during enumeration. Polling will
                        // reconcile again; no stale PID is adopted.
                    }
                    catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or ManagementException)
                    {
                        return Detection.Unavailable($"Cannot inspect a candidate process: {error.Message}");
                    }
                }
            }
            return matches.Count switch
            {
                0 => Detection.Absent(),
                1 => Detection.Present(matches[0]),
                _ => Detection.Unavailable($"{matches.Count} matching instances; identity is ambiguous.")
            };
        }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or ManagementException)
        {
            return Detection.Unavailable($"Process discovery failed: {error.Message}");
        }
    }

    private sealed record ProcessMetadata(string? CommandLine, string? OwnerSid);

    private static ProcessMetadata? ReadProcessMetadata(int processId)
    {
        using var process = new ManagementObject($"Win32_Process.Handle='{processId}'");
        try { process.Get(); }
        catch (ManagementException error) when (error.ErrorCode == ManagementStatus.NotFound)
        {
            return null;
        }
        using ManagementBaseObject? owner = process.InvokeMethod("GetOwnerSid", null, null);
        string? sid = owner?["ReturnValue"] is uint result && result == 0
            ? owner["Sid"] as string
            : null;
        return new(process["CommandLine"] as string, sid);
    }

    private static IReadOnlyList<string> SplitWindowsCommandLine(string commandLine)
    {
        nint values = CommandLineToArgvW(commandLine, out int count);
        if (values == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                nint value = Marshal.ReadIntPtr(values, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringUni(value) ?? string.Empty;
            }
            return result;
        }
        finally { LocalFree(values); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
