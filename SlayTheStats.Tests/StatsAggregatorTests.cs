using Xunit;
using SlayTheStats;

namespace SlayTheStats.Tests;

public class StatsAggregatorTests
{
    // --- GetCharacterWR ---

    [Fact]
    public void GetCharacterWR_NoData_ReturnsFifty()
    {
        var db = new StatsDb();
        Assert.Equal(50.0, StatsAggregator.GetCharacterWR(db, "CHARACTER.IRONCLAD"));
    }

    [Fact]
    public void GetCharacterWR_WithData_ReturnsCorrectRate()
    {
        var db = new StatsDb();
        var stat = db.GetOrCreateCharacter("CHARACTER.IRONCLAD", "standard");
        stat.Runs = 10; stat.Wins = 7;
        Assert.Equal(70.0, StatsAggregator.GetCharacterWR(db, "CHARACTER.IRONCLAD"));
    }

    // --- GetGlobalWR ---

    [Fact]
    public void GetGlobalWR_NoData_ReturnsFifty()
    {
        var db = new StatsDb();
        Assert.Equal(50.0, StatsAggregator.GetGlobalWR(db));
    }

    [Fact]
    public void GetGlobalWR_MultipleCharacters_AggregatesCorrectly()
    {
        var db = new StatsDb();
        var s1 = db.GetOrCreateCharacter("CHARACTER.IRONCLAD", "standard");
        s1.Runs = 10; s1.Wins = 6;
        var s2 = db.GetOrCreateCharacter("CHARACTER.SILENT", "standard");
        s2.Runs = 10; s2.Wins = 4;
        Assert.Equal(50.0, StatsAggregator.GetGlobalWR(db));
    }

    // --- GetPickRateBaseline ---

    [Fact]
    public void GetPickRateBaseline_NoData_ReturnsOneThird()
    {
        var db = new StatsDb();
        Assert.Equal(100.0 / 3.0, StatsAggregator.GetPickRateBaseline(db));
    }

    [Fact]
    public void GetPickRateBaseline_WithSkipData_UsesFormula()
    {
        var db = new StatsDb();
        db.TotalRewardScreens = 10;
        db.TotalSkips = 2; // skipRate = 0.2; baseline = (1 - 0.2) / 3 * 100
        var expected = (1.0 - 0.2) / 3.0 * 100.0;
        Assert.Equal(expected, StatsAggregator.GetPickRateBaseline(db));
    }

    // --- GetHighestWonAscension ---

    [Fact]
    public void GetHighestWonAscension_FastPath_ReturnsFromDictionary()
    {
        var db = new StatsDb();
        db.HighestWonAscensions["CHARACTER.IRONCLAD"] = 7;
        Assert.Equal(7, StatsAggregator.GetHighestWonAscension(db, "CHARACTER.IRONCLAD"));
    }

    [Fact]
    public void GetHighestWonAscension_NullCharacter_ReturnsGlobalMax()
    {
        var db = new StatsDb();
        db.HighestWonAscensions["CHARACTER.IRONCLAD"] = 5;
        db.HighestWonAscensions["CHARACTER.SILENT"] = 9;
        Assert.Equal(9, StatsAggregator.GetHighestWonAscension(db, null));
    }

    [Fact]
    public void GetHighestWonAscension_NoData_ReturnsNull()
    {
        var db = new StatsDb();
        Assert.Null(StatsAggregator.GetHighestWonAscension(db, "CHARACTER.IRONCLAD"));
    }

    [Fact]
    public void GetHighestWonAscension_FallbackScan_FindsMaxFromCardContext()
    {
        var db = new StatsDb();
        // Fast path is empty — fallback scans card context maps
        db.GetOrCreate("CARD.STRIKE", new RunContext("CHARACTER.IRONCLAD", 5, 1, "standard", "v0.1")).RunsWon = 1;
        db.GetOrCreate("CARD.DEFEND", new RunContext("CHARACTER.IRONCLAD", 3, 1, "standard", "v0.1")).RunsWon = 1;
        Assert.Equal(5, StatsAggregator.GetHighestWonAscension(db, "CHARACTER.IRONCLAD"));
    }

    // --- AggregateRelicsByAct ---

    [Fact]
    public void AggregateRelicsByAct_SumsRunsPresent()
    {
        var contextMap = new Dictionary<string, RelicStat>
        {
            ["CHARACTER.IRONCLAD|0|1|standard|v0.1"] = new RelicStat { RunsPresent = 3, RunsWon = 2 },
            ["CHARACTER.IRONCLAD|5|1|standard|v0.1"] = new RelicStat { RunsPresent = 2, RunsWon = 1 },
        };
        var result = StatsAggregator.AggregateRelicsByAct(contextMap, character: null, gameMode: "standard");
        Assert.Single(result);
        Assert.Equal(5, result[1].RunsPresent);
        Assert.Equal(3, result[1].RunsWon);
    }

    [Fact]
    public void AggregateRelicsByAct_FiltersByCharacter()
    {
        var contextMap = new Dictionary<string, RelicStat>
        {
            ["CHARACTER.IRONCLAD|0|1|standard|v0.1"] = new RelicStat { RunsPresent = 4 },
            ["CHARACTER.SILENT|0|1|standard|v0.1"]   = new RelicStat { RunsPresent = 2 },
        };
        var result = StatsAggregator.AggregateRelicsByAct(contextMap, character: "CHARACTER.IRONCLAD", gameMode: "standard");
        Assert.Single(result);
        Assert.Equal(4, result[1].RunsPresent);
    }


    [Fact]
    public void AggregateByAct_SumsAcrossCharactersAndAscensions()
    {
        var contextMap = new Dictionary<string, CardStat>
        {
            ["CHARACTER.IRONCLAD|0|1|standard|v0.98.0"] = new CardStat { RunsOffered = 3, RunsPicked = 2, RunsWon = 1 },
            ["CHARACTER.IRONCLAD|5|1|standard|v0.98.0"] = new CardStat { RunsOffered = 2, RunsPicked = 1, RunsWon = 1 },
        };

        var result = StatsAggregator.AggregateByAct(contextMap);

        Assert.Single(result);
        Assert.Equal(5, result[1].RunsOffered);
        Assert.Equal(3, result[1].RunsPicked);
        Assert.Equal(2, result[1].RunsWon);
    }

    [Fact]
    public void AggregateByAct_FiltersByCharacter()
    {
        var contextMap = new Dictionary<string, CardStat>
        {
            ["CHARACTER.IRONCLAD|0|1|standard|v0.98.0"] = new CardStat { RunsOffered = 5, RunsPicked = 3 },
            ["CHARACTER.SILENT|0|1|standard|v0.98.0"]   = new CardStat { RunsOffered = 2, RunsPicked = 1 },
        };

        var result = StatsAggregator.AggregateByAct(contextMap, character: "CHARACTER.IRONCLAD");

        Assert.Single(result);
        Assert.Equal(5, result[1].RunsOffered);
    }

    [Fact]
    public void AggregateByAct_FiltersByGameMode()
    {
        var contextMap = new Dictionary<string, CardStat>
        {
            ["CHARACTER.IRONCLAD|0|1|standard|v0.98.0"] = new CardStat { RunsOffered = 4, RunsPicked = 2 },
            ["CHARACTER.IRONCLAD|0|1|co_op|v0.98.0"]    = new CardStat { RunsOffered = 3, RunsPicked = 1 },
        };

        var result = StatsAggregator.AggregateByAct(contextMap, character: null, gameMode: "standard");

        Assert.Single(result);
        Assert.Equal(4, result[1].RunsOffered);
    }

    [Fact]
    public void AggregateByAct_KeepsActsSeparate()
    {
        var contextMap = new Dictionary<string, CardStat>
        {
            ["CHARACTER.IRONCLAD|0|1|standard|v0.98.0"] = new CardStat { RunsOffered = 4, RunsPicked = 2 },
            ["CHARACTER.IRONCLAD|0|2|standard|v0.98.0"] = new CardStat { RunsOffered = 2, RunsPicked = 1 },
        };

        var result = StatsAggregator.AggregateByAct(contextMap);

        Assert.Equal(2, result.Count);
        Assert.Equal(4, result[1].RunsOffered);
        Assert.Equal(2, result[2].RunsOffered);
    }
}
