namespace SlayTheStats;

public static class StatsAggregator
{
    /// <summary>
    /// Aggregates a card's per-context stats into per-act totals,
    /// summing across all characters and ascensions.
    /// </summary>
    public static Dictionary<int, CardStat> AggregateByAct(Dictionary<string, CardStat> contextMap)
    {
        var result = new Dictionary<int, CardStat>();

        foreach (var (key, stat) in contextMap)
        {
            var ctx = RunContext.Parse(key);
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
}
