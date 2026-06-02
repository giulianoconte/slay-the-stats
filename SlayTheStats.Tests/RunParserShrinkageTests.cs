using Xunit;
using SlayTheStats;
using static SlayTheStats.Tests.RunFixture;

namespace SlayTheStats.Tests;

/// <summary>
/// End-to-end tests for RunParser.ProcessNewRuns over a real on-disk history
/// tree. Unlike the ProcessRun-level tests, these exercise the scan + idempotency
/// + shrinkage-rebuild path, so they set SlayTheStatsConfig.DataDirectory to a
/// temp tree shaped like %APPDATA%/SlayTheSpire2/steam/<id>/profile*/saves/history.
/// Ref giulianoconte/slay-the-stats#4.
/// </summary>
public class RunParserShrinkageTests : IDisposable
{
    private readonly string _root;
    private readonly string _historyDir;
    private readonly string _savePath;
    private readonly string _prevDataDir;

    public RunParserShrinkageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sts-shrink-" + Guid.NewGuid().ToString("N"));
        _historyDir = Path.Combine(_root, "steam", "76561190000000000", "profile1", "saves", "history");
        Directory.CreateDirectory(_historyDir);
        _savePath = Path.Combine(_root, "slay-the-stats.json");

        _prevDataDir = SlayTheStatsConfig.DataDirectory;
        SlayTheStatsConfig.DataDirectory = _root;
    }

    public void Dispose()
    {
        SlayTheStatsConfig.DataDirectory = _prevDataDir;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteRun(string runId, string json) =>
        File.WriteAllText(Path.Combine(_historyDir, runId + ".run"), json);

    private static string PickRun(string cardId) =>
        Build(acts: [[[new(cardId, Picked: true)]]]);

    [Fact]
    public void DeletingOneRun_PrunesItsStats_KeepsTheRest()
    {
        WriteRun("run1", PickRun("CARD.STRIKE"));
        WriteRun("run2", PickRun("CARD.DEFEND"));

        var db = StatsDb.Load(_savePath);
        RunParser.ProcessNewRuns(db, _savePath);

        Assert.Equal(2, db.ProcessedRuns.Count);
        Assert.True(db.Cards.ContainsKey("CARD.STRIKE"));
        Assert.True(db.Cards.ContainsKey("CARD.DEFEND"));

        // User deletes run2 from history. Next launch should rebuild from the
        // surviving runs, dropping run2's contributions entirely.
        File.Delete(Path.Combine(_historyDir, "run2.run"));
        RunParser.ProcessNewRuns(db, _savePath);

        Assert.Equal(new[] { "run1" }, db.ProcessedRuns);
        Assert.True(db.Cards.ContainsKey("CARD.STRIKE"));
        Assert.False(db.Cards.ContainsKey("CARD.DEFEND"));
    }

    [Fact]
    public void DeletingOneRun_PersistsTheRebuiltDb()
    {
        WriteRun("run1", PickRun("CARD.STRIKE"));
        WriteRun("run2", PickRun("CARD.DEFEND"));

        var db = StatsDb.Load(_savePath);
        RunParser.ProcessNewRuns(db, _savePath);

        File.Delete(Path.Combine(_historyDir, "run2.run"));
        RunParser.ProcessNewRuns(db, _savePath);

        // The rebuild must reach disk, else the stale json re-triggers it forever.
        var reloaded = StatsDb.Load(_savePath);
        Assert.Equal(new[] { "run1" }, reloaded.ProcessedRuns);
        Assert.False(reloaded.Cards.ContainsKey("CARD.DEFEND"));
    }

    [Fact]
    public void DeletingAllRuns_ResetsAndPersistsEmptyDb()
    {
        WriteRun("run1", PickRun("CARD.STRIKE"));

        var db = StatsDb.Load(_savePath);
        RunParser.ProcessNewRuns(db, _savePath);
        Assert.Single(db.ProcessedRuns);

        // Empty history (all runs deleted) but the dir still exists — shrinkage
        // to zero. newCount is 0, so persistence relies on the rebuilt flag.
        File.Delete(Path.Combine(_historyDir, "run1.run"));
        RunParser.ProcessNewRuns(db, _savePath);

        Assert.Empty(db.ProcessedRuns);
        Assert.Empty(db.Cards);

        var reloaded = StatsDb.Load(_savePath);
        Assert.Empty(reloaded.ProcessedRuns);
    }

    [Fact]
    public void NoDeletions_DoesNotRebuild_AddsOnlyNewRuns()
    {
        WriteRun("run1", PickRun("CARD.STRIKE"));

        var db = StatsDb.Load(_savePath);
        RunParser.ProcessNewRuns(db, _savePath);
        var strikeBefore = db.Cards["CARD.STRIKE"].Values.First().RunsPicked;

        // Add a second run; the first must NOT be reprocessed (no double-count).
        WriteRun("run2", PickRun("CARD.DEFEND"));
        RunParser.ProcessNewRuns(db, _savePath);

        Assert.Equal(2, db.ProcessedRuns.Count);
        Assert.Equal(strikeBefore, db.Cards["CARD.STRIKE"].Values.First().RunsPicked);
        Assert.True(db.Cards.ContainsKey("CARD.DEFEND"));
    }
}
