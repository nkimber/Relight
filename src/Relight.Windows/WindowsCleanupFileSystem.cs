using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Relight.Core;

namespace Relight.Windows;

public sealed class CleanupExcludedException(string message) : IOException(message);

public interface ICleanupFileSystem
{
    IEnumerable<string> Enumerate(string directory);
    CleanupFile ReadCleanup(string path);
    void DeleteVerified(CleanupFile expected);
}

internal sealed partial class WindowsStorageScanFileSystem
{
    public CleanupFile ReadCleanup(string path)
    {
        using var handle = Open(path, false);
        return CleanupMetadata(path, handle);
    }

    private static CleanupFile CleanupMetadata(string path, SafeFileHandle handle)
    {
        var measured = ReadHandle(handle);
        if (measured.IsDirectory || measured.Excluded)
            throw new CleanupExcludedException("Directories, reparse and cloud/offline entries are not cleanup candidates.");
        if (!GetBasicTimes(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicTimes>())) ThrowLastError();
        if (!GetStandardInfo(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())) ThrowLastError();
        if ((basic.Attributes & (1 | 2 | 4 | 0x4000)) != 0 || standard.Links != 1)
            throw new CleanupExcludedException("Read-only, hidden, system, encrypted or hard-linked files are excluded.");
        return new(path, measured.Identity, measured.LogicalBytes, measured.AllocatedBytes,
            DateTimeOffset.FromFileTime(basic.Write), basic.Change, standard.Links, basic.Attributes);
    }

    public void DeleteVerified(CleanupFile expected)
    {
        var ancestors = new List<SafeFileHandle>();
        try
        {
            var directories = new Stack<string>();
            for (string? path = Path.GetDirectoryName(expected.Path); path is not null; path = Path.GetDirectoryName(path))
                directories.Push(path);
            while (directories.TryPop(out string? path))
            {
                var guard = Open(path, false, share: 3); // Briefly pin ancestors against replacement.
                ancestors.Add(guard);
                var metadata = ReadHandle(guard);
                if (!metadata.IsDirectory || metadata.Excluded) throw new IOException("Cleanup ancestor changed or is excluded.");
            }
            // Exclusive DELETE handle rejects open readers/writers and path replacement.
            using var file = Open(expected.Path, false, access: 0x10080, share: 0);
            var actual = CleanupMetadata(expected.Path, file);
            if (actual != expected) throw new IOException("File changed since preview; generate a fresh preview.");
            byte delete = 1;
            if (!SetDisposition(file, 4, ref delete, 1)) ThrowLastError();
            // Closing this same verified handle completes permanent deletion; no pathname delete race.
        }
        finally { foreach (var handle in Enumerable.Reverse(ancestors)) handle.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicTimes { public long Creation, Access, Write, Change; public uint Attributes; }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasicTimes(SafeFileHandle file, int infoClass, out BasicTimes info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle file, int infoClass, ref byte delete, uint size);
}
