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

    // ── Shape classification ────────────────────────────────────────
    //
    // These drive the Future of Potions fix: events with a single loc key
    // whose options differ only by `variables` (e.g. Rarity=Common vs
    // Rarity=Rare) must split into per-variable-value buckets at
    // aggregation time — otherwise all options collapse into one bucket
    // that renders identical Picks/Win% for every option.

    private static EventVisit FopVisit(string rarity, bool won, string character = "CHARACTER.IRONCLAD", int ascension = 0)
    {
        var v = new EventVisit
        {
            Character = character, Ascension = ascension, Act = 1,
            GameMode = "standard", BuildVersion = "v0.3.0", Profile = "default",
            Won = won,
        };
        v.OptionPath.Add("THE_FUTURE_OF_POTIONS.pages.INITIAL.options.POTION.title");
        v.Variables["Rarity"] = rarity;
        return v;
    }

    [Fact]
    public void Shape_DistinctKeys_ClassifiedAsShape1()
    {
        var db = new StatsDb();
        db.EventVisits["EVENT.X"] = new List<EventVisit>
        {
            Visit(terminalOption: "CLIMB"),
            Visit(terminalOption: "DESCEND"),
        };

        var agg = StatsAggregator.AggregateEvent(db, "EVENT.X");
        Assert.Equal(EventShape.Shape1_DistinctKeys, agg.Shape);
        Assert.Null(agg.BucketVariable);
        // Bucket keys are plain terminals.
        Assert.Contains("CLIMB", agg.Options.Keys);
        Assert.Contains("DESCEND", agg.Options.Keys);
    }

    [Fact]
    public void Shape_FutureOfPotions_ClassifiedAsShape2ByRarity()
    {
        var db = new StatsDb();
        db.EventVisits["EVENT.FOP"] = new List<EventVisit>
        {
            FopVisit("Common",   won: true),
            FopVisit("Uncommon", won: false),
            FopVisit("Rare",     won: true),
            FopVisit("Common",   won: false),
        };

        var agg = StatsAggregator.AggregateEvent(db, "EVENT.FOP");
        Assert.Equal(EventShape.Shape2_VariableKey, agg.Shape);
        Assert.Equal("Rarity", agg.BucketVariable);

        // Buckets split: "POTION|Common" has 2 picks (1 win), "|Uncommon" 1/0, "|Rare" 1/1.
        Assert.Equal(3, agg.Options.Count);
        var common = agg.Options[$"POTION{EventAggregate.BucketKeySep}Common"];
        Assert.Equal(2, common.Picks);
        Assert.Equal(1, common.Wins);
        var uncommon = agg.Options[$"POTION{EventAggregate.BucketKeySep}Uncommon"];
        Assert.Equal(1, uncommon.Picks);
        Assert.Equal(0, uncommon.Wins);
        var rare = agg.Options[$"POTION{EventAggregate.BucketKeySep}Rare"];
        Assert.Equal(1, rare.Picks);
        Assert.Equal(1, rare.Wins);
    }

    [Fact]
    public void Shape_SingleVisit_ReturnsUnknown()
    {
        var db = new StatsDb();
        db.EventVisits["EVENT.FOP"] = new List<EventVisit>
        {
            FopVisit("Rare", won: true),
        };

        var agg = StatsAggregator.AggregateEvent(db, "EVENT.FOP");
        Assert.Equal(EventShape.Unknown, agg.Shape);
        Assert.Null(agg.BucketVariable);
        // Without a bucket variable, the plain terminal key is used.
        Assert.Contains("POTION", agg.Options.Keys);
    }

    [Fact]
    public void Shape_SameKeyStableVars_StaysUnknown()
    {
        // Two visits, same terminal, same variable values → can't distinguish
        // "Shape1 where player always picks the same option" from "Shape2
        // that just hasn't shown variation yet". Conservative default: Unknown.
        var db = new StatsDb();
        db.EventVisits["EVENT.FOP"] = new List<EventVisit>
        {
            FopVisit("Rare", won: true),
            FopVisit("Rare", won: false),
        };

        var agg = StatsAggregator.AggregateEvent(db, "EVENT.FOP");
        Assert.Equal(EventShape.Unknown, agg.Shape);
    }

    [Fact]
    public void Shape_PicksLowestCardinalityVariable()
    {
        // When multiple variables vary, BucketVariable should pick the one
        // with fewest distinct values — the most-aggregated semantic split.
        // Future of Potions: Rarity has 4 possible values, Potion has ~20,
        // Type has 3. Type should win (alphabetical tie-break wouldn't reach
        // it if cardinalities were equal). Here we stage Rarity=3 distinct,
        // Potion=4 distinct — Rarity wins.
        var db = new StatsDb();
        var visits = new List<EventVisit>();
        string[] rarities = { "Common", "Uncommon", "Rare" };
        string[] potions  = { "FIRE", "ATTACK", "FAIRY", "ESSENCE" };
        for (int i = 0; i < 4; i++)
        {
            var v = FopVisit(rarities[i % 3], won: false);
            v.Variables["Potion"] = potions[i];
            visits.Add(v);
        }
        db.EventVisits["EVENT.FOP"] = visits;

        var agg = StatsAggregator.AggregateEvent(db, "EVENT.FOP");
        Assert.Equal(EventShape.Shape2_VariableKey, agg.Shape);
        Assert.Equal("Rarity", agg.BucketVariable); // 3 distinct < 4 distinct
    }

    [Fact]
    public void Shape_RecomputesEveryAggregation()
    {
        // First aggregation with homogeneous variable → Unknown shape.
        // After adding a visit with a new variable value → flips to Shape2.
        // No persistence; same db just re-aggregated.
        var db = new StatsDb();
        db.EventVisits["EVENT.FOP"] = new List<EventVisit>
        {
            FopVisit("Rare", won: true),
            FopVisit("Rare", won: false),
        };

        var agg1 = StatsAggregator.AggregateEvent(db, "EVENT.FOP");
        Assert.Equal(EventShape.Unknown, agg1.Shape);

        db.EventVisits["EVENT.FOP"].Add(FopVisit("Common", won: true));

        var agg2 = StatsAggregator.AggregateEvent(db, "EVENT.FOP");
        Assert.Equal(EventShape.Shape2_VariableKey, agg2.Shape);
        Assert.Equal("Rarity", agg2.BucketVariable);
    }

    [Fact]
    public void Shape_BuildBucketKey_Shape1ReturnsTerminalOnly()
    {
        var agg = new EventAggregate { Shape = EventShape.Shape1_DistinctKeys };
        var key = agg.BuildBucketKey("CLIMB", new Dictionary<string, string> { ["X"] = "y" });
        Assert.Equal("CLIMB", key);
    }

    [Fact]
    public void Shape_BuildBucketKey_Shape2AppendsVariable()
    {
        var agg = new EventAggregate { Shape = EventShape.Shape2_VariableKey, BucketVariable = "Rarity" };
        var key = agg.BuildBucketKey("POTION", new Dictionary<string, string> { ["Rarity"] = "Rare" });
        Assert.Equal($"POTION{EventAggregate.BucketKeySep}Rare", key);
    }

    [Fact]
    public void Shape_ClassifiedFromFullDb_NotFilteredSlice()
    {
        // Regression: shape must be a property of the event across all recorded
        // visits, not of the filtered slice. Historical visits are Defect-only;
        // user starts an Ironclad run and hovers Future of Potions. Without this
        // behavior the Ironclad-filtered list drops to 0–1, classify returns
        // Unknown, and all three rarity options collapse into one bucket.
        var db = new StatsDb();
        db.EventVisits["EVENT.FOP"] = new List<EventVisit>
        {
            FopVisit("Common",   won: true,  character: "CHARACTER.DEFECT"),
            FopVisit("Uncommon", won: false, character: "CHARACTER.DEFECT"),
            FopVisit("Rare",     won: true,  character: "CHARACTER.DEFECT"),
        };

        var filter = new AggregationFilter { Character = "CHARACTER.IRONCLAD", GameMode = "standard" };
        var agg = StatsAggregator.AggregateEvent(db, "EVENT.FOP", filter);

        // Filter drops every visit → TotalVisits=0 in the filtered aggregate.
        Assert.Equal(0, agg.TotalVisits);
        // But shape is still correctly classified from the full db so a future
        // visit on Ironclad lands in the right Shape2 bucket immediately.
        Assert.Equal(EventShape.Shape2_VariableKey, agg.Shape);
        Assert.Equal("Rarity", agg.BucketVariable);
    }

    [Fact]
    public void Shape_BuildBucketKey_Shape2MissingVariableFallsBackToTerminal()
    {
        // If the hovered option is missing the BucketVariable (shouldn't
        // happen for a well-behaved Shape2 event, but defensive), return
        // the plain terminal. The lookup then either matches a stray
        // same-name bucket or falls through to the 0-picks rendering.
        var agg = new EventAggregate { Shape = EventShape.Shape2_VariableKey, BucketVariable = "Rarity" };
        var key = agg.BuildBucketKey("POTION", new Dictionary<string, string>());
        Assert.Equal("POTION", key);
    }
}
