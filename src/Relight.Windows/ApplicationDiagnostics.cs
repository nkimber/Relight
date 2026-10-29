using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Relight.Core;
using Relight.Storage;

namespace Relight.Windows;

/// <summary>One independent process-wide diagnostic worker, including when the UI or thread pool is busy.</summary>
public sealed class ApplicationDiagnostics : IAsyncDisposable
{
    internal ApplicationDiagnosticLog Log { get; }
    private readonly Action<Action> _postUi;
    private readonly TimeProvider _clock;
    private readonly Func<DiagnosticResources> _resources;
    private readonly ConcurrentDictionary<Guid, ScanDiagnosticSession> _scans = new();
    private long _lastUiReply, _probeStarted;
    private int _probePending;
    private bool _delayed;
    private readonly TimeSpan _uiDelay;
    public string DirectoryPath => Log.DirectoryPath;
    public bool Degraded => Log.Degraded;
    public string Status => Log.Degraded
        ? $"Application diagnostics degraded: {Log.Problem ?? "records lost or retention blocked"}; lost records: {Log.LostRecords}."
        : $"Application diagnostics active · last flushed: {Log.LastWriteUtc?.ToLocalTime().ToString("G") ?? "waiting"} · queued: {Log.QueueDepth}.";

    public ApplicationDiagnostics(string dataDirectory, Action<Action> postUi, DiagnosticBuild build,
        bool startWorker = true, TimeProvider? clock = null, Func<DiagnosticResources>? resources = null,
        TimeSpan? cadence = null, TimeSpan? uiDelayThreshold = null)
    {
        _clock = clock ?? TimeProvider.System; _postUi = postUi; _resources = resources ?? ReadResources;
        _uiDelay = uiDelayThreshold ?? TimeSpan.FromSeconds(20);
        _lastUiReply = _clock.GetTimestamp();
        Log = new(dataDirectory, clock: _clock);
        Log.TryRecord(new(DiagnosticKind.ProcessStarted, Build: build));
        if (startWorker) Log.Start(Sample, cadence);
    }

    internal void Sample()
    {
        long now = _clock.GetTimestamp();
        double age = _clock.GetElapsedTime(Interlocked.Read(ref _lastUiReply), now).TotalSeconds;
        bool pending = Volatile.Read(ref _probePending) != 0;
        double probeAge = pending ? _clock.GetElapsedTime(Interlocked.Read(ref _probeStarted), now).TotalSeconds : 0;
        bool delayed = pending && probeAge >= _uiDelay.TotalSeconds;
        var ui = new DiagnosticDispatcher(age, probeAge, pending, delayed);
        if (delayed != _delayed)
        { _delayed = delayed; Log.TryRecord(new(delayed ? DiagnosticKind.DispatcherDelayed : DiagnosticKind.DispatcherRecovered, Dispatcher: ui)); }
        try { Log.TryRecord(new(DiagnosticKind.Heartbeat, Resources: _resources(), Dispatcher: ui)); }
        catch (Exception error) { RecordException(error, DiagnosticKind.MetricFailure); }
        foreach (var scan in _scans.Values) Log.TryRecord(new(DiagnosticKind.ScanHeartbeat, Scan: scan.Snapshot()));
        if (Interlocked.CompareExchange(ref _probePending, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _probeStarted, now);
            try
            {
                _postUi(() =>
                {
                    Interlocked.Exchange(ref _lastUiReply, _clock.GetTimestamp());
                    Interlocked.Exchange(ref _probePending, 0);
                });
            }
            catch (Exception error) { Interlocked.Exchange(ref _probePending, 0); RecordException(error, DiagnosticKind.HandledException); }
        }
    }

    public ScanDiagnosticSession? BeginScan(string root, int limit)
    {
        try
        {
            if (_scans.Count >= 4) { Log.CaptureFailed(); return null; }
            var session = new ScanDiagnosticSession(this, _clock, root, limit);
            _scans[session.Id] = session; Log.TryRecord(new(DiagnosticKind.ScanStarted, Scan: session.Snapshot())); Log.RequestSample();
            return session;
        }
        catch (Exception) { Log.CaptureFailed(); return null; }
    }
    internal void Finish(ScanDiagnosticSession scan, DiagnosticKind kind, Exception? error)
    {
        try { Log.TryRecord(new(kind, Scan: scan.Snapshot(), Failure: error is null ? null : DiagnosticFailure.Capture(error))); }
        catch (Exception) { Log.CaptureFailed(); }
        finally { _scans.TryRemove(scan.Id, out _); Log.RequestSample(); }
    }
    public void RecordException(Exception error, DiagnosticKind kind = DiagnosticKind.UnhandledException, bool terminal = false)
    {
        try { Log.TryRecord(new(kind, Failure: DiagnosticFailure.Capture(error))); if (terminal) Log.FlushTerminal(TimeSpan.FromMilliseconds(250)); }
        catch (Exception) { Log.CaptureFailed(); }
    }
    public void RequestStop() => Log.RequestStop();
    public ValueTask DisposeAsync() => Log.DisposeAsync();

    private static DiagnosticResources ReadResources()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh(); var gc = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out int workers, out int io);
        return new(process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(false), gc.HeapSizeBytes,
            gc.FragmentedBytes, gc.MemoryLoadBytes, gc.HighMemoryLoadThresholdBytes, GC.GetTotalAllocatedBytes(false),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), process.TotalProcessorTime.TotalSeconds,
            process.HandleCount, process.Threads.Count, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, workers, io)
            { PeakWorkingSet = process.PeakWorkingSet64, GcPausePercent = gc.PauseTimePercentage };
    }
    internal static string Tag(string path)
    {
        if (path.Length > 32768) path = path[..32768];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.TrimEnd('\\').ToUpperInvariant())))[..16];
    }
}

public sealed class ScanDiagnosticSession : IStorageScanDiagnostics
{
    private readonly ApplicationDiagnostics _owner;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly string _rootDrive, _rootTag;
    private readonly int _limit;
    private readonly long _started;
    private long _operationStarted, _operationEnded, _progress, _stageStarted;
    private string? _path, _lastFailureType;
    private int? _lastFailureCode;
    private int _visited, _measured, _skipped, _aliases, _identities, _depth, _issues;
    private bool _ioPending, _cancel, _ended;
    private bool? _complete, _limited;
    private ScanIoOperation? _operation;
    private DiagnosticScanStage _stage = DiagnosticScanStage.WaitingForWorker;
    public Guid Id { get; } = Guid.NewGuid();

    internal ScanDiagnosticSession(ApplicationDiagnostics owner, TimeProvider clock, string root, int limit)
    {
        _owner = owner; _clock = clock; _limit = limit; _rootTag = ApplicationDiagnostics.Tag(root);
        _rootDrive = root.Length >= 3 && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\'
            ? char.ToUpperInvariant(root[0]) + ":\\" : "unknown";
        _started = _progress = _operationStarted = _operationEnded = _stageStarted = _clock.GetTimestamp();
    }
    public void Stage(DiagnosticScanStage stage)
    {
        lock (_sync) { if (_stage != stage) _stageStarted = _clock.GetTimestamp(); _stage = stage; }
        try { _owner.Log.TryRecord(new(DiagnosticKind.ScanStage, Scan: Snapshot())); }
        catch (Exception) { _owner.Log.CaptureFailed(); }
        _owner.Log.RequestSample();
    }
    public void Operation(ScanIoOperation operation, string path, int depth, int visited, int measured, int skipped, int aliases, int identities)
    {
        lock (_sync)
        {
            if (visited > _visited) _progress = _clock.GetTimestamp();
            _operation = operation; _path = path; _depth = depth; _visited = visited; _measured = measured;
            _skipped = skipped; _aliases = aliases; _identities = identities;
            _operationStarted = _clock.GetTimestamp(); _ioPending = true;
        }
    }
    public void OperationEnded() { lock (_sync) { _ioPending = false; _operationEnded = _clock.GetTimestamp(); } }
    public void ReadFailure(Exception error)
    {
        bool record;
        lock (_sync) { _lastFailureType = error.GetType().FullName; _lastFailureCode = error.HResult; record = ++_issues <= 5; }
        if (record) try { _owner.Log.TryRecord(new(DiagnosticKind.ScanReadIssue, Scan: Snapshot(), Failure: DiagnosticFailure.Capture(error))); }
            catch (Exception) { _owner.Log.CaptureFailed(); }
    }
    public void CancelRequested()
    {
        lock (_sync) _cancel = true;
        try { _owner.Log.TryRecord(new(DiagnosticKind.ScanStage, Scan: Snapshot())); }
        catch (Exception) { _owner.Log.CaptureFailed(); }
    }
    public void End(DiagnosticScanStage stage, StorageScanSnapshot? result = null, Exception? error = null)
    {
        lock (_sync)
        {
            if (_ended) return; _ended = true; _stageStarted = _clock.GetTimestamp(); _stage = stage;
            if (result is not null) { _visited = result.EntriesVisited; _measured = result.FilesMeasured; _skipped = result.SkippedEntries;
                _aliases = result.HardLinkAliases; _complete = result.CoverageComplete; _limited = result.EntryLimitReached; }
        }
        _owner.Finish(this, stage == DiagnosticScanStage.Completed ? DiagnosticKind.ScanFinished :
            stage == DiagnosticScanStage.Canceled ? DiagnosticKind.ScanCanceled : DiagnosticKind.ScanFailed, error);
    }
    public void TraversalCompleted(StorageScanSnapshot result)
    {
        lock (_sync)
        {
            _visited = result.EntriesVisited; _measured = result.FilesMeasured; _skipped = result.SkippedEntries;
            _aliases = result.HardLinkAliases; _complete = result.CoverageComplete; _limited = result.EntryLimitReached;
        }
        Stage(DiagnosticScanStage.AwaitingUi);
    }
    internal DiagnosticScan Snapshot()
    {
        lock (_sync)
        {
            long now = _clock.GetTimestamp(); string? suffix = null;
            if (_path is not null)
            {
                string extension = Path.GetExtension(_path).ToLowerInvariant();
                suffix = new[] { ".vhdx", ".vhd", ".dll", ".exe", ".json", ".log", ".tmp", ".js", ".cs" }.Contains(extension) ? extension : "other";
            }
            return new(Id, _stage, _rootDrive, _rootTag, _limit, _visited, _measured, _skipped, _aliases, _identities, _depth,
                _operation, _path is null ? null : ApplicationDiagnostics.Tag(_path), suffix, _ioPending,
                _clock.GetElapsedTime(_operationStarted, _ioPending ? now : _operationEnded).TotalSeconds,
                _clock.GetElapsedTime(_progress, now).TotalSeconds, _clock.GetElapsedTime(_started, now).TotalSeconds,
                _cancel, _complete, _limited, _lastFailureType, _lastFailureCode)
                { StageSeconds = _clock.GetElapsedTime(_stageStarted, now).TotalSeconds };
        }
    }
}
