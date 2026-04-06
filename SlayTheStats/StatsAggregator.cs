namespace SlayTheStats;

/// <summary>
/// Filter parameters for stats aggregation. All fields are optional —
/// null/empty values mean "include all".
/// </summary>
public class AggregationFilter
{
    /// <summary>Single character filter (used by in-run tooltips). Overridden by Characters if set.</summary>
    public string? Character { get; set; }
    /// <summary>Multi-select character filter (used by compendium pane). Takes precedence over Character.</summary>
    public HashSet<string>? Characters { get; set; }
    public string GameMode { get; set; } = "standard";
    public int? AscensionMin { get; set; }
    public int? AscensionMax { get; set; }
    public string? VersionMin { get; set; }
    public string? VersionMax { get; set; }
    public string? Profile { get; set; }

    /// <summary>Returns true if a RunContext passes all filters.</summary>
    public bool Matches(RunContext ctx)
    {
        // Character filter
        if (Characters != null && Characters.Count > 0)
        {
            if (!Characters.Contains(ctx.Character)) return false;
        }
        else if (Character != null && ctx.Character != Character) return false;

        // Game mode
        if (GameMode != null && ctx.GameMode != GameMode) return false;

        // Ascension range
        if (AscensionMin != null && ctx.Ascension < AscensionMin) return false;
        if (AscensionMax != null && ctx.Ascension > AscensionMax) return false;

        // Version range (semantic comparison)
        if (VersionMin != null && CompareVersions(ctx.BuildVersion, VersionMin) < 0) return false;
        if (VersionMax != null && CompareVersions(ctx.BuildVersion, VersionMax) > 0) return false;

        // Profile
        if (Profile != null && ctx.Profile != Profile) return false;

        return true;
    }

    /// <summary>
    /// Compares two version strings semantically (e.g. "v0.4.10" > "v0.4.9").
    /// Strips leading 'v' prefix. Returns negative if a &lt; b, 0 if equal, positive if a &gt; b.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static string Strip(string v) => v.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? v[1..] : v;
        var partsA = Strip(a).Split('.');
        var partsB = Strip(b).Split('.');

        int maxLen = Math.Max(partsA.Length, partsB.Length);
        for (int i = 0; i < maxLen; i++)
        {
            int numA = i < partsA.Length && int.TryParse(partsA[i], out var na) ? na : 0;
            int numB = i < partsB.Length && int.TryParse(partsB[i], out var nb) ? nb : 0;
            if (numA != numB) return numA.CompareTo(numB);
        }
        return 0;
    }
}

public static class StatsAggregator
{
    /// <summary>
    /// Aggregates a card's per-context stats into per-act totals,
    /// summing across all characters, ascensions, game modes, and build versions.
    /// </summary>
    public static Dictionary<int, CardStat> AggregateByAct(Dictionary<string, CardStat> contextMap)
        => AggregateByAct(contextMap, new AggregationFilter { GameMode = null! });

    /// <summary>
    /// Aggregates a card's per-context stats into per-act totals,
    /// optionally filtering by character, game mode, and/or exact ascension.
    /// Legacy overload — prefer the AggregationFilter version.
    /// </summary>
    public static Dictionary<int, CardStat> AggregateByAct(
        Dictionary<string, CardStat> contextMap,
        string? character,
        string? gameMode = "standard",
        int? onlyAscension = null)
    {
        var filter = new AggregationFilter
        {
            Character = character,
            GameMode = gameMode ?? "standard",
        };
        if (onlyAscension != null)
        {
            filter.AscensionMin = onlyAscension;
            filter.AscensionMax = onlyAscension;
        }
        if (gameMode == null) filter.GameMode = null!;
        return AggregateByAct(contextMap, filter);
    }

    /// <summary>
    /// Aggregates a card's per-context stats into per-act totals using the given filter.
    /// </summary>
    public static Dictionary<int, CardStat> AggregateByAct(
        Dictionary<string, CardStat> contextMap,
        AggregationFilter filter)
    {
        var result = new Dictionary<int, CardStat>();

        foreach (var (key, stat) in contextMap)
        {
            var ctx = RunContext.Parse(key);
            if (!filter.Matches(ctx)) continue;

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
            agg.RunsOfferedUpgraded  += stat.RunsOfferedUpgraded;
            agg.RunsPickedUpgraded   += stat.RunsPickedUpgraded;
            agg.RunsShopSeenUpgraded   += stat.RunsShopSeenUpgraded;
            agg.RunsShopBoughtUpgraded += stat.RunsShopBoughtUpgraded;
            agg.CampfireUpgrades     += stat.CampfireUpgrades;
            agg.EventRelicUpgrades   += stat.EventRelicUpgrades;
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
    /// optionally filtering by character, game mode, and/or exact ascension.
    /// Legacy overload — prefer the AggregationFilter version.
    /// </summary>
    public static Dictionary<int, RelicStat> AggregateRelicsByAct(
        Dictionary<string, RelicStat> contextMap,
        string? character,
        string? gameMode = "standard",
        int? onlyAscension = null)
    {
        var filter = new AggregationFilter
        {
            Character = character,
            GameMode = gameMode ?? "standard",
        };
        if (onlyAscension != null)
        {
            filter.AscensionMin = onlyAscension;
            filter.AscensionMax = onlyAscension;
        }
        if (gameMode == null) filter.GameMode = null!;
        return AggregateRelicsByAct(contextMap, filter);
    }

    /// <summary>
    /// Aggregates a relic's per-context stats into per-act totals using the given filter.
    /// </summary>
    public static Dictionary<int, RelicStat> AggregateRelicsByAct(
        Dictionary<string, RelicStat> contextMap,
        AggregationFilter filter)
    {
        var result = new Dictionary<int, RelicStat>();

        foreach (var (key, stat) in contextMap)
        {
            var ctx = RunContext.Parse(key);
            if (!filter.Matches(ctx)) continue;

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
    /// Aggregates an encounter's per-context stats into per-act totals using the given filter.
    /// </summary>
    public static Dictionary<int, EncounterEvent> AggregateEncountersByAct(
        Dictionary<string, EncounterEvent> contextMap,
        AggregationFilter filter)
    {
        var result = new Dictionary<int, EncounterEvent>();

        foreach (var (key, stat) in contextMap)
        {
            var ctx = RunContext.Parse(key);
            if (!filter.Matches(ctx)) continue;

            if (!result.TryGetValue(ctx.Act, out var agg))
            {
                agg = new EncounterEvent();
                result[ctx.Act] = agg;
            }

            agg.Fought           += stat.Fought;
            agg.Died             += stat.Died;
            agg.WonRun           += stat.WonRun;
            agg.TurnsTakenSum    += stat.TurnsTakenSum;
            agg.DamageTakenSum   += stat.DamageTakenSum;
            agg.DamageTakenSqSum += stat.DamageTakenSqSum;
            agg.HpEnteringSum    += stat.HpEnteringSum;
            agg.MaxHpSum         += stat.MaxHpSum;
            agg.PotionsUsedSum   += stat.PotionsUsedSum;
            agg.DmgPctSum        += stat.DmgPctSum;
            agg.DmgPctSqSum      += stat.DmgPctSqSum;
        }

        return result;
    }

    /// <summary>
    /// Aggregates an encounter's per-context stats into per-character totals using the given filter.
    /// Returns a dictionary keyed by character ID (e.g. "CHARACTER.IRONCLAD").
    /// </summary>
    public static Dictionary<string, EncounterEvent> AggregateEncountersByCharacter(
        Dictionary<string, EncounterEvent> contextMap,
        AggregationFilter filter)
    {
        // Use a filter copy with no character constraint so we get all characters
        var openFilter = new AggregationFilter
        {
            GameMode = filter.GameMode,
            AscensionMin = filter.AscensionMin,
            AscensionMax = filter.AscensionMax,
            VersionMin = filter.VersionMin,
            VersionMax = filter.VersionMax,
            Profile = filter.Profile,
        };

        var result = new Dictionary<string, EncounterEvent>();

        foreach (var (key, stat) in contextMap)
        {
            var ctx = RunContext.Parse(key);
            if (!openFilter.Matches(ctx)) continue;

            if (!result.TryGetValue(ctx.Character, out var agg))
            {
                agg = new EncounterEvent();
                result[ctx.Character] = agg;
            }

            agg.Fought           += stat.Fought;
            agg.Died             += stat.Died;
            agg.WonRun           += stat.WonRun;
            agg.TurnsTakenSum    += stat.TurnsTakenSum;
            agg.DamageTakenSum   += stat.DamageTakenSum;
            agg.DamageTakenSqSum += stat.DamageTakenSqSum;
            agg.HpEnteringSum    += stat.HpEnteringSum;
            agg.MaxHpSum         += stat.MaxHpSum;
            agg.PotionsUsedSum   += stat.PotionsUsedSum;
            agg.DmgPctSum        += stat.DmgPctSum;
            agg.DmgPctSqSum      += stat.DmgPctSqSum;
        }

        return result;
    }

    /// <summary>
    /// Computes average Dmg% across all encounters matching the filter in a given category and act.
    /// Used as baseline for coloring encounter stats. Falls back to 20.0 if no data.
    /// </summary>
    public static double GetEncounterDmgPctBaseline(StatsDb db, AggregationFilter filter, string? category = null, int? act = null)
    {
        double totalDmgPct = 0;
        int totalFought = 0;

        foreach (var (encId, contextMap) in db.Encounters)
        {
            if (category != null && db.EncounterMeta.TryGetValue(encId, out var meta) && meta.Category != category)
                continue;

            foreach (var (key, stat) in contextMap)
            {
                var ctx = RunContext.Parse(key);
                if (!filter.Matches(ctx)) continue;
                if (act != null && ctx.Act != act) continue;

                totalFought += stat.Fought;
                totalDmgPct += stat.DmgPctSum;
            }
        }

        return totalFought == 0 ? 20.0 : totalDmgPct / totalFought * 100.0;
    }

    /// <summary>
    /// Computes average death rate across all encounters matching the filter in a given category and act.
    /// Falls back to 10.0 if no data.
    /// </summary>
    public static double GetEncounterDeathRateBaseline(StatsDb db, AggregationFilter filter, string? category = null, int? act = null)
    {
        int totalFought = 0, totalDied = 0;

        foreach (var (encId, contextMap) in db.Encounters)
        {
            if (category != null && db.EncounterMeta.TryGetValue(encId, out var meta) && meta.Category != category)
                continue;

            foreach (var (key, stat) in contextMap)
            {
                var ctx = RunContext.Parse(key);
                if (!filter.Matches(ctx)) continue;
                if (act != null && ctx.Act != act) continue;

                totalFought += stat.Fought;
                totalDied += stat.Died;
            }
        }

        return totalFought == 0 ? 10.0 : 100.0 * totalDied / totalFought;
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

    /// <summary>
    /// Collects all distinct build versions found across all card/relic context keys.
    /// Sorted semantically (oldest first).
    /// </summary>
    public static List<string> GetDistinctVersions(StatsDb db)
    {
        var versions = new HashSet<string>();
        foreach (var contextMap in db.Cards.Values)
            foreach (var key in contextMap.Keys)
                versions.Add(RunContext.Parse(key).BuildVersion);
        foreach (var contextMap in db.Relics.Values)
            foreach (var key in contextMap.Keys)
                versions.Add(RunContext.Parse(key).BuildVersion);
        var sorted = versions.ToList();
        sorted.Sort(AggregationFilter.CompareVersions);
        return sorted;
    }

    /// <summary>
    /// Collects all distinct character IDs found in the Characters table.
    /// </summary>
    public static List<string> GetDistinctCharacters(StatsDb db)
    {
        var characters = new HashSet<string>();
        foreach (var key in db.Characters.Keys)
        {
            var pipeIdx = key.IndexOf('|');
            if (pipeIdx > 0) characters.Add(key[..pipeIdx]);
        }
        var sorted = characters.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    /// <summary>
    /// Collects all distinct ascension levels found across all card/relic context keys.
    /// Returned sorted ascending. May include negative values from mods that add lower
    /// ascensions, and values above 10 from mods that extend the cap.
    /// </summary>
    public static List<int> GetDistinctAscensions(StatsDb db)
    {
        var ascensions = new HashSet<int>();
        foreach (var contextMap in db.Cards.Values)
            foreach (var key in contextMap.Keys)
                ascensions.Add(RunContext.Parse(key).Ascension);
        foreach (var contextMap in db.Relics.Values)
            foreach (var key in contextMap.Keys)
                ascensions.Add(RunContext.Parse(key).Ascension);
        var sorted = ascensions.ToList();
        sorted.Sort();
        return sorted;
    }

    /// <summary>
    /// Collects all distinct profile names found across all card/relic context keys.
    /// </summary>
    public static List<string> GetDistinctProfiles(StatsDb db)
    {
        var profiles = new HashSet<string>();
        foreach (var contextMap in db.Cards.Values)
            foreach (var key in contextMap.Keys)
                profiles.Add(RunContext.Parse(key).Profile);
        foreach (var contextMap in db.Relics.Values)
            foreach (var key in contextMap.Keys)
                profiles.Add(RunContext.Parse(key).Profile);
        var sorted = profiles.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }
}
