using System.ComponentModel;
using Relight.Core;
using Relight.Storage;

namespace Relight.Windows;

/// <summary>Only an in-memory, short-lived preview can authorize one explicit reviewed run.</summary>
public sealed class ReviewedCleanupService
{
    private readonly ICleanupFileSystem _files;
    private readonly CleanupStore _store;
    private readonly string[] _protectedRoots;
    private readonly Func<CleanupRule, CancellationToken, Task<bool>> _guard;
    private readonly Func<string, long?> _available;
    private readonly TimeProvider _clock;
    private CleanupPreview? _preview;
    private long _previewTimestamp;

    public ReviewedCleanupService(string dataDirectory, ICleanupFileSystem? files = null,
        Func<CleanupRule, CancellationToken, Task<bool>>? guard = null,
        Func<string, long?>? available = null, TimeProvider? clock = null)
    {
        _files = files ?? new WindowsStorageScanFileSystem();
        _store = new(dataDirectory);
        _clock = clock ?? TimeProvider.System;
        _guard = guard ?? GuardAbsentAsync;
        _available = available ?? (root => { try { return new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace; } catch (IOException) { return null; } });
        _protectedRoots = new[] { dataDirectory, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToArray();
    }

    public void InvalidatePreview() => _preview = null;
    public CleanupRule RuleForAutomaticApproval(Guid previewId)
    {
        var preview = _preview;
        if (preview is null || preview.Id != previewId || !preview.Complete ||
            _clock.GetElapsedTime(_previewTimestamp) > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Generate a fresh, complete preview before automatic approval.");
        return preview.Rule with { Exclusions = [.. preview.Rule.Exclusions] };
    }

    public void ValidateRoot(CleanupRule rule)
    {
        rule.Validate();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rule.Root));
        if (!string.Equals(root, rule.Root, StringComparison.OrdinalIgnoreCase) || Path.GetPathRoot(root) == root ||
            root.Split('\\').Any(p => new[] { ".git", "$Recycle.Bin", "System Volume Information" }.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
            _protectedRoots.Any(p => Contains(root, p) || Contains(p, root)))
            throw new ArgumentException("Choose a dedicated disposable-file folder outside system, application, repository metadata and Relight data locations.");
        if (_files is WindowsStorageScanFileSystem && new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
            throw new ArgumentException("Cleanup requires a fixed local drive.");
    }

    private static bool Contains(string parent, string child) => string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public async Task<CleanupPreview> PreviewAsync(CleanupRule rule, CancellationToken token = default)
    {
        _preview = null;
        ValidateRoot(rule);
        rule = rule with { Exclusions = [.. rule.Exclusions] };
        if (!await _guard(rule, token).ConfigureAwait(false)) throw new IOException("Application guard is running or unavailable; cleanup is suspended.");
        var result = await Task.Run(() =>
        {
            var issues = new List<CleanupIssue>();
            int issueCount = 0, inspected = 0;
            bool complete = true;
            var matching = new List<CleanupFile>();
            void Issue(string path, string reason, bool blocks = false)
            {
                issueCount++;
                if (issues.Count < 100) issues.Add(new(path, reason, blocks));
                if (blocks) complete = false;
            }
            foreach (string path in _files.Enumerate(rule.Root))
            {
                token.ThrowIfCancellationRequested();
                if (++inspected > 10000) { Issue(rule.Root, "10,000-entry limit reached; choose a narrower folder.", true); break; }
                if (!rule.Matches(Path.GetFileName(path))) continue;
                if (!string.Equals(Path.GetDirectoryName(path), rule.Root, StringComparison.OrdinalIgnoreCase) || path.Length > 3000)
                { Issue(path, "Out-of-root or overlong path.", true); continue; }
                try { matching.Add(_files.ReadCleanup(path)); }
                catch (Exception error) when (ReadFailure(error)) { Issue(path, error.Message, error is not CleanupExcludedException); }
            }
            DateTimeOffset cutoff = _clock.GetUtcNow().AddDays(-rule.MinimumAgeDays);
            var candidates = new List<CleanupFile>();
            long bytes = 0;
            foreach (var file in matching.OrderByDescending(f => f.LastWriteUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                         .Skip(rule.RetainNewest).OrderBy(f => f.LastWriteUtc))
            {
                if (file.LastWriteUtc > cutoff) { Issue(file.Path, "Newer than the minimum age."); continue; }
                if (candidates.Count >= rule.MaximumFiles || file.Length > rule.MaximumBytes - bytes)
                { Issue(file.Path, "Per-run file/byte limit; omitted from this preview."); continue; }
                candidates.Add(file); bytes += file.Length;
            }
            return new CleanupPreview(Guid.NewGuid(), rule with { Exclusions = [.. rule.Exclusions] }, _clock.GetUtcNow(),
                candidates.ToArray(), issues.ToArray(), issueCount, complete);
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _preview = result;
        _previewTimestamp = _clock.GetTimestamp();
        return result with { Rule = result.Rule with { Exclusions = [.. result.Rule.Exclusions] }, Candidates = [.. result.Candidates], Issues = [.. result.Issues] };
    }

    public async Task<CleanupRun> ExecuteReviewedAsync(Guid previewId, IReadOnlyList<string> selectedPaths,
        CancellationToken token = default, Func<IDisposable>? acquireAuthorization = null)
    {
        var preview = _preview;
        _preview = null; // Single-use even if execution fails or cancellation arrives.
        if (preview is null || preview.Id != previewId || !preview.Complete ||
            _clock.GetElapsedTime(_previewTimestamp) > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Generate a complete, fresh preview before cleanup.");
        ValidateRoot(preview.Rule);
        var paths = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0 || paths.Count != selectedPaths.Count || paths.Count > preview.Rule.MaximumFiles ||
            paths.Any(p => !preview.Candidates.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Select unique files from the current preview only.");
        var selected = preview.Candidates.Where(f => paths.Contains(f.Path)).ToArray();
        var run = new CleanupRun(Guid.NewGuid(), preview.Rule.Id, _clock.GetUtcNow(), null,
            selected.Select(f => new CleanupItemOutcome(f.Path, "Not attempted")).ToArray(), null, null)
            { Origin = acquireAuthorization is null ? "Reviewed" : "Automatic" };
        using var runLease = await Task.Run(() => _store.AcquireRunLease(), token).ConfigureAwait(false);
        await Task.Run(() => _store.SaveRun(run), token).ConfigureAwait(false); // Durable record required before dispatch.
        try
        {
            run = run with { AvailableBefore = await Task.Run(() => _available(preview.Rule.Root), token).ConfigureAwait(false) };
            for (int i = 0; i < selected.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!await _guard(preview.Rule, token).ConfigureAwait(false)) throw new IOException("Application guard is running or unavailable; remaining files were left alone.");
                run.Items[i] = new(selected[i].Path, "Pending; outcome unknown if interrupted");
                await Task.Run(() => _store.SaveRun(run), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                try
                {
                    await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var authorization = acquireAuthorization?.Invoke();
                        token.ThrowIfCancellationRequested();
                        if (!StillEligible(preview.Rule, selected[i], token))
                            throw new IOException("Age or newest-file retention changed since preview; file was left alone.");
                        _files.DeleteVerified(selected[i]);
                    }, token).ConfigureAwait(false);
                    run.Items[i] = new(selected[i].Path, "Deleted permanently");
                }
                catch (InvalidOperationException) when (acquireAuthorization is not null)
                {
                    run.Items[i] = new(selected[i].Path, "Not attempted", "Automatic authorization was revoked before dispatch.");
                    throw;
                }
                catch (Exception error) when (ReadFailure(error)) { run.Items[i] = new(selected[i].Path, "Skipped", error.Message); }
                await Task.Run(() => _store.SaveRun(run)).ConfigureAwait(false);
            }
        }
        catch (Exception error) { run = run with { Problem = error is OperationCanceledException ? "Canceled; remaining files left alone." : error.Message }; }
        run = run with { FinishedUtc = _clock.GetUtcNow(), AvailableAfter = await Task.Run(() => _available(preview.Rule.Root)).ConfigureAwait(false) };
        await Task.Run(() => _store.SaveRun(run)).ConfigureAwait(false);
        return run;
    }

    private static bool ReadFailure(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception;
    private bool StillEligible(CleanupRule rule, CleanupFile selected, CancellationToken token)
    {
        if (selected.LastWriteUtc > _clock.GetUtcNow().AddDays(-rule.MinimumAgeDays)) return false;
        if (rule.RetainNewest == 0) return true;
        int inspected = 0, newer = 0;
        foreach (string path in _files.Enumerate(rule.Root))
        {
            token.ThrowIfCancellationRequested();
            if (++inspected > 10000) return false;
            if (!rule.Matches(Path.GetFileName(path)) || string.Equals(path, selected.Path, StringComparison.OrdinalIgnoreCase)) continue;
            CleanupFile file;
            try { file = _files.ReadCleanup(path); }
            catch (CleanupExcludedException) { continue; }
            if (file.LastWriteUtc > selected.LastWriteUtc || file.LastWriteUtc == selected.LastWriteUtc &&
                StringComparer.OrdinalIgnoreCase.Compare(file.Path, selected.Path) < 0)
                if (++newer >= rule.RetainNewest) return true;
        }
        return false;
    }
    private static async Task<bool> GuardAbsentAsync(CleanupRule rule, CancellationToken token) => rule.GuardExecutable is null ||
        (await new ExecutableDiscovery(new(rule.GuardExecutable, [])).DetectAsync(token).ConfigureAwait(false)).Kind == DetectionKind.Absent;
}
