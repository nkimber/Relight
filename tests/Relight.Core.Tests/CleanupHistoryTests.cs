using Relight.Core;
using Relight.Storage;
using Relight.ViewModels;

namespace Relight.Core.Tests;

public sealed class CleanupHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relight-history-" + Guid.NewGuid().ToString("N"));
    private CleanupStore Store => new(_root);
    private CleanupRule Rule => new(Guid.Parse("7d343928-264b-40ac-af64-a7954ae9ba25"), "Historical rule", Path.Combine(_root, "files"), "*.log", []);
    private CleanupRun Run(string origin = "Reviewed") => new(Guid.NewGuid(), Rule.Id, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow,
        [new(Path.Combine(_root, "files", "old.log"), "Skipped", "File changed after preview")], 100, 80)
        { Origin = origin, RuleSnapshot = Rule };

    [Fact]
    public void OlderDetailsAndRuleSnapshotSurviveNewRunsAndRuleRemoval()
    {
        var first = Run("Automatic"); Store.SaveRun(first);
        var second = Run(); Store.SaveRun(second);
        var book = Store.SaveRules(0, [Rule]); Store.SaveRules(book.Revision, []);
        var retained = Store.LoadRun(first.Id)!;
        Assert.Equal("Automatic", retained.Origin);
        Assert.Equal("Historical rule", retained.RuleSnapshot!.Name);
        Assert.Equal("File changed after preview", Assert.Single(retained.Items).Detail);
        Assert.Equal("Historical rule", Store.LoadHistory().Single(h => h.Id == first.Id).RuleName);
        Assert.False(Store.LoadRules().AutomaticEnabled);
    }

    [Fact]
    public void InterruptedPendingDetailsRemainUnknownWhenAnotherRunStarts()
    {
        var pending = Run() with { FinishedUtc = null, Items = [new("old.log", "Pending; outcome unknown if interrupted")] };
        Store.SaveRun(pending); Store.SaveRun(Run());
        var retained = Store.LoadRun(pending.Id)!;
        Assert.Null(retained.FinishedUtc);
        Assert.Contains("unknown", Assert.Single(retained.Items).Outcome);
        Assert.Equal(1, Store.LoadHistory().Single(h => h.Id == pending.Id).NotCompleted);
    }

    [Fact]
    public void UpgradeRetainsLegacyLatestDetailsBeforeNewRunReplacesThem()
    {
        var legacy = Run() with { RuleSnapshot = null }; Store.SaveRun(legacy);
        File.Delete(Path.Combine(_root, "Storage", "cleanup-run-details.json"));
        Store.SaveRun(Run() with { FinishedUtc = null });
        var retained = Store.LoadRun(legacy.Id)!;
        Assert.Equal("File changed after preview", Assert.Single(retained.Items).Detail);
        Assert.Null(retained.RuleSnapshot);
    }

    [Fact]
    public void DetailArchiveIsBoundedByCountAndLegacySummariesHaveNoInventedDetails()
    {
        var first = Run(); Store.SaveRun(first);
        for (int i = 0; i < 100; i++) Store.SaveRun(Run());
        Assert.Equal(100, Store.LoadHistory().Length);
        Assert.Null(Store.LoadRun(first.Id));
        var newest = Store.LoadLastRun()!;
        File.Delete(Path.Combine(_root, "Storage", "cleanup-run-details.json"));
        var summaryOnly = Store.LoadHistory().First(h => h.Id != newest.Id);
        Assert.Null(Store.LoadRun(summaryOnly.Id));
        Assert.NotNull(Store.LoadRun(newest.Id));
    }

    [Fact]
    public void ByteLimitEvictsWholeOlderDetailsAndKeepsSummaries()
    {
        var runs = Enumerable.Range(0, 6).Select(_ => Run() with
        {
            Items = Enumerable.Range(0, 200).Select(i => new CleanupItemOutcome(new string('x', 5500) + i + ".log", "Skipped")).ToArray()
        }).ToArray();
        foreach (var run in runs) Store.SaveRun(run);
        Assert.True(new FileInfo(Path.Combine(_root, "Storage", "cleanup-run-details.json")).Length <= 4 * 1024 * 1024);
        Assert.Null(Store.LoadRun(runs[0].Id));
        Assert.Equal(200, Store.LoadRun(runs[^1].Id)!.Items.Length);
        Assert.Equal(6, Store.LoadHistory().Length);
    }

    [Fact]
    public void CorruptOrUnsupportedArchiveIsPreservedAndBlocksNewEvidenceWrites()
    {
        var original = Run(); Store.SaveRun(original);
        string path = Path.Combine(_root, "Storage", "cleanup-run-details.json");
        foreach (string invalid in new[] { "{broken", "{\"SchemaVersion\":99,\"Runs\":[]}" })
        {
            File.WriteAllText(path, invalid);
            Assert.ThrowsAny<Exception>(() => Store.SaveRun(Run()));
            Assert.Equal(invalid, File.ReadAllText(path));
            Assert.Equal(original.Id, Store.LoadLastRun()!.Id);
        }
    }

    [Fact]
    public async Task ReadOnlyHistoryFiltersAndShowsRunDetailsWithoutCreatingRules()
    {
        var first = Run("Automatic"); Store.SaveRun(first);
        var second = Run() with { Items = [new("deleted.log", "Deleted permanently")] }; Store.SaveRun(second);
        var model = new CleanupHistoryViewModel(_root);
        await model.RefreshAsync(); Assert.Equal(2, model.Runs.Count);
        model.Origin = "Automatic"; Assert.Single(model.Runs);
        model.SelectedRun = model.Runs[0]; await model.LoadSelectedAsync();
        Assert.Contains("Historical rule", model.Details);
        Assert.Contains("other disk activity", model.Details);
        Assert.Contains("UTC", model.Details);
        Assert.Equal("File changed after preview", Assert.Single(model.Items).Detail);
        model.Origin = "Reviewed"; Assert.Empty(model.Items);
        Assert.Null(model.SelectedRun);
        model.Origin = "All runs"; model.ProblemsOnly = true; Assert.Single(model.Runs);
        Assert.False(File.Exists(Path.Combine(_root, "Storage", "cleanup-rules.json")));
        model.Close();
    }

    [Fact]
    public async Task MissingDetailsAndUnfinishedRunsAreExplicitAndErrorsClearStaleRows()
    {
        var legacy = Run() with { RuleSnapshot = null }; Store.SaveRun(legacy);
        var pending = Run() with { FinishedUtc = null, RuleSnapshot = null, Items = [new("unknown.log", "Pending; outcome unknown if interrupted")] };
        Store.SaveRun(pending);
        File.Delete(Path.Combine(_root, "Storage", "cleanup-run-details.json"));
        var model = new CleanupHistoryViewModel(_root); await model.RefreshAsync();
        model.SelectedRun = model.Runs.Single(r => r.Run.Id == legacy.Id); await model.LoadSelectedAsync();
        Assert.Contains("not retained", model.Details); Assert.Empty(model.Items);
        model.SelectedRun = model.Runs.Single(r => r.Run.Id == pending.Id); await model.LoadSelectedAsync();
        Assert.Contains("in progress or interrupted", model.Details);
        Assert.Contains("not recorded", model.Details);
        File.WriteAllText(Path.Combine(_root, "Storage", "cleanup-history.json"), "{broken");
        await model.RefreshAsync(); Assert.Empty(model.Runs); Assert.Empty(model.Items);
        Assert.Contains("unavailable", model.Status); model.Close();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
