namespace SlayTheStats;

public static class StatsAggregator
{
    /// <summary>
    /// Aggregates a card's per-context stats into per-act totals,
    /// summing across all characters, ascensions, game modes, and build versions.
    /// </summary>
    public static Dictionary<int, CardStat> AggregateByAct(Dictionary<string, CardStat> contextMap)
        => AggregateByAct(contextMap, character: null, gameMode: null);

    /// <summary>
    /// Aggregates a card's per-context stats into per-act totals,
    /// optionally filtering by character and/or game mode.
    /// Pass null to skip that filter.
    /// </summary>
    public static Dictionary<int, CardStat> AggregateByAct(
        Dictionary<string, CardStat> contextMap,
        string? character,
        string? gameMode = "standard")
    {
        var result = new Dictionary<int, CardStat>();

        foreach (var (key, stat) in contextMap)
        {
            var ctx = RunContext.Parse(key);
            if (character != null && ctx.Character != character) continue;
            if (gameMode != null && ctx.GameMode != gameMode) continue;

            if (!result.TryGetValue(ctx.Act, out var agg))
            {
                agg = new CardStat();
                result[ctx.Act] = agg;
            }

            agg.Offered     += stat.Offered;
            agg.Picked      += stat.Picked;
            agg.Won         += stat.Won;
            agg.RunsOffered += stat.RunsOffered;
            agg.RunsPicked  += stat.RunsPicked;
            agg.RunsWon     += stat.RunsWon;
        }

        return result;
    }

    /// <summary>
    /// Returns the overall win-rate percentage for a character in a given game mode,
    /// or 50.0 as a neutral fallback if no data exists yet.
    /// </summary>
    public static double GetCharacterWR(StatsDb db, string character, string gameMode = "standard")
    {
        var key = $"{character}|{gameMode}";
        if (!db.Characters.TryGetValue(key, out var stat) || stat.Runs == 0)
            return 50.0;
        return 100.0 * stat.Wins / stat.Runs;
    }

    /// <summary>
    /// Returns the expected pick rate for a single card, accounting for the skip option.
    /// Baseline = (1 - skipRate) / 3, where skipRate = TotalSkips / TotalRewardScreens.
    /// Falls back to 1/3 if no data.
    /// </summary>
    public static double GetPickRateBaseline(StatsDb db)
    {
        if (db.TotalRewardScreens == 0) return 1.0 / 3.0;
        var skipRate = (double)db.TotalSkips / db.TotalRewardScreens;
        return (1.0 - skipRate) / 3.0;
    }

    /// <summary>
    /// Aggregates a relic's per-context stats into per-act totals,
    /// optionally filtering by character and/or game mode.
    /// Pass null to skip that filter.
    /// </summary>
    public static Dictionary<int, RelicStat> AggregateRelicsByAct(
        Dictionary<string, RelicStat> contextMap,
        string? character,
        string? gameMode = "standard")
    {
        var result = new Dictionary<int, RelicStat>();

        foreach (var (key, stat) in contextMap)
        {
            var ctx = RunContext.Parse(key);
            if (character != null && ctx.Character != character) continue;
            if (gameMode != null && ctx.GameMode != gameMode) continue;

            if (!result.TryGetValue(ctx.Act, out var agg))
            {
                agg = new RelicStat();
                result[ctx.Act] = agg;
            }

            agg.RunsPresent += stat.RunsPresent;
            agg.RunsWon     += stat.RunsWon;
        }

        return result;
    }
}
