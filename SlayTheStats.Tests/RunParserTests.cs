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

    // --- Win/loss attribution (presence-based) ---

    [Fact]
    public void WinningRun_IncrementsRunsWonAndRunsPresent()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: true, acts:
        [[
            [new("CARD.STRIKE", Picked: true)]
        ]], deck: [new DeckCard("CARD.STRIKE", FloorAdded: 1)]));

        RunParser.ProcessRun(path, "run1", db);

        var stat = db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(1, stat.RunsPresent);
        Assert.Equal(1, stat.RunsWon);
    }

    [Fact]
    public void LosingRun_IncrementsRunsPresentButNotRunsWon()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: false, acts:
        [[
            [new("CARD.STRIKE", Picked: true)]
        ]], deck: [new DeckCard("CARD.STRIKE", FloorAdded: 1)]));

        RunParser.ProcessRun(path, "run1", db);

        var stat = db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(1, stat.RunsPresent);
        Assert.Equal(0, stat.RunsWon);
    }

    [Fact]
    public void StarterCard_NotPickedFromReward_TrackedByPresence()
    {
        var db = new StatsDb();
        var path = TempRun(Build(
            won: true,
            deck: [new DeckCard("CARD.STRIKE_IRONCLAD", FloorAdded: 1)]));

        RunParser.ProcessRun(path, "run1", db);

        var stat = db.Cards["CARD.STRIKE_IRONCLAD"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(1, stat.RunsPresent);
        Assert.Equal(1, stat.RunsWon);
        Assert.Equal(0, stat.RunsOffered);
        Assert.Equal(0, stat.RunsPicked);
    }

    [Fact]
    public void MultipleCopiesInDeck_RunsPresentDeduplicated()
    {
        var db = new StatsDb();
        // Two copies of the same card, both in act 1 — should only count as RunsPresent=1
        var path = TempRun(Build(
            acts: [[[new("CARD.STRIKE", Picked: false)], [new("CARD.STRIKE", Picked: false)]]],
            deck: [new DeckCard("CARD.STRIKE", FloorAdded: 1), new DeckCard("CARD.STRIKE", FloorAdded: 2)]));

        RunParser.ProcessRun(path, "run1", db);

        var stat = db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"];
        Assert.Equal(1, stat.RunsPresent);
    }

    [Fact]
    public void DeckCard_ActDerivedFromFloor()
    {
        var db = new StatsDb();
        // Act 1 has 2 floors; card at floor 3 falls in act 2
        var path = TempRun(Build(
            acts: [
                [[new("CARD.DEFEND", Picked: false)], [new("CARD.DEFEND", Picked: false)]],
                [[new("CARD.STRIKE", Picked: true)]]
            ],
            deck: [new DeckCard("CARD.STRIKE", FloorAdded: 3)]));

        RunParser.ProcessRun(path, "run1", db);

        Assert.True(db.Cards["CARD.STRIKE"].ContainsKey("CHARACTER.IRONCLAD|0|2|UNKNOWN|UNKNOWN"));
        Assert.False(db.Cards["CARD.STRIKE"].ContainsKey("CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"));
    }

    [Fact]
    public void UpgradedCardInDeck_TrackedWithPlusSuffix()
    {
        var db = new StatsDb();
        var path = TempRun(Build(
            deck: [new DeckCard("CARD.STRIKE", FloorAdded: 1, UpgradeLevel: 1)]));

        RunParser.ProcessRun(path, "run1", db);

        Assert.True(db.Cards.ContainsKey("CARD.STRIKE+"));
        Assert.Equal(1, db.Cards["CARD.STRIKE+"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"].RunsPresent);
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

    // --- FloorToAct boundary cases ---

    [Fact]
    public void DeckCard_AtExactActBoundary_AssignedToCurrentAct()
    {
        var db = new StatsDb();
        // Act 1 has 2 floors (boundary = 2); a deck card at floor 2 (exact boundary) should land in act 1
        // Act 2 reward offers a different card so CARD.STRIKE only appears in the deck, not in any offer set
        var path = TempRun(Build(
            acts: [
                [[new("CARD.DEFEND", Picked: false)], [new("CARD.DEFEND", Picked: false)]],
                [[new("CARD.DEFEND", Picked: false)]]
            ],
            deck: [new DeckCard("CARD.STRIKE", FloorAdded: 2)]));

        RunParser.ProcessRun(path, "run1", db);

        // Presence should be recorded under act 1, not act 2
        Assert.Equal(1, db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"].RunsPresent);
        Assert.False(db.Cards["CARD.STRIKE"].ContainsKey("CHARACTER.IRONCLAD|0|2|UNKNOWN|UNKNOWN"));
    }

    [Fact]
    public void DeckCard_BeyondAllActFloors_AssignedToLastAct()
    {
        var db = new StatsDb();
        // Acts have 1 floor each (boundaries = [1, 2]); floor 99 is beyond both — should fall into act 2 (last act)
        var path = TempRun(Build(
            acts: [[[new("CARD.DEFEND", Picked: false)]], [[new("CARD.STRIKE", Picked: false)]]],
            deck: [new DeckCard("CARD.STRIKE", FloorAdded: 99)]));

        RunParser.ProcessRun(path, "run1", db);

        Assert.True(db.Cards["CARD.STRIKE"].ContainsKey("CHARACTER.IRONCLAD|0|2|UNKNOWN|UNKNOWN"));
        Assert.False(db.Cards["CARD.STRIKE"].ContainsKey("CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"));
    }

    [Fact]
    public void DeckCard_SameCardAcrossMultipleActs_GetsSeparateContextEntries()
    {
        var db = new StatsDb();
        // Act 1 has 1 floor; two copies of the same card acquired in act 1 and act 2
        // should produce separate context entries (one per act), each with RunsPresent = 1
        var path = TempRun(Build(
            acts: [[[new("CARD.STRIKE", Picked: false)]], [[new("CARD.STRIKE", Picked: false)]]],
            deck: [
                new DeckCard("CARD.STRIKE", FloorAdded: 1), // act 1 (floor 1 <= boundary 1)
                new DeckCard("CARD.STRIKE", FloorAdded: 2), // act 2 (floor 2 > boundary 1, <= boundary 2)
            ]));

        RunParser.ProcessRun(path, "run1", db);

        Assert.True(db.Cards["CARD.STRIKE"].ContainsKey("CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"));
        Assert.True(db.Cards["CARD.STRIKE"].ContainsKey("CHARACTER.IRONCLAD|0|2|UNKNOWN|UNKNOWN"));
        Assert.Equal(1, db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN"].RunsPresent);
        Assert.Equal(1, db.Cards["CARD.STRIKE"]["CHARACTER.IRONCLAD|0|2|UNKNOWN|UNKNOWN"].RunsPresent);
    }
}
