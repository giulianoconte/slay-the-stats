using Xunit;
using SlayTheStats;

namespace SlayTheStats.Tests;

public class StatsAggregatorTests
{
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
