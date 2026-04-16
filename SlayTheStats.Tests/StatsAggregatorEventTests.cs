using Xunit;
using SlayTheStats;

namespace SlayTheStats.Tests;

public class StatsAggregatorEventTests
{
    /// <summary>
    /// Builds a minimal visit. Callers override only what the test needs.
    /// </summary>
    private static EventVisit Visit(
        string character = "CHARACTER.IRONCLAD",
        int ascension = 0,
        int act = 1,
        string buildVersion = "v0.3.0",
        string profile = "default",
        bool won = false,
        string? terminalOption = "CLIMB",
        int damageTaken = 0,
        int hpHealed = 0,
        int maxHpGained = 0,
        int maxHpLost = 0,
        int goldGained = 0,
        int goldSpent = 0,
        int goldLost = 0,
        int goldStolen = 0)
    {
        var v = new EventVisit
        {
            Character = character, Ascension = ascension, Act = act,
            GameMode = "standard", BuildVersion = buildVersion, Profile = profile,
            Won = won,
            DamageTaken = damageTaken, HpHealed = hpHealed,
            MaxHpGained = maxHpGained, MaxHpLost = maxHpLost,
            GoldGained = goldGained, GoldSpent = goldSpent,
            GoldLost = goldLost, GoldStolen = goldStolen,
        };
        if (terminalOption != null)
            v.OptionPath.Add($"EVENT_X.pages.INITIAL.options.{terminalOption}.title");
        return v;
    }

    [Fact]
    public void Aggregate_EmptyDb_ReturnsZeroTotals()
    {
        var agg = StatsAggregator.AggregateEvent(new StatsDb(), "EVENT.DROWNING_BEACON");
        Assert.Equal(0, agg.TotalVisits);
        Assert.Empty(agg.Options);
    }

    [Fact]
    public void Aggregate_BucketsByTerminalOption()
    {
        var db = new StatsDb();
        db.EventVisits["EVENT.X"] = new List<EventVisit>
        {
            Visit(terminalOption: "CLIMB",  won: true,  hpHealed: 5, damageTaken: 0),
            Visit(terminalOption: "CLIMB",  won: false, hpHealed: 0, damageTaken: 10),
            Visit(terminalOption: "DESCEND", won: true,  hpHealed: 0, damageTaken: 3),
        };

        var agg = StatsAggregator.AggregateEvent(db, "EVENT.X");
        Assert.Equal(3, agg.TotalVisits);
        Assert.Equal(2, agg.TotalWins);

        var climb = agg.Options["CLIMB"];
        Assert.Equal(2, climb.Picks);
        Assert.Equal(1, climb.Wins);
        Assert.Equal(0.5, climb.WinRate);

        var descend = agg.Options["DESCEND"];
        Assert.Equal(1, descend.Picks);
        Assert.Equal(1, descend.Wins);
    }

    [Fact]
    public void Aggregate_FilterByCharacter()
    {
        var db = new StatsDb();
        db.EventVisits["EVENT.X"] = new List<EventVisit>
        {
            Visit(character: "CHARACTER.IRONCLAD", won: true),
            Visit(character: "CHARACTER.SILENT",   won: false),
        };

        var filter = new AggregationFilter { Character = "CHARACTER.IRONCLAD", GameMode = "standard" };
        var agg = StatsAggregator.AggregateEvent(db, "EVENT.X", filter);

        Assert.Equal(1, agg.TotalVisits);
        Assert.Equal(1, agg.TotalWins);
    }

    [Fact]
    public void Aggregate_FilterByAscensionRange()
    {
        var db = new StatsDb();
        db.EventVisits["EVENT.X"] = new List<EventVisit>
        {
            Visit(ascension: 5),  // excluded
            Visit(ascension: 7),
            Visit(ascension: 10), // excluded
        };

        var filter = new AggregationFilter { AscensionMin = 6, AscensionMax = 9, GameMode = "standard" };
        var agg = StatsAggregator.AggregateEvent(db, "EVENT.X", filter);
        Assert.Equal(1, agg.TotalVisits);
    }

    [Fact]
    public void Aggregate_MultiStepPath_OfferedAndPickedPerStep()
    {
        // Slippery-Bridge-style recursive event:
        //   visit A: [HOLD_ON_0, OVERCOME]       — held-on once, overcame
        //   visit B: [HOLD_ON_0, HOLD_ON_1, OVERCOME] — held-on twice, overcame
        //   visit C: [OVERCOME]                  — overcame immediately
        var db = new StatsDb();
        db.EventVisits["EVENT.SB"] = new List<EventVisit>
        {
            MakePath("EVENT.SB", won: true,  path: new[] { "HOLD_ON_0", "OVERCOME" }),
            MakePath("EVENT.SB", won: false, path: new[] { "HOLD_ON_0", "HOLD_ON_1", "OVERCOME" }),
            MakePath("EVENT.SB", won: true,  path: new[] { "OVERCOME" }),
        };

        var agg = StatsAggregator.AggregateEvent(db, "EVENT.SB");
        Assert.Equal(3, agg.TotalVisits);

        // Denominator for pick-rate = TotalVisits = 3 (same for all options,
        // like shop pick% where denominator = times-event-seen).
        // Picks = times the option appeared in a visit's option_path.
        var overcome = agg.Options["OVERCOME"];
        // Every visit ends with OVERCOME → picked 3 times.
        Assert.Equal(3, overcome.Picks);
        Assert.Equal(2, overcome.Wins); // visits A (won) and C (won)

        var h0 = agg.Options["HOLD_ON_0"];
        // Visits A and B picked HOLD_ON_0 at step 0.
        Assert.Equal(2, h0.Picks);

        var h1 = agg.Options["HOLD_ON_1"];
        // Only visit B picked HOLD_ON_1 at step 1.
        Assert.Equal(1, h1.Picks);
    }

    private static EventVisit MakePath(string eventId, bool won, string[] path)
    {
        var v = new EventVisit
        {
            Character = "CHARACTER.IRONCLAD", Ascension = 0, Act = 1,
            GameMode = "standard", BuildVersion = "v0.3.0", Profile = "default",
            Won = won,
        };
        foreach (var seg in path)
            v.OptionPath.Add($"{eventId}.pages.P.options.{seg}.title");
        return v;
    }

    [Fact]
    public void EmptyOptionPath_DoesNotCreateBucket()
    {
        // Visits with no option path (shouldn't happen in practice, but defensive)
        // contribute to TotalVisits but no option bucket exists.
        var db = new StatsDb();
        db.EventVisits["EVENT.X"] = new List<EventVisit>
        {
            Visit(terminalOption: null, won: true),
        };

        var agg = StatsAggregator.AggregateEvent(db, "EVENT.X");
        Assert.Equal(1, agg.TotalVisits);
        Assert.Empty(agg.Options);
    }
}
