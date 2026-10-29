using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Relight.Core;

namespace Relight.Storage;

public enum DiagnosticKind { ProcessStarted, ProcessStopped, Heartbeat, ScanStarted, ScanStage, ScanFinished,
    ScanCanceled, ScanFailed, ScanReadIssue, ScanHeartbeat, DispatcherDelayed, DispatcherRecovered, UnhandledException, TaskException,
    MetricFailure, HandledException }
public enum DiagnosticScanStage { WaitingForWorker, Traversing, AwaitingUi, Saving, Comparing, Presenting, Completed, Canceled, Failed }
public sealed record DiagnosticFailure(string Type, int HResult, int? NativeCode, string[] Frames)
{
    public static DiagnosticFailure Capture(Exception error)
    {
        error = error.GetBaseException();
        return new(error.GetType().FullName ?? "Exception", error.HResult,
        error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : null,
        new StackTrace(error, false).GetFrames()?.Take(20).Select(f =>
        {
            var method = f.GetMethod(); string name = $"{method?.DeclaringType?.FullName}.{method?.Name}@IL{f.GetILOffset():X}";
            return name.Length > 300 ? name[..300] : name;
        }).ToArray() ?? []); // No messages, arguments, source-file paths or environment contents.
    }
}
public sealed record DiagnosticBuild(string Version, string Configuration, string Runtime, string OS,
    string Architecture, int Processors, int WindowsSession)
{ public string? ModuleId { get; init; } }
public sealed record DiagnosticResources(long WorkingSet, long PrivateBytes, long ManagedBytes, long HeapBytes,
    long FragmentedBytes, long MemoryLoad, long HighMemoryThreshold, long AllocatedBytes,
    int Gen0, int Gen1, int Gen2, double CpuSeconds, int Handles, int Threads,
    int PoolThreads, long PendingPoolItems, int AvailablePoolWorkers, int AvailableIoWorkers)
{ public long PeakWorkingSet { get; init; } public double GcPausePercent { get; init; } }
public sealed record DiagnosticDispatcher(double SecondsSinceReply, double ProbeAgeSeconds, bool Pending, bool Delayed);
public sealed record DiagnosticScan(Guid ScanId, DiagnosticScanStage Stage, string RootDrive, string RootTag, int EntryLimit,
    int Visited, int Measured, int Skipped, int Aliases, int Identities, int Depth,
    ScanIoOperation? Operation, string? PathTag, string? Suffix, bool IoPending, double OperationSeconds,
    double SecondsSinceProgress, double ElapsedSeconds, bool CancelRequested, bool? Complete = null, bool? LimitReached = null,
    string? LastFailureType = null, int? LastFailureHResult = null)
{ public double StageSeconds { get; init; } }
public sealed record ApplicationDiagnosticEntry(DiagnosticKind Kind, DiagnosticScan? Scan = null,
    DiagnosticResources? Resources = null, DiagnosticDispatcher? Dispatcher = null,
    DiagnosticFailure? Failure = null, DiagnosticBuild? Build = null);
public sealed record ApplicationDiagnosticLine(int SchemaVersion, Guid SessionId, int ProcessId,
    long Sequence, DateTimeOffset Utc, long LostRecords, ApplicationDiagnosticEntry Entry);

/// <summary>Owned, flushed JSONL files. Only the diagnostic worker calls Append.</summary>
public sealed class DiagnosticFileJournal : IDisposable
{
    internal static readonly Regex OwnedName = new(@"^application-[a-f0-9]{32}-[0-9]{6}\.jsonl$", RegexOptions.CultureInvariant);
    internal static readonly JsonSerializerOptions Json = CreateJson();
    private readonly string _directory;
    private readonly string _prefix;
    private readonly long _fileLimit, _totalLimit;
    private FileStream? _stream;
    private int _segment;
    public int RetentionBlocked { get; private set; }
    public string DirectoryPath => _directory;

    public DiagnosticFileJournal(string dataDirectory, Guid session, long fileLimit = 2 * 1024 * 1024,
        long totalLimit = 32 * 1024 * 1024)
    {
        if (fileLimit < 1024 || totalLimit < fileLimit) throw new ArgumentOutOfRangeException(nameof(fileLimit));
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "Logs", "Diagnostics");
        _prefix = $"application-{session:N}-"; _fileLimit = fileLimit; _totalLimit = totalLimit;
    }

    public void Append(ApplicationDiagnosticLine line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        if (bytes.Length > 32768 || bytes.Length > _fileLimit) throw new InvalidDataException("Diagnostic record exceeds its bound.");
        if (_stream is not null && _stream.Length + bytes.Length > _fileLimit) { _stream.Dispose(); _stream = null; _segment++; }
        if (_stream is null)
        {
            Directory.CreateDirectory(_directory);
            _stream = new FileStream(Path.Combine(_directory, $"{_prefix}{_segment:D6}.jsonl"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            Retain();
        }
        try { _stream.Write(bytes); _stream.Flush(true); }
        catch { _stream.Dispose(); _stream = null; _segment++; throw; }
    }

    private void Retain()
    {
        RetentionBlocked = 0;
        var files = new DirectoryInfo(_directory).EnumerateFiles("application-*.jsonl")
            .Where(f => OwnedName.IsMatch(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
        long total = 0; int own = 0;
        foreach (var file in files)
        {
            long length = file.Length;
            total += length;
            if (file.Name.StartsWith(_prefix, StringComparison.Ordinal)) own++;
            bool remove = total > _totalLimit || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7) ||
                (file.Name.StartsWith(_prefix, StringComparison.Ordinal) && own > 4);
            if (!remove) continue;
            try { file.Delete(); total -= length; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { RetentionBlocked++; }
        }
    }
    public void Dispose() { _stream?.Dispose(); _stream = null; }
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)); return options;
    }
}

/// <summary>One process-wide background writer/watchdog; never waits for disk on producer/UI paths.</summary>
public sealed class ApplicationDiagnosticLog : IAsyncDisposable
{
    private readonly Channel<ApplicationDiagnosticEntry> _queue;
    private readonly AutoResetEvent _wake = new(false);
    private readonly DiagnosticFileJournal _journal;
    private readonly Action<ApplicationDiagnosticLine> _append;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Guid _session = Guid.NewGuid();
    private readonly TimeProvider _clock;
    private long _lost, _sequence, _lastWrite, _accepted, _processed;
    private string? _problem;
    private int _stopping, _started, _sampleRequested;
    public string DirectoryPath => _journal.DirectoryPath;
    public Task Completion => _completion.Task;
    public string? Problem => Volatile.Read(ref _problem);
    public long LostRecords => Interlocked.Read(ref _lost);
    public int QueueDepth => _queue.Reader.Count;
    public DateTimeOffset? LastWriteUtc => Interlocked.Read(ref _lastWrite) is var ticks && ticks > 0 ? new(ticks, TimeSpan.Zero) : null;
    public bool Degraded => Problem is not null || LostRecords > 0 || _journal.RetentionBlocked > 0;

    public ApplicationDiagnosticLog(string dataDirectory, int capacity = 256, TimeProvider? clock = null,
        Action<ApplicationDiagnosticLine>? append = null)
    {
        _clock = clock ?? TimeProvider.System; _journal = new(dataDirectory, _session);
        _append = append ?? _journal.Append;
        _queue = Channel.CreateBounded<ApplicationDiagnosticEntry>(new BoundedChannelOptions(capacity)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }
    public bool TryRecord(ApplicationDiagnosticEntry entry)
    {
        if (Volatile.Read(ref _stopping) != 0) return false;
        if (!_queue.Writer.TryWrite(entry)) { Interlocked.Increment(ref _lost); return false; }
        Interlocked.Increment(ref _accepted); Signal(); return true;
    }
    public void Start(Action sample, TimeSpan? interval = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Diagnostic worker already started.");
        TimeSpan cadence = interval ?? TimeSpan.FromSeconds(10);
        new Thread(() => Run(sample, cadence)) { IsBackground = true, Name = "Relight diagnostics" }.Start();
    }
    private void Run(Action sample, TimeSpan cadence)
    {
        long last = _clock.GetTimestamp(); bool first = true;
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _stopping) == 0 && (first || Interlocked.Exchange(ref _sampleRequested, 0) != 0 || _clock.GetElapsedTime(last) >= cadence))
                {
                    first = false; last = _clock.GetTimestamp();
                    try { sample(); } catch (Exception) { CaptureFailed(); }
                }
                Drain();
                if (Volatile.Read(ref _stopping) != 0 && _queue.Reader.Count == 0) break;
                _wake.WaitOne(1000);
            }
            Write(new(DiagnosticKind.ProcessStopped));
        }
        catch (Exception) { CaptureFailed(); }
        finally { try { _journal.Dispose(); } catch (Exception) { CaptureFailed(); } _wake.Dispose(); _completion.TrySetResult(); }
    }
    internal void Drain()
    {
        while (_queue.Reader.TryRead(out var entry))
        { Write(entry); Interlocked.Increment(ref _processed); }
    }
    private void Write(ApplicationDiagnosticEntry entry)
    {
        try
        {
            _append(new(1, _session, Environment.ProcessId, Interlocked.Increment(ref _sequence), _clock.GetUtcNow(), LostRecords, entry));
            Interlocked.Exchange(ref _lastWrite, _clock.GetUtcNow().UtcTicks); Volatile.Write(ref _problem, null);
        }
        catch (Exception error)
        { Interlocked.Increment(ref _lost); Volatile.Write(ref _problem, $"Diagnostic write failed ({error.GetType().Name}, 0x{error.HResult:X8})."); }
    }
    private void Signal() { try { _wake.Set(); } catch (ObjectDisposedException) { } }
    public void RequestStop() { Interlocked.Exchange(ref _stopping, 1); _queue.Writer.TryComplete(); Signal(); }
    public void RequestSample() { Interlocked.Exchange(ref _sampleRequested, 1); Signal(); }
    public void CaptureFailed()
    { Interlocked.Increment(ref _lost); Volatile.Write(ref _problem, "Diagnostic capture failed; evidence may be incomplete."); }
    public bool FlushTerminal(TimeSpan timeout)
    {
        long target = Interlocked.Read(ref _accepted);
        return SpinWait.SpinUntil(() => Interlocked.Read(ref _processed) >= target, timeout);
    }
    public async ValueTask DisposeAsync()
    {
        RequestStop();
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            try { Drain(); Write(new(DiagnosticKind.ProcessStopped)); }
            finally { _journal.Dispose(); _wake.Dispose(); _completion.TrySetResult(); }
        }
        await Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }
}
