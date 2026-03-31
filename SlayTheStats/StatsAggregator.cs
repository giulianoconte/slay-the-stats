namespace SlayTheStats;

public static class StatsAggregator
{
    /// <summary>
    /// Aggregates a card's per-context stats into per-act totals,
    /// summing across all characters, ascensions, game modes, and build versions.
    /// </summary>
    public static Dictionary<int, CardStat> AggregateByAct(Dictionary<string, CardStat> contextMap)
        => AggregateByAct(contextMap, character: null, gameMode: null, onlyAscension: null);

    /// <summary>
    /// Aggregates a card's per-context stats into per-act totals,
    /// optionally filtering by character, game mode, and/or max ascension.
    /// Pass null to skip a filter.
    /// </summary>
    public static Dictionary<int, CardStat> AggregateByAct(
        Dictionary<string, CardStat> contextMap,
        string? character,
        string? gameMode = "standard",
        int? onlyAscension = null)
    {
        var result = new Dictionary<int, CardStat>();

        foreach (var (key, stat) in contextMap)
        {
            var ctx = RunContext.Parse(key);
            if (character != null && ctx.Character != character) continue;
            if (gameMode != null && ctx.GameMode != gameMode) continue;
            if (onlyAscension != null && ctx.Ascension != onlyAscension) continue;

            if (!result.TryGetValue(ctx.Act, out var agg))
            {
                agg = new CardStat();
                result[ctx.Act] = agg;
            }

            agg.Offered      += stat.Offered;
            agg.Picked       += stat.Picked;
            agg.Won          += stat.Won;
            agg.RunsOffered  += stat.RunsOffered;
            agg.RunsPicked   += stat.RunsPicked;
            agg.RunsPresent  += stat.RunsPresent;
            agg.RunsWon      += stat.RunsWon;
            agg.RunsShopSeen   += stat.RunsShopSeen;
            agg.RunsShopBought += stat.RunsShopBought;
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
    /// Returns the overall win-rate percentage across all characters in a given game mode,
    /// or 50.0 as a neutral fallback if no data exists yet.
    /// </summary>
    public static double GetGlobalWR(StatsDb db, string gameMode = "standard")
    {
        int totalRuns = 0, totalWins = 0;
        foreach (var (key, stat) in db.Characters)
        {
            if (!key.EndsWith("|" + gameMode)) continue;
            totalRuns += stat.Runs;
            totalWins += stat.Wins;
        }
        return totalRuns == 0 ? 50.0 : 100.0 * totalWins / totalRuns;
    }

    /// <summary>
    /// Returns the expected pick rate for a single card as a percentage (0–100),
    /// accounting for the skip option.
    /// Baseline = (1 - skipRate) / 3 × 100, where skipRate = TotalSkips / TotalRewardScreens.
    /// Falls back to 100/3 ≈ 33.3 if no data.
    /// </summary>
    public static double GetPickRateBaseline(StatsDb db)
    {
        if (db.TotalRewardScreens == 0) return 100.0 / 3.0;
        var skipRate = (double)db.TotalSkips / db.TotalRewardScreens;
        return (1.0 - skipRate) / 3.0 * 100.0;
    }

    /// <summary>
    /// Returns the highest ascension won for a character, or null if they have no wins.
    /// Reads from HighestWonAscensions first (O(1)); falls back to scanning card context maps
    /// for data recorded before that field was introduced.
    /// </summary>
    /// <summary>
    /// Returns the highest ascension won for a character, or null if they have no wins.
    /// When character is null, returns the max across all characters (for use in the compendium
    /// where no character context is available).
    /// Reads from HighestWonAscensions first (O(1)); falls back to scanning card context maps
    /// for data recorded before that field was introduced.
    /// </summary>
    public static int? GetHighestWonAscension(StatsDb db, string? character)
    {
        // Fast path: populated by RunParser going forward
        if (character != null)
        {
            if (db.HighestWonAscensions.TryGetValue(character, out var asc))
                return asc;
        }
        else if (db.HighestWonAscensions.Count > 0)
        {
            return db.HighestWonAscensions.Values.Max();
        }

        // Fallback: derive from card context maps for older data.
        // character == null means no filter — take the global max.
        int? maxAsc = null;
        foreach (var contextMap in db.Cards.Values)
        {
            foreach (var (key, stat) in contextMap)
            {
                if (stat.RunsWon == 0) continue;
                var ctx = RunContext.Parse(key);
                if (character != null && ctx.Character != character) continue;
                if (maxAsc == null || ctx.Ascension > maxAsc)
                    maxAsc = ctx.Ascension;
            }
        }
        return maxAsc;
    }

    /// <summary>
    /// Aggregates a relic's per-context stats into per-act totals,
    /// optionally filtering by character, game mode, and/or max ascension.
    /// Pass null to skip a filter.
    /// </summary>
    public static Dictionary<int, RelicStat> AggregateRelicsByAct(
        Dictionary<string, RelicStat> contextMap,
        string? character,
        string? gameMode = "standard",
        int? onlyAscension = null)
    {
        var result = new Dictionary<int, RelicStat>();

        foreach (var (key, stat) in contextMap)
        {
            var ctx = RunContext.Parse(key);
            if (character != null && ctx.Character != character) continue;
            if (gameMode != null && ctx.GameMode != gameMode) continue;
            if (onlyAscension != null && ctx.Ascension != onlyAscension) continue;

            if (!result.TryGetValue(ctx.Act, out var agg))
            {
                agg = new RelicStat();
                result[ctx.Act] = agg;
            }

            agg.RunsPresent    += stat.RunsPresent;
            agg.RunsWon        += stat.RunsWon;
            agg.RunsShopSeen   += stat.RunsShopSeen;
            agg.RunsShopBought += stat.RunsShopBought;
        }

        return result;
    }

    /// <summary>
    /// Returns the overall shop buy rate as a percentage (0–100): total shop purchases / total shop
    /// appearances across all cards and relics. Falls back to 20.0 if no shop data exists yet.
    /// </summary>
    public static double GetShopBuyRateBaseline(StatsDb db)
    {
        int totalSeen = 0, totalBought = 0;
        foreach (var contextMap in db.Cards.Values)
            foreach (var stat in contextMap.Values)
            { totalSeen += stat.RunsShopSeen; totalBought += stat.RunsShopBought; }
        foreach (var contextMap in db.Relics.Values)
            foreach (var stat in contextMap.Values)
            { totalSeen += stat.RunsShopSeen; totalBought += stat.RunsShopBought; }
        return totalSeen == 0 ? 20.0 : 100.0 * totalBought / totalSeen;
    }
}
