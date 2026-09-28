using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Relight.Windows;

public sealed record RunningExecutableCandidate(string Name, string ExecutablePath,
    int ProcessId, long StartedUtcTicks)
{
    public string Display => $"{Name} (PID {ProcessId}) — {ExecutablePath}";
}

public sealed record RunningExecutableCatalogResult(
    IReadOnlyList<RunningExecutableCandidate> Candidates, int Skipped);

/// <summary>
/// Read-only setup catalog. A selection supplies an executable path, never a
/// process identity for recovery; registration performs fresh discovery.
/// </summary>
public sealed class RunningExecutableCatalog
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;
    private const int TokenUser = 1;

    public Task<RunningExecutableCatalogResult> ListAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => List(cancellationToken), cancellationToken);

    private static RunningExecutableCatalogResult List(CancellationToken cancellationToken)
    {
        using Process current = Process.GetCurrentProcess();
        int sessionId = current.SessionId;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userSid = identity.User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var candidates = new List<RunningExecutableCandidate>();
        int skipped = 0;
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.Id == current.Id || process.HasExited ||
                        process.SessionId != sessionId) continue;
                    string? path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) ||
                        !string.Equals(Path.GetExtension(path), ".exe",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }
                    var target = new ExecutableTarget(path, []);
                    target.Validate();
                    using SafeProcessHandle handle = OpenProcess(
                        ProcessQueryLimitedInformation, false, process.Id);
                    if (handle.IsInvalid || !IsUnpackaged(handle))
                    {
                        skipped++;
                        continue;
                    }
                    if (!string.Equals(ReadOwnerSid(handle), userSid,
                            StringComparison.Ordinal))
                    {
                        skipped++;
                        continue;
                    }
                    candidates.Add(new(process.ProcessName, target.CanonicalPath,
                        process.Id, process.StartTime.ToUniversalTime().Ticks));
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Exception error) when (error is Win32Exception or
                    UnauthorizedAccessException or
                    InvalidOperationException or ArgumentException)
                {
                    skipped++;
                }
            }
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
        return new(candidates.OrderBy(item => item.Name,
            StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.ProcessId)
            .ToArray(), skipped);
    }

    private static string ReadOwnerSid(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (GetTokenInformation(token, TokenUser, IntPtr.Zero, 0,
                    out int length) || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer ||
                length is <= 0 or > 4096)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, length, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                return new SecurityIdentifier(sid).Value;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static bool IsUnpackaged(SafeProcessHandle process)
    {
        uint length = 0;
        return GetPackageFamilyName(process, ref length, null) ==
            AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process,
        ref uint packageFamilyNameLength, System.Text.StringBuilder? packageFamilyName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process,
        uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token,
        int informationClass, IntPtr information, int informationLength,
        out int returnLength);
}
