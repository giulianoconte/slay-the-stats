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
