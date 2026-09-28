using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Relight.Windows;

/// <summary>
/// Identifies this token's logon session, not merely its reusable Windows
/// session number. Only the opaque storage key should appear in file names.
/// </summary>
public sealed record WindowsLogonSessionIdentity(
    string UserSid, int SessionId, ulong AuthenticationId)
{
    public string StorageKey => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{UserSid}|{SessionId}|{AuthenticationId:X16}")))[..32];

    public static WindowsLogonSessionIdentity Current()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Logon-session identity requires Windows.");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userSid = identity.User?.Value ??
            throw new InvalidOperationException("The current user SID is unavailable.");
        using Process process = Process.GetCurrentProcess();
        int sessionId = process.SessionId;
        if (sessionId < 0)
            throw new InvalidOperationException("The current Windows session ID is unavailable.");
        SafeAccessTokenHandle token = identity.AccessToken;
        if (!GetTokenInformation(token, TokenStatisticsClass, out TokenStatistics statistics,
                (uint)Marshal.SizeOf<TokenStatistics>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The logon authentication ID could not be read.");
        ulong authenticationId = ((ulong)(uint)statistics.AuthenticationId.HighPart << 32) |
            statistics.AuthenticationId.LowPart;
        if (authenticationId == 0)
            throw new InvalidOperationException("The logon authentication ID is unavailable.");
        return new(userSid, sessionId, authenticationId);
    }

    private const int TokenStatisticsClass = 10;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId;
        public Luid AuthenticationId;
        public long ExpirationTime;
        public int TokenType;
        public int ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public Luid ModifiedId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass, out TokenStatistics tokenInformation,
        uint tokenInformationLength, out uint returnLength);
}
