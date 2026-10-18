using System.Text.Json;
using System.Text.Json.Serialization;
using Relight.Core;

namespace Relight.Storage;

/// <summary>Manual rules and the latest run only. No scheduler or persisted deletion authority.</summary>
public sealed class CleanupStore(string dataDirectory)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(dataDirectory), "Storage");
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const int MaximumBytes = 2 * 1024 * 1024;
    public FileStream AcquireRunLease()
    {
        Directory.CreateDirectory(_directory);
        return new FileStream(Path.Combine(_directory, "cleanup-run.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public CleanupRuleBook LoadRules()
    {
        var book = Read<CleanupRuleBook>("cleanup-rules.json");
        if (book is null && File.Exists(Path.Combine(_directory, "cleanup-rules.json.bak")))
            throw new InvalidDataException("Cleanup rules are missing but a backup exists; preserve/recover the original rules before editing.");
        book ??= new(1, 0, []);
        if (book.SchemaVersion != 1 || book.Revision < 0 || book.Rules is null || book.Rules.Length > 50)
            throw new InvalidDataException("Cleanup rules are invalid or unsupported; the file was preserved.");
        var ids = new HashSet<Guid>();
        foreach (var rule in book.Rules)
        {
            if (rule is null || !ids.Add(rule.Id)) throw new InvalidDataException("Cleanup rule identities are invalid.");
            rule.Validate();
        }
        return book;
    }

    public CleanupRuleBook SaveRules(long expectedRevision, CleanupRule[] rules)
    {
        using var lease = Lock();
        var current = LoadRules();
        if (current.Revision != expectedRevision) throw new InvalidOperationException("Cleanup rules changed. Reopen the window before saving.");
        var next = new CleanupRuleBook(1, checked(current.Revision + 1), rules);
        if (rules.Length > 50 || rules.Select(r => r.Id).Distinct().Count() != rules.Length)
            throw new ArgumentException("Use at most 50 uniquely identified cleanup rules.");
        foreach (var rule in rules) rule.Validate();
        Write("cleanup-rules.json", next, backup: true);
        return next;
    }

    public CleanupRun? LoadLastRun()
    {
        var run = Read<CleanupRun>("cleanup-last-run.json");
        if (run is not null && (run.Id == Guid.Empty || run.RuleId == Guid.Empty || run.Items is null ||
            run.Items.Length > 200 || run.Items.Any(i => i is null || string.IsNullOrWhiteSpace(i.Path) || string.IsNullOrWhiteSpace(i.Outcome))))
            throw new InvalidDataException("Cleanup run evidence is invalid; it was preserved.");
        return run;
    }
    public void SaveRun(CleanupRun run)
    {
        if (run.Items.Length > 200) throw new ArgumentException("Cleanup run is too large.");
        using var lease = Lock();
        // Refuse to overwrite unreadable evidence from a previous interrupted run.
        _ = LoadLastRun();
        Write("cleanup-last-run.json", run, backup: false);
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(_directory);
        return new FileStream(Path.Combine(_directory, "cleanup-store.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private T? Read<T>(string name) where T : class
    {
        string path = Path.Combine(_directory, name);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Cleanup data exceeds its size limit.");
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("Cleanup data is empty.");
    }

    private void Write<T>(string name, T value, bool backup)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Cleanup data exceeds its size limit.");
        string path = Path.Combine(_directory, name), temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            if (backup && File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
