using System.Text;
using System.Text.Json;

namespace Relight.Storage;

public sealed record ApplicationDiagnosticExport(int Records, int Skipped);

public sealed class ApplicationDiagnosticReader(string dataDirectory)
{
    public async Task<ApplicationDiagnosticExport> ExportAsync(Stream output, CancellationToken token = default)
    {
        string directory = Path.Combine(Path.GetFullPath(dataDirectory), "Logs", "Diagnostics");
        if (!Directory.Exists(directory)) return new(0, 0);
        var retained = new Queue<byte[]>(); int bytes = 0, skipped = 0;
        var files = new DirectoryInfo(directory).EnumerateFiles("application-*.jsonl")
            .Where(f => DiagnosticFileJournal.OwnedName.IsMatch(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(32).OrderBy(f => f.LastWriteTimeUtc).ToArray();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 2 * 1024 * 1024) { skipped++; continue; }
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 32768) { skipped++; continue; }
                    ApplicationDiagnosticLine? record;
                    try { record = JsonSerializer.Deserialize<ApplicationDiagnosticLine>(line, DiagnosticFileJournal.Json); }
                    catch (JsonException) { skipped++; continue; }
                    if (record is null || record.SchemaVersion != 1 || record.SessionId == Guid.Empty || record.Entry is null)
                    { skipped++; continue; }
                    byte[] clean = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, DiagnosticFileJournal.Json) + "\n");
                    retained.Enqueue(clean); bytes += clean.Length;
                    while (retained.Count > 500 || bytes > 2 * 1024 * 1024) bytes -= retained.Dequeue().Length;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
        }
        foreach (byte[] line in retained) await output.WriteAsync(line, token).ConfigureAwait(false);
        return new(retained.Count, skipped);
    }
}
