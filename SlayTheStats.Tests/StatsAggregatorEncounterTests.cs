using Xunit;
using SlayTheStats;

namespace SlayTheStats.Tests;

public class StatsAggregatorEncounterTests
{
    [Fact]
    public void AggregateEncountersByAct_SumsCorrectly()
    {
        var contextMap = new Dictionary<string, EncounterEvent>
        {
            ["CHARACTER.IRONCLAD|5|1|standard|v1.0|default"] = new()
            {
                Fought = 3, Died = 1, WonRun = 2,
                TurnsTakenSum = 12, DamageTakenSum = 30, DamageTakenSqSum = 400,
                HpEnteringSum = 200, MaxHpSum = 240, PotionsUsedSum = 2,
                DmgPctSum = 0.375, DmgPctSqSum = 0.055
            },
            ["CHARACTER.IRONCLAD|5|1|standard|v1.1|default"] = new()
            {
                Fought = 2, Died = 0, WonRun = 1,
                TurnsTakenSum = 8, DamageTakenSum = 20, DamageTakenSqSum = 250,
                HpEnteringSum = 140, MaxHpSum = 160, PotionsUsedSum = 1,
                DmgPctSum = 0.25, DmgPctSqSum = 0.04
            },
        };

        var filter = new AggregationFilter { Character = "CHARACTER.IRONCLAD", GameMode = "standard" };
        var result = StatsAggregator.AggregateEncountersByAct(contextMap, filter);

        Assert.True(result.ContainsKey(1));
        var act1 = result[1];
        Assert.Equal(5, act1.Fought);
        Assert.Equal(1, act1.Died);
        Assert.Equal(3, act1.WonRun);
        Assert.Equal(20, act1.TurnsTakenSum);
        Assert.Equal(50, act1.DamageTakenSum);
        Assert.Equal(650, act1.DamageTakenSqSum);
        Assert.Equal(340, act1.HpEnteringSum);
        Assert.Equal(400, act1.MaxHpSum);
        Assert.Equal(3, act1.PotionsUsedSum);
        Assert.Equal(0.625, act1.DmgPctSum, 4);
        Assert.Equal(0.095, act1.DmgPctSqSum, 4);
    }

    [Fact]
    public void AggregateEncountersByAct_FiltersCharacter()
    {
        var contextMap = new Dictionary<string, EncounterEvent>
        {
            ["CHARACTER.IRONCLAD|0|1|standard|v1.0|default"] = new() { Fought = 3 },
            ["CHARACTER.DEFECT|0|1|standard|v1.0|default"] = new() { Fought = 5 },
        };

        var filter = new AggregationFilter { Character = "CHARACTER.IRONCLAD", GameMode = "standard" };
        var result = StatsAggregator.AggregateEncountersByAct(contextMap, filter);

        Assert.Equal(3, result[1].Fought);
    }

    [Fact]
    public void AggregateEncountersByAct_FiltersAscensionRange()
    {
        var contextMap = new Dictionary<string, EncounterEvent>
        {
            ["CHARACTER.IRONCLAD|3|1|standard|v1.0|default"] = new() { Fought = 2 },
            ["CHARACTER.IRONCLAD|7|1|standard|v1.0|default"] = new() { Fought = 4 },
            ["CHARACTER.IRONCLAD|10|1|standard|v1.0|default"] = new() { Fought = 1 },
        };

        var filter = new AggregationFilter { GameMode = "standard", AscensionMin = 5, AscensionMax = 10 };
        var result = StatsAggregator.AggregateEncountersByAct(contextMap, filter);

        Assert.Equal(5, result[1].Fought); // 4 + 1 (asc 7 and 10)
    }

    [Fact]
    public void AggregateEncountersByAct_SeparatesByAct()
    {
        var contextMap = new Dictionary<string, EncounterEvent>
        {
            ["CHARACTER.IRONCLAD|0|1|standard|v1.0|default"] = new() { Fought = 3 },
            ["CHARACTER.IRONCLAD|0|2|standard|v1.0|default"] = new() { Fought = 5 },
        };

        var filter = new AggregationFilter { GameMode = "standard" };
        var result = StatsAggregator.AggregateEncountersByAct(contextMap, filter);

        Assert.Equal(3, result[1].Fought);
        Assert.Equal(5, result[2].Fought);
    }

    [Fact]
    public void GetEncounterDmgPctBaseline_ComputesAverage()
    {
        var db = new StatsDb();
        // Two encounters: one with 0.25 dmg%, one with 0.15 dmg%
        var enc1 = db.GetOrCreateEncounter("ENCOUNTER.A_WEAK", new RunContext("CHARACTER.IRONCLAD", 0, 1, "standard", "v1.0", "default"));
        enc1.Fought = 4;
        enc1.DmgPctSum = 1.0; // avg = 0.25

        var enc2 = db.GetOrCreateEncounter("ENCOUNTER.B_WEAK", new RunContext("CHARACTER.IRONCLAD", 0, 1, "standard", "v1.0", "default"));
        enc2.Fought = 6;
        enc2.DmgPctSum = 0.9; // avg = 0.15

        db.EncounterMeta["ENCOUNTER.A_WEAK"] = new EncounterMeta { Category = "weak" };
        db.EncounterMeta["ENCOUNTER.B_WEAK"] = new EncounterMeta { Category = "weak" };

        var filter = new AggregationFilter { GameMode = "standard" };
        var baseline = StatsAggregator.GetEncounterDmgPctBaseline(db, filter, "weak", 1);

        // (1.0 + 0.9) / (4 + 6) * 100 = 19.0
        Assert.Equal(19.0, baseline, 1);
    }

    [Fact]
    public void GetEncounterDeathRateBaseline_ComputesAverage()
    {
        var db = new StatsDb();
        var enc1 = db.GetOrCreateEncounter("ENCOUNTER.A_ELITE", new RunContext("CHARACTER.IRONCLAD", 0, 1, "standard", "v1.0", "default"));
        enc1.Fought = 10;
        enc1.Died = 3;

        var enc2 = db.GetOrCreateEncounter("ENCOUNTER.B_ELITE", new RunContext("CHARACTER.IRONCLAD", 0, 1, "standard", "v1.0", "default"));
        enc2.Fought = 10;
        enc2.Died = 1;

        db.EncounterMeta["ENCOUNTER.A_ELITE"] = new EncounterMeta { Category = "elite" };
        db.EncounterMeta["ENCOUNTER.B_ELITE"] = new EncounterMeta { Category = "elite" };

        var filter = new AggregationFilter { GameMode = "standard" };
        var baseline = StatsAggregator.GetEncounterDeathRateBaseline(db, filter, "elite", 1);

        // (3 + 1) / (10 + 10) * 100 = 20.0
        Assert.Equal(20.0, baseline, 1);
    }

    [Fact]
    public void GetEncounterDmgPctBaseline_FiltersByCategory()
    {
        var db = new StatsDb();
        var enc1 = db.GetOrCreateEncounter("ENCOUNTER.A_WEAK", new RunContext("CHARACTER.IRONCLAD", 0, 1, "standard", "v1.0", "default"));
        enc1.Fought = 10;
        enc1.DmgPctSum = 2.0;

        var enc2 = db.GetOrCreateEncounter("ENCOUNTER.A_ELITE", new RunContext("CHARACTER.IRONCLAD", 0, 1, "standard", "v1.0", "default"));
        enc2.Fought = 5;
        enc2.DmgPctSum = 1.5;

        db.EncounterMeta["ENCOUNTER.A_WEAK"] = new EncounterMeta { Category = "weak" };
        db.EncounterMeta["ENCOUNTER.A_ELITE"] = new EncounterMeta { Category = "elite" };

        var filter = new AggregationFilter { GameMode = "standard" };

        // Only weak encounters
        var weakBaseline = StatsAggregator.GetEncounterDmgPctBaseline(db, filter, "weak", 1);
        Assert.Equal(20.0, weakBaseline, 1); // 2.0/10 * 100

        // Only elite encounters
        var eliteBaseline = StatsAggregator.GetEncounterDmgPctBaseline(db, filter, "elite", 1);
        Assert.Equal(30.0, eliteBaseline, 1); // 1.5/5 * 100
    }
}
