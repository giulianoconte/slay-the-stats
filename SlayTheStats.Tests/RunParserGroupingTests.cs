using Xunit;
using SlayTheStats;
using static SlayTheStats.Tests.RunFixture;

namespace SlayTheStats.Tests;

/// <summary>
/// Upgrade-grouping inclusion-exclusion (#5): a single run holding both the
/// non-upgraded and "+" form of a card must be counted once in the grouped
/// view, not twice. Covers both the parse-time overlap accumulation and the
/// pure merge math in StatsAggregator.MergeGroupedContextMaps.
/// </summary>
public class RunParserGroupingTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private const string Ctx = "CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN|default";

    private string TempRun(string json)
    {
        var path = WriteTempFile(json);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>Grouped view for the base card id, via the production merge path.</summary>
    private static Dictionary<string, CardStat> Grouped(StatsDb db, string baseId)
    {
        db.CardsGroupOverlap.TryGetValue(baseId, out var overlap);
        return StatsAggregator.MergeGroupedContextMaps(
            db.Cards.GetValueOrDefault(baseId, new()),
            db.Cards.GetValueOrDefault(baseId + "+", new()),
            overlap);
    }

    // --- Parse-time overlap accumulation ---

    [Fact]
    public void BothVariantsInWonDeck_RecordsPresenceOverlap()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: true, deck:
        [
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0),
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1),
        ]));
        RunParser.ProcessRun(path, "run1", "default", db);

        // Each variant records the run independently...
        Assert.Equal(1, db.Cards["STRIKE_IRONCLAD"][Ctx].RunsPresent);
        Assert.Equal(1, db.Cards["STRIKE_IRONCLAD+"][Ctx].RunsPresent);
        // ...and the overlap captures that they came from the same run.
        Assert.Equal(1, db.CardsGroupOverlap["STRIKE_IRONCLAD"][Ctx].RunsPresent);
        Assert.Equal(1, db.CardsGroupOverlap["STRIKE_IRONCLAD"][Ctx].RunsWon);
    }

    [Fact]
    public void UniformUpgradeDeck_NoOverlap()
    {
        var db = new StatsDb();
        // Two copies, both upgraded — only the "+" variant present, no overlap.
        var path = TempRun(Build(won: true, deck:
        [
            new("DEFEND_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1),
            new("DEFEND_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1),
        ]));
        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.False(db.CardsGroupOverlap.ContainsKey("DEFEND_IRONCLAD"));
    }

    [Fact]
    public void LostRunBothVariants_OverlapPresentButNotWon()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: false, deck:
        [
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0),
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1),
        ]));
        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.Equal(1, db.CardsGroupOverlap["STRIKE_IRONCLAD"][Ctx].RunsPresent);
        Assert.Equal(0, db.CardsGroupOverlap["STRIKE_IRONCLAD"][Ctx].RunsWon);
    }

    [Fact]
    public void BothVariantsOfferedSameAct_RecordsOfferedAndPickedOverlap()
    {
        var db = new StatsDb();
        // Two reward screens in act 1: one offers STRIKE, one offers STRIKE+, both picked.
        var path = TempRun(Build(acts:
        [[
            [new("STRIKE_IRONCLAD", Picked: true, UpgradeLevel: 0)],
            [new("STRIKE_IRONCLAD", Picked: true, UpgradeLevel: 1)],
        ]]));
        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.Equal(1, db.CardsGroupOverlap["STRIKE_IRONCLAD"][Ctx].RunsOffered);
        Assert.Equal(1, db.CardsGroupOverlap["STRIKE_IRONCLAD"][Ctx].RunsPicked);
    }

    // --- Grouped merge (inclusion-exclusion) ---

    [Fact]
    public void GroupedPresence_CountsOneRunOnce()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: true, deck:
        [
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0),
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1),
        ]));
        RunParser.ProcessRun(path, "run1", "default", db);

        var grouped = Grouped(db, "STRIKE_IRONCLAD");
        // Naive A + B would be 2/2; inclusion-exclusion yields the true 1/1.
        Assert.Equal(1, grouped[Ctx].RunsPresent);
        Assert.Equal(1, grouped[Ctx].RunsWon);
    }

    [Fact]
    public void GroupedOffered_CountsOneRunOnce()
    {
        var db = new StatsDb();
        var path = TempRun(Build(acts:
        [[
            [new("STRIKE_IRONCLAD", Picked: true, UpgradeLevel: 0)],
            [new("STRIKE_IRONCLAD", Picked: true, UpgradeLevel: 1)],
        ]]));
        RunParser.ProcessRun(path, "run1", "default", db);

        var grouped = Grouped(db, "STRIKE_IRONCLAD");
        Assert.Equal(1, grouped[Ctx].RunsOffered);
        Assert.Equal(1, grouped[Ctx].RunsPicked);
    }

    [Fact]
    public void SeparateRunsEachOneVariant_GroupSumsToTwo()
    {
        var db = new StatsDb();
        var p1 = TempRun(Build(won: true, deck: [new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0)]));
        var p2 = TempRun(Build(won: true, deck: [new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1)]));
        RunParser.ProcessRun(p1, "run1", "default", db);
        RunParser.ProcessRun(p2, "run2", "default", db);

        // No single run held both, so there's no overlap to subtract: two real runs.
        Assert.False(db.CardsGroupOverlap.ContainsKey("STRIKE_IRONCLAD"));
        Assert.Equal(2, Grouped(db, "STRIKE_IRONCLAD")[Ctx].RunsPresent);
    }

    [Fact]
    public void ReportedSymptom_StarterRunCountsAreEqual()
    {
        // One won run: Strike present in both forms (×2, one upgraded), Defend
        // uniform. Pre-fix, grouped Strike showed 2/0 while Defend showed 1/0.
        var db = new StatsDb();
        var path = TempRun(Build(won: true, deck:
        [
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0),
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1),
            new("DEFEND_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0),
            new("DEFEND_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0),
        ]));
        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.Equal(1, Grouped(db, "STRIKE_IRONCLAD")[Ctx].RunsPresent);
        Assert.Equal(1, Grouped(db, "DEFEND_IRONCLAD")[Ctx].RunsPresent);
    }
}
