using Xunit;
using SlayTheStats;
using static SlayTheStats.Tests.RunFixture;

namespace SlayTheStats.Tests;

/// <summary>
/// Act-Total dedup (#6): when one run adds the same card in two different acts,
/// each per-act row correctly shows it, but the Total — a UNION across acts —
/// counts the run once, not twice. This is the N-ary sibling the #5 overlap term
/// couldn't address; the run-id model handles any number of acts uniformly.
/// </summary>
public class RunParserActTotalTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

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

    /// <summary>Mimics the tooltip Total: union the per-act aggregates into one stat.</summary>
    private static CardStat ActTotal(Dictionary<int, CardStat> byAct)
    {
        var total = new CardStat();
        foreach (var s in byAct.Values) total.MergeFrom(s);
        return total;
    }

    // Two acts, one floor each → floor 1 maps to act 1, floor 2 to act 2.
    private static string TwoActRun(params DeckCard[] deck) =>
        Build(won: true, acts: [[[]], [[]]], deck: [.. deck]);

    [Fact]
    public void SameCardAddedInTwoActs_PerActRowsAreSeparate()
    {
        var db = new StatsDb();
        var path = TempRun(TwoActRun(
            new("STRIKE_IRONCLAD", FloorAdded: 1),   // act 1
            new("STRIKE_IRONCLAD", FloorAdded: 2)));  // act 2
        RunParser.ProcessRun(path, "run1", "default", db);

        var byAct = StatsAggregator.AggregateByAct(db.Cards["STRIKE_IRONCLAD"]);
        Assert.Equal(1, byAct[1].RunsPresent);
        Assert.Equal(1, byAct[2].RunsPresent);
    }

    [Fact]
    public void SameCardAddedInTwoActs_TotalCountsRunOnce()
    {
        var db = new StatsDb();
        var path = TempRun(TwoActRun(
            new("STRIKE_IRONCLAD", FloorAdded: 1),
            new("STRIKE_IRONCLAD", FloorAdded: 2)));
        RunParser.ProcessRun(path, "run1", "default", db);

        var total = ActTotal(StatsAggregator.AggregateByAct(db.Cards["STRIKE_IRONCLAD"]));
        // Pre-#6 this summed to 2; the union counts the single run once.
        Assert.Equal(1, total.RunsPresent);
        Assert.Equal(1, total.RunsWon);
    }

    [Fact]
    public void DifferentRunsSameActSpread_TotalCountsBoth()
    {
        var db = new StatsDb();
        // Two distinct runs, each adding the card in one act → Total = 2.
        RunParser.ProcessRun(TempRun(TwoActRun(new DeckCard("STRIKE_IRONCLAD", FloorAdded: 1))), "run1", "default", db);
        RunParser.ProcessRun(TempRun(TwoActRun(new DeckCard("STRIKE_IRONCLAD", FloorAdded: 2))), "run2", "default", db);

        var total = ActTotal(StatsAggregator.AggregateByAct(db.Cards["STRIKE_IRONCLAD"]));
        Assert.Equal(2, total.RunsPresent);
    }

    [Fact]
    public void Relic_AcquiredInTwoActs_TotalCountsRunOnce()
    {
        // Same relic recorded in two act-contexts of one run (rare in practice —
        // a relic is acquired once — but proves the relic union dedups by act).
        var db = new StatsDb();
        var path = TempRun(Build(won: true, relicActs:
        [
            [[new RelicChoice("RELIC.ANCHOR", Picked: true)]],
            [[new RelicChoice("RELIC.ANCHOR", Picked: true)]],
        ]));
        RunParser.ProcessRun(path, "run1", "default", db);

        var byAct = StatsAggregator.AggregateRelicsByAct(db.Relics["RELIC.ANCHOR"], character: null, gameMode: null);
        var total = new RelicStat();
        foreach (var s in byAct.Values) total.MergeFrom(s);
        Assert.Equal(1, total.RunsPresent);
        Assert.Equal(1, total.RunsWon);
    }

    [Fact]
    public void BothVariantsAcrossActs_GroupedTotalCountsRunOnce()
    {
        // The full unification: one run, base form added in act 1, "+" in act 2.
        // Grouping (union over variants) AND the Total (union over acts) must both
        // collapse to the single run.
        var db = new StatsDb();
        var path = TempRun(TwoActRun(
            new("STRIKE_IRONCLAD", FloorAdded: 1, UpgradeLevel: 0),
            new("STRIKE_IRONCLAD", FloorAdded: 2, UpgradeLevel: 1)));
        RunParser.ProcessRun(path, "run1", "default", db);

        var grouped = StatsAggregator.MergeGroupedContextMaps(
            db.Cards["STRIKE_IRONCLAD"], db.Cards["STRIKE_IRONCLAD+"]);
        var total = ActTotal(StatsAggregator.AggregateByAct(grouped));
        Assert.Equal(1, total.RunsPresent);
    }
}
