using System.Diagnostics;

namespace Relight.Storage;

/// <summary>
/// Cross-process file sharing keeps owned-log retention out of an export scan.
/// The empty lock file lives outside Logs and is never part of retention.
/// </summary>
internal static class OperationalLogRetentionGate
{
    internal const string FileName = ".relight-log-retention.lock";

    internal static FileStream? TryEnterRetention(string dataDirectory)
    {
        try
        {
            return new FileStream(Path.Combine(dataDirectory, FileName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (IsSharingViolation(error))
        {
            // A reader is exporting. The next append retries retention.
            return null;
        }
    }

    internal static async Task<FileStream> EnterExportAsync(string dataDirectory,
        CancellationToken cancellationToken)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(dataDirectory, FileName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (IOException error) when (IsSharingViolation(error) &&
                timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException error) =>
        (error.HResult & 0xFFFF) is 32 or 33;
}
