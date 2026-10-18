using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Relight.Windows;

internal sealed partial class WindowsStorageScanFileSystem : IStorageScanFileSystem, ICleanupFileSystem
{
    private const uint ExcludedAttributes = 0x400 | 0x1000 | 0x40000 | 0x400000;
    private const uint DirectoryAttribute = 0x10;

    public StorageFileMetadata Read(string path)
    {
        using var handle = Open(path, enumerate: false);
        return ReadHandle(handle);
    }

    private static StorageFileMetadata ReadHandle(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var basic)) ThrowLastError();
        bool directory = (basic.Attributes & DirectoryAttribute) != 0;
        bool excluded = (basic.Attributes & ExcludedAttributes) != 0;
        if (directory || excluded) return new(directory, excluded, "", 0, 0);
        if (!GetStandardInfo(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())) ThrowLastError();
        if (!GetIdInfo(handle, 18, out var id, (uint)Marshal.SizeOf<IdInfo>())) ThrowLastError();
        if (standard.DeletePending != 0 || standard.Directory != 0)
            throw new IOException("Entry changed or is pending deletion.");
        long allocated = standard.AllocationSize;
        if ((basic.Attributes & (0x800 | 0x200)) != 0) // Compressed or sparse file.
        {
            if (!GetCompressionInfo(handle, 8, out var compression, (uint)Marshal.SizeOf<CompressionInfo>())) ThrowLastError();
            allocated = compression.Size;
        }
        return new(false, false, $"{id.Volume:X16}:{id.Low:X16}{id.High:X16}",
            standard.EndOfFile, allocated);
    }

    public IEnumerable<string> Enumerate(string directory)
    {
        // Enumerate the verified object by handle, allowing applications to rename/delete normally.
        using var guard = Open(directory, enumerate: true);
        if (!GetFileInformationByHandle(guard, out var info)) ThrowLastError();
        if ((info.Attributes & ExcludedAttributes) != 0 || (info.Attributes & DirectoryAttribute) == 0)
            throw new IOException("Directory changed or became a reparse/offline entry; skipped.");
        const int bufferSize = 64 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            while (true)
            {
                // FILE_FULL_DIR_INFO: fixed header through EaSize is 68 bytes.
                if (!GetDirectoryInfo(guard, 14, buffer, bufferSize))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 18) yield break; // ERROR_NO_MORE_FILES.
                    throw new Win32Exception(error);
                }
                int offset = 0;
                while (true)
                {
                    int next = Marshal.ReadInt32(buffer, offset);
                    int nameBytes = Marshal.ReadInt32(buffer, offset + 60);
                    if (nameBytes < 0 || nameBytes % 2 != 0 || offset + 68L + nameBytes > bufferSize)
                        throw new IOException("Invalid directory metadata returned by Windows.");
                    string name = Marshal.PtrToStringUni(buffer + offset + 68, nameBytes / 2)!;
                    if (name != "." && name != "..") yield return Path.Combine(directory, name);
                    if (next == 0) break;
                    if (next < 68 || offset + (long)next + 68 > bufferSize)
                        throw new IOException("Invalid directory metadata offset returned by Windows.");
                    offset += next;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafeFileHandle Open(string path, bool enumerate, uint? access = null, uint share = 7)
    {
        // Only directory-list or zero content access; sharing includes write/delete.
        // OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT | OPEN_NO_RECALL.
        var handle = CreateFile(path, access ?? (enumerate ? 1u : 0u), share, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            // Reject ancestor junctions and renamed paths before using the opened object.
            var actual = new StringBuilder(512);
            uint length = GetFinalPathNameByHandle(handle, actual, (uint)actual.Capacity, 0);
            if (length == 0) ThrowLastError();
            if (length >= actual.Capacity && length < 32768)
            {
                actual = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandle(handle, actual, (uint)actual.Capacity, 0);
                if (length == 0) ThrowLastError();
            }
            if (length >= actual.Capacity) throw new IOException("Canonical path exceeds the scan limit.");
            string canonical = actual.ToString();
            if (canonical.StartsWith(@"\\?\", StringComparison.Ordinal)) canonical = canonical[4..];
            if (!string.Equals(Path.TrimEndingDirectorySeparator(canonical),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Path changed or resolves through an alias/reparse ancestor; skipped.");
        }
        catch { handle.Dispose(); throw; }
        return handle;
    }

    private static void ThrowLastError() => throw new Win32Exception(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint Links;
        public byte DeletePending, Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume, Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompressionInfo
    {
        public long Size;
        public ushort Format;
        public byte UnitShift, ChunkShift, ClusterShift, Reserved1, Reserved2, Reserved3;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BasicInfo info);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle file, int infoClass, out StandardInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdInfo(SafeFileHandle file, int infoClass, out IdInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCompressionInfo(SafeFileHandle file, int infoClass, out CompressionInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDirectoryInfo(SafeFileHandle file, int infoClass, IntPtr buffer, int size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
