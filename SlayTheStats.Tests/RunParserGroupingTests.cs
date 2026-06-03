using Xunit;
using SlayTheStats;
using static SlayTheStats.Tests.RunFixture;

namespace SlayTheStats.Tests;

/// <summary>
/// Upgrade-grouping under the run-id model (#6, superseding #5's overlap term):
/// the grouped view unions the base and "+" run-id sets, so a single run holding
/// both upgrade states of a card is counted once. Per-variant entries are
/// untouched (ungrouped stays accurate).
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

    /// <summary>Grouped view for a base card id, via the production union path.</summary>
    private static Dictionary<string, CardStat> Grouped(StatsDb db, string baseId) =>
        StatsAggregator.MergeGroupedContextMaps(
            db.Cards.GetValueOrDefault(baseId, new()),
            db.Cards.GetValueOrDefault(baseId + "+", new()));

    [Fact]
    public void BothVariantsInWonDeck_GroupsToOneRun()
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
        // ...but the grouped union counts the single run once, not twice.
        Assert.Equal(1, Grouped(db, "STRIKE_IRONCLAD")[Ctx].RunsPresent);
        Assert.Equal(1, Grouped(db, "STRIKE_IRONCLAD")[Ctx].RunsWon);
    }

    [Fact]
    public void UniformUpgradeDeck_GroupsToOneRun()
    {
        var db = new StatsDb();
        // Two copies, both upgraded — only the "+" variant present.
        var path = TempRun(Build(won: true, deck:
        [
            new("DEFEND_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1),
            new("DEFEND_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1),
        ]));
        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.Equal(1, Grouped(db, "DEFEND_IRONCLAD")[Ctx].RunsPresent);
    }

    [Fact]
    public void LostRunBothVariants_GroupedPresentNotWon()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: false, deck:
        [
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0),
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1),
        ]));
        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.Equal(1, Grouped(db, "STRIKE_IRONCLAD")[Ctx].RunsPresent);
        Assert.Equal(0, Grouped(db, "STRIKE_IRONCLAD")[Ctx].RunsWon);
    }

    [Fact]
    public void BothVariantsOfferedSameAct_GroupsToOneRun()
    {
        var db = new StatsDb();
        // Two reward screens in act 1: one offers STRIKE, one offers STRIKE+, both picked.
        var path = TempRun(Build(acts:
        [[
            [new("STRIKE_IRONCLAD", Picked: true, UpgradeLevel: 0)],
            [new("STRIKE_IRONCLAD", Picked: true, UpgradeLevel: 1)],
        ]]));
        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.Equal(1, Grouped(db, "STRIKE_IRONCLAD")[Ctx].RunsOffered);
        Assert.Equal(1, Grouped(db, "STRIKE_IRONCLAD")[Ctx].RunsPicked);
    }

    [Fact]
    public void SeparateRunsEachOneVariant_GroupSumsToTwo()
    {
        var db = new StatsDb();
        var p1 = TempRun(Build(won: true, deck: [new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0)]));
        var p2 = TempRun(Build(won: true, deck: [new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 1)]));
        RunParser.ProcessRun(p1, "run1", "default", db);
        RunParser.ProcessRun(p2, "run2", "default", db);

        // Two distinct runs → two distinct run indices → union counts both.
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
