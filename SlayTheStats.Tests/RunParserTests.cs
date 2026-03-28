using Xunit;
using SlayTheStats;
using static SlayTheStats.Tests.RunFixture;

namespace SlayTheStats.Tests;

public class RunParserTests : IDisposable
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

    // --- Basic parsing ---

    [Fact]
    public void BasicCard_RecordsOfferedAndPicked()
    {
        var db = new StatsDb();
        var path = TempRun(Build(acts:
        [[
            [new("CARD.STRIKE", Picked: true), new("CARD.DEFEND", Picked: false)]
        ]]));

        RunParser.ProcessRun(path, "run1", db);

        var strike = db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(1, strike.RunsOffered);
        Assert.Equal(1, strike.RunsPicked);

        var defend = db.Cards["CARD.DEFEND"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(1, defend.RunsOffered);
        Assert.Equal(0, defend.RunsPicked);
    }

    // --- Upgraded cards ---

    [Fact]
    public void UpgradedCard_AppendsPlusSuffix()
    {
        var db = new StatsDb();
        var path = TempRun(Build(acts:
        [[
            [new("CARD.STRIKE", Picked: true, UpgradeLevel: 1)]
        ]]));

        RunParser.ProcessRun(path, "run1", db);

        Assert.True(db.Cards.ContainsKey("CARD.STRIKE+"));
        Assert.False(db.Cards.ContainsKey("CARD.STRIKE"));
    }

    [Fact]
    public void UnupgradedCard_NoSuffix()
    {
        var db = new StatsDb();
        var path = TempRun(Build(acts:
        [[
            [new("CARD.STRIKE", Picked: true, UpgradeLevel: 0)]
        ]]));

        RunParser.ProcessRun(path, "run1", db);

        Assert.True(db.Cards.ContainsKey("CARD.STRIKE"));
        Assert.False(db.Cards.ContainsKey("CARD.STRIKE+"));
    }

    // --- Skip tracking ---

    [Fact]
    public void AllSkipped_RecordsSkipEntry()
    {
        var db = new StatsDb();
        var path = TempRun(Build(acts:
        [[
            [new("CARD.STRIKE", Picked: false), new("CARD.DEFEND", Picked: false)]
        ]]));

        RunParser.ProcessRun(path, "run1", db);

        Assert.True(db.Cards.ContainsKey(RunParser.SkipId));
        var skip = db.Cards[RunParser.SkipId]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(1, skip.RunsPicked);
    }

    // --- Win/loss attribution ---

    [Fact]
    public void WinningRun_IncrementsRunsWon()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: true, acts:
        [[
            [new("CARD.STRIKE", Picked: true)]
        ]]));

        RunParser.ProcessRun(path, "run1", db);

        var stat = db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(1, stat.RunsWon);
    }

    [Fact]
    public void LosingRun_DoesNotIncrementRunsWon()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: false, acts:
        [[
            [new("CARD.STRIKE", Picked: true)]
        ]]));

        RunParser.ProcessRun(path, "run1", db);

        var stat = db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(0, stat.RunsWon);
    }

    // --- Abandoned runs ---

    [Fact]
    public void AbandonedRun_RecordsNothing()
    {
        var db = new StatsDb();
        var path = TempRun(Build(abandoned: true, acts:
        [[
            [new("CARD.STRIKE", Picked: true)]
        ]]));

        RunParser.ProcessRun(path, "run1", db);

        Assert.Empty(db.Cards);
    }

    // --- Game mode separation ---

    [Fact]
    public void SoloAndMultiplayerRuns_TrackStatsSeparately()
    {
        var db = new StatsDb();
        var soloPath = TempRun(Build(gameMode: "standard", acts:
        [[
            [new("CARD.STRIKE", Picked: true)]
        ]]));
        var multiPath = TempRun(Build(gameMode: "co_op", acts:
        [[
            [new("CARD.STRIKE", Picked: true)]
        ]]));

        RunParser.ProcessRun(soloPath, "run1", db);
        RunParser.ProcessRun(multiPath, "run2", db);

        var strikeKeys = db.Cards["CARD.STRIKE"].Keys.ToList();
        Assert.Equal(2, strikeKeys.Count);
        Assert.Contains(strikeKeys, k => k.Contains("|standard|"));
        Assert.Contains(strikeKeys, k => k.Contains("|co_op|"));
    }

    // --- Per-run deduplication ---

    [Fact]
    public void SameCardOfferedTwiceInRun_DeduplicatesRunsOffered()
    {
        var db = new StatsDb();
        // Same card offered in two different floors of act 1
        var path = TempRun(Build(acts:
        [[
            [new("CARD.STRIKE", Picked: false)],
            [new("CARD.STRIKE", Picked: true)]
        ]]));

        RunParser.ProcessRun(path, "run1", db);

        var stat = db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(1, stat.RunsOffered);
        Assert.Equal(1, stat.RunsPicked);
    }
}
