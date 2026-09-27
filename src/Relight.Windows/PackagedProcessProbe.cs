using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Relight.Windows;

public sealed record PackagedProcessCandidate(int ProcessId, long StartedUtcTicks,
    bool HasTypeSwitch);
public sealed record PackagedProcessInspection(
    IReadOnlyList<PackagedProcessCandidate> Candidates, string? Problem);

/// <summary>
/// Read-only package-family inspection. Multiple processes are reported as
/// candidates; this probe does not choose a main process or authorize recovery.
/// </summary>
public sealed class PackagedProcessProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private readonly string _familyName;
    private readonly string _processName;
    private readonly int _sessionId;
    private readonly string _userSid;

    public PackagedProcessProbe(string familyName, string executableName)
    {
        if (string.IsNullOrWhiteSpace(familyName) || familyName.Contains('\\') ||
            familyName.Contains('/') || familyName.Length > 255)
            throw new ArgumentException("A package family name is required.", nameof(familyName));
        if (string.IsNullOrWhiteSpace(executableName) ||
            !string.Equals(Path.GetFileName(executableName), executableName,
                StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(executableName), ".exe",
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An executable file name is required.",
                nameof(executableName));
        _familyName = familyName;
        _processName = Path.GetFileNameWithoutExtension(executableName);
        using Process current = Process.GetCurrentProcess();
        _sessionId = current.SessionId;
        _userSid = WindowsIdentity.GetCurrent().User?.Value ??
            throw new InvalidOperationException("The current user SID is unavailable.");
    }

    public Task<PackagedProcessInspection> InspectAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Inspect(cancellationToken), cancellationToken);

    private PackagedProcessInspection Inspect(CancellationToken cancellationToken)
    {
        var candidates = new List<PackagedProcessCandidate>();
        string? problem = null;
        try
        {
            foreach (Process process in Process.GetProcessesByName(_processName))
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (process.HasExited || process.SessionId != _sessionId) continue;
                        using SafeProcessHandle handle = OpenProcess(
                            ProcessQueryLimitedInformation, false, process.Id);
                        if (handle.IsInvalid)
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        string? family = GetFamilyName(handle);
                        if (family is null || !string.Equals(family, _familyName,
                                StringComparison.OrdinalIgnoreCase)) continue;
                        ProcessMetadata? metadata = GetProcessMetadata(process.Id);
                        if (metadata is null)
                        {
                            if (process.HasExited) continue;
                            throw new InvalidOperationException("A packaged process disappeared during inspection.");
                        }
                        if (metadata.OwnerSid is null)
                            throw new InvalidOperationException("A packaged process owner could not be verified.");
                        if (!string.Equals(metadata.OwnerSid, _userSid, StringComparison.Ordinal)) continue;
                        if (metadata.CommandLine is null)
                            throw new InvalidOperationException("A packaged process command line could not be read.");
                        candidates.Add(new(process.Id,
                            process.StartTime.ToUniversalTime().Ticks,
                            Regex.IsMatch(metadata.CommandLine, @"(?:^|\s)--type=\S+",
                                RegexOptions.CultureInvariant)));
                    }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    catch (Exception error) when (error is Win32Exception or
                        UnauthorizedAccessException or ManagementException or
                        InvalidOperationException)
                    {
                        problem ??= $"A candidate process could not be verified: {error.Message}";
                    }
                }
            }
        }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException)
        {
            problem ??= $"Process enumeration failed: {error.Message}";
        }
        return new(candidates, problem);
    }

    private static string? GetFamilyName(SafeProcessHandle handle)
    {
        uint length = 0;
        int result = GetPackageFamilyName(handle, ref length, null);
        if (result == AppModelErrorNoPackage) return null;
        if (result != ErrorInsufficientBuffer || length is 0 or > 512)
            throw new Win32Exception(result, "Package family could not be read.");
        var buffer = new StringBuilder((int)length);
        result = GetPackageFamilyName(handle, ref length, buffer);
        if (result != 0)
            throw new Win32Exception(result, "Package family could not be read.");
        return buffer.ToString();
    }

    private sealed record ProcessMetadata(string? OwnerSid, string? CommandLine);

    private static ProcessMetadata? GetProcessMetadata(int processId)
    {
        using var process = new ManagementObject($"Win32_Process.Handle='{processId}'");
        try { process.Get(); }
        catch (ManagementException error) when (error.ErrorCode == ManagementStatus.NotFound)
        {
            return null;
        }
        using ManagementBaseObject? owner = process.InvokeMethod("GetOwnerSid", null, null);
        string? sid = owner?["ReturnValue"] is uint result && result == 0
            ? owner["Sid"] as string : null;
        return new(sid, process["CommandLine"] as string);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process,
        ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);
}
