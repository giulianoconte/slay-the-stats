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

    /// <summary>
    /// Display-only raw config snapshot. Carries the sentinel strings
    /// ("__lowest__" / "__highest__") and the raw ascension bounds straight
    /// from SlayTheStatsConfig so the footer formatter can distinguish
    /// "Lowest sentinel" from "Highest sentinel" and "explicit 0/20" from
    /// "unbounded" when rendering. Set by the filter builders
    /// (BuildCompendiumFilter / BuildInRunFilter / BuildSafeFilter /
    /// BuildSafeFilterFromDefaults) so <see cref="CardHoverShowPatch.BuildFilterContext"/>
    /// can produce the full context line without needing parallel raw
    /// parameters on every call site. Does NOT affect matching behaviour
    /// at all — the Matches() path ignores this field.
    /// </summary>
    public FilterDisplayRaw Display = new FilterDisplayRaw();

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

/// <summary>
/// Raw config snapshot attached to an AggregationFilter purely for footer
/// rendering. The filter's primary fields (AscensionMin/Max, VersionMin/Max,
/// Profile) are post-sanitisation and lose sentinel info; these raw strings
/// preserve it.
/// </summary>
public struct FilterDisplayRaw
{
    /// <summary>Raw ascension lower bound — can be SlayTheStatsConfig.AscensionLowest
    /// (int.MinValue), AscensionHighest (int.MaxValue), or an explicit int in [0, 20].</summary>
    public int RawAscMin;
    /// <summary>Raw ascension upper bound — same sentinel scheme as RawAscMin.</summary>
    public int RawAscMax;
    /// <summary>Raw version lower bound — "__lowest__" / "__highest__" / "vX.Y.Z" / "".</summary>
    public string RawVerMin;
    /// <summary>Raw version upper bound — same scheme.</summary>
    public string RawVerMax;
    /// <summary>Raw profile — "" for all, profile name otherwise.</summary>
    public string RawProfile;
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
            if (stat.DamageValues is { Count: > 0 })
            {
                agg.DamageValues ??= new List<int>();
                agg.DamageValues.AddRange(stat.DamageValues);
            }
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
            if (stat.DamageValues is { Count: > 0 })
            {
                agg.DamageValues ??= new List<int>();
                agg.DamageValues.AddRange(stat.DamageValues);
            }
        }

        return result;
    }

    /// <summary>
    /// Encounter-weighted pool aggregation. Computes each matching encounter's per-fight
    /// statistics independently, then averages those per-encounter values across encounters
    /// so each encounter type contributes equally regardless of how often the player has
    /// fought it. Matches the "uniform spawn" assumption of STS2 encounter pools and the
    /// player's mental model of "how does this encounter compare to its peers". Total
    /// Fought and Died are still summed across encounters (for the N column and Deaths
    /// cell display).
    /// </summary>
    public static PoolMetrics AggregateEncounterPoolWeighted(
        StatsDb db, AggregationFilter filter, string? category = null, string? biome = null)
    {
        var perEncounter = new List<EncounterEvent>();

        foreach (var (encId, contextMap) in db.Encounters)
        {
            if (category != null && db.EncounterMeta.TryGetValue(encId, out var meta) && meta.Category != category)
                continue;
            if (!MatchesBiome(db, encId, biome)) continue;

            // Aggregate this single encounter's matching contexts into one EncounterEvent.
            var encEvent = new EncounterEvent();
            foreach (var (key, stat) in contextMap)
            {
                var ctx = RunContext.Parse(key);
                if (!filter.Matches(ctx)) continue;

                encEvent.Fought          += stat.Fought;
                encEvent.Died            += stat.Died;
                encEvent.TurnsTakenSum   += stat.TurnsTakenSum;
                encEvent.PotionsUsedSum  += stat.PotionsUsedSum;
                encEvent.DamageTakenSum  += stat.DamageTakenSum;
                encEvent.DmgPctSum       += stat.DmgPctSum;
                if (stat.DamageValues is { Count: > 0 })
                {
                    encEvent.DamageValues ??= new List<int>();
                    encEvent.DamageValues.AddRange(stat.DamageValues);
                }
            }

            if (encEvent.Fought > 0) perEncounter.Add(encEvent);
        }

        return AggregateMetricsFromEvents(perEncounter);
    }

    /// <summary>
    /// Generic encounter-weighted metrics aggregation. Each EncounterEvent in the input
    /// contributes one observation per metric: per-encounter median, p25, p75, avg turns,
    /// avg potions, dmg%, death rate, IQRC. Percentile-based metrics (median, p25, p75)
    /// aggregate via median-of-values; mean-based metrics aggregate via average.
    ///
    /// Used both for pool aggregations across multiple encounter types (each event = one
    /// encounter type) and for cross-character aggregations of a single encounter (each
    /// event = one character's stats for that encounter). The aggregation logic is
    /// identical — only the meaning of "one observation" changes.
    /// </summary>
    public static PoolMetrics AggregateMetricsFromEvents(IEnumerable<EncounterEvent> events)
    {
        int totalFought = 0, totalDied = 0;
        var medians      = new List<double>();
        var p25s         = new List<double>();
        var p75s         = new List<double>();
        var avgTurnsList = new List<double>();
        var avgPotsList  = new List<double>();
        var avgDmgPcts   = new List<double>();
        var deathRates   = new List<double>();
        var iqrcs        = new List<double>();

        foreach (var e in events)
        {
            if (e.Fought == 0) continue;

            totalFought += e.Fought;
            totalDied   += e.Died;

            var median = e.DamageMedian();
            if (median.HasValue) medians.Add(median.Value);

            var iqr = e.DamageIQR();
            if (iqr.HasValue)
            {
                p25s.Add(iqr.Value.p25);
                p75s.Add(iqr.Value.p75);
                if (median.HasValue && median.Value > 0)
                    iqrcs.Add((iqr.Value.p75 - iqr.Value.p25) / median.Value);
            }

            avgTurnsList.Add((double)e.TurnsTakenSum / e.Fought);
            avgPotsList .Add((double)e.PotionsUsedSum / e.Fought);
            avgDmgPcts  .Add(e.DmgPctSum / e.Fought * 100.0);
            deathRates  .Add(100.0 * e.Died / e.Fought);
        }

        return new PoolMetrics
        {
            Fought    = totalFought,
            Died      = totalDied,
            Median    = MedianOf(medians),
            IQR       = (p25s.Count > 0)
                          ? ((double p25, double p75)?)(MedianOf(p25s), MedianOf(p75s))
                          : null,
            AvgTurns  = avgTurnsList.Count > 0 ? avgTurnsList.Average() : 0,
            AvgPots   = avgPotsList .Count > 0 ? avgPotsList .Average() : 0,
            AvgDmgPct = avgDmgPcts  .Count > 0 ? avgDmgPcts  .Average() : 0,
            DeathRate = deathRates  .Count > 0 ? deathRates  .Average() : 0,
            Iqrc      = iqrcs       .Count > 0 ? iqrcs       .Average() : 1.0,
        };
    }

    /// <summary>Median of a list of doubles. Returns 0 for an empty list. For even-length
    /// lists, returns the average of the two middle values.</summary>
    private static double MedianOf(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    /// <summary>
    /// Aggregates all encounters matching a category and biome under the given filter
    /// into a single <see cref="EncounterEvent"/>. Used for pool-level context rows
    /// (e.g. "all elites in Hive for Defect"). Returns an event with Fought=0 if
    /// no matching data exists.
    /// </summary>
    public static EncounterEvent AggregateEncounterPool(
        StatsDb db, AggregationFilter filter, string? category = null, string? biome = null)
    {
        var result = new EncounterEvent();

        foreach (var (encId, contextMap) in db.Encounters)
        {
            if (category != null && db.EncounterMeta.TryGetValue(encId, out var meta) && meta.Category != category)
                continue;
            if (!MatchesBiome(db, encId, biome)) continue;

            foreach (var (key, stat) in contextMap)
            {
                var ctx = RunContext.Parse(key);
                if (!filter.Matches(ctx)) continue;

                result.Fought           += stat.Fought;
                result.Died             += stat.Died;
                result.WonRun           += stat.WonRun;
                result.TurnsTakenSum    += stat.TurnsTakenSum;
                result.DamageTakenSum   += stat.DamageTakenSum;
                result.DamageTakenSqSum += stat.DamageTakenSqSum;
                result.HpEnteringSum    += stat.HpEnteringSum;
                result.MaxHpSum         += stat.MaxHpSum;
                result.PotionsUsedSum   += stat.PotionsUsedSum;
                result.DmgPctSum        += stat.DmgPctSum;
                result.DmgPctSqSum      += stat.DmgPctSqSum;
                if (stat.DamageValues is { Count: > 0 })
                {
                    result.DamageValues ??= new List<int>();
                    result.DamageValues.AddRange(stat.DamageValues);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns true if the given encounter matches the biome key. Biome keys follow the
    /// bestiary's convention: null or "all:" = all encounters, "act:N" = encounters in
    /// that act, otherwise matches the encounter's biome string exactly.
    /// </summary>
    private static bool MatchesBiome(StatsDb db, string encId, string? biome)
    {
        if (string.IsNullOrEmpty(biome) || biome == "all:") return true;
        if (!db.EncounterMeta.TryGetValue(encId, out var meta)) return false;
        if (biome.StartsWith("act:") && int.TryParse(biome[4..], out var act))
            return meta.Act == act;
        return meta.Biome == biome;
    }

    /// <summary>
    /// Computes average Dmg% across all encounters matching the filter in a given
    /// category and biome. Used as baseline for coloring encounter stats.
    /// Falls back to 20.0 if no data.
    /// </summary>
    public static double GetEncounterDmgPctBaseline(StatsDb db, AggregationFilter filter, string? category = null, string? biome = null)
    {
        double totalDmgPct = 0;
        int totalFought = 0;

        foreach (var (encId, contextMap) in db.Encounters)
        {
            if (category != null && db.EncounterMeta.TryGetValue(encId, out var meta) && meta.Category != category)
                continue;
            if (!MatchesBiome(db, encId, biome)) continue;

            foreach (var (key, stat) in contextMap)
            {
                var ctx = RunContext.Parse(key);
                if (!filter.Matches(ctx)) continue;

                totalFought += stat.Fought;
                totalDmgPct += stat.DmgPctSum;
            }
        }

        return totalFought == 0 ? 20.0 : totalDmgPct / totalFought * 100.0;
    }

    /// <summary>
    /// Computes average absolute damage taken across all encounters matching the filter
    /// in a given category and biome. Used by the in-combat tooltip footer where the
    /// player has a single character and absolute damage is more meaningful than the
    /// %-of-max-HP variant. Falls back to 0 if no data.
    /// </summary>
    public static double GetEncounterDmgBaseline(StatsDb db, AggregationFilter filter, string? category = null, string? biome = null)
    {
        long totalDmg = 0;
        int totalFought = 0;

        foreach (var (encId, contextMap) in db.Encounters)
        {
            if (category != null && db.EncounterMeta.TryGetValue(encId, out var meta) && meta.Category != category)
                continue;
            if (!MatchesBiome(db, encId, biome)) continue;

            foreach (var (key, stat) in contextMap)
            {
                var ctx = RunContext.Parse(key);
                if (!filter.Matches(ctx)) continue;

                totalFought += stat.Fought;
                totalDmg += stat.DamageTakenSum;
            }
        }

        return totalFought == 0 ? 0.0 : (double)totalDmg / totalFought;
    }

    /// <summary>
    /// Computes average death rate across all encounters matching the filter in a given
    /// category and biome. Falls back to 10.0 if no data.
    /// </summary>
    public static double GetEncounterDeathRateBaseline(StatsDb db, AggregationFilter filter, string? category = null, string? biome = null)
    {
        int totalFought = 0, totalDied = 0;

        foreach (var (encId, contextMap) in db.Encounters)
        {
            if (category != null && db.EncounterMeta.TryGetValue(encId, out var meta) && meta.Category != category)
                continue;
            if (!MatchesBiome(db, encId, biome)) continue;

            foreach (var (key, stat) in contextMap)
            {
                var ctx = RunContext.Parse(key);
                if (!filter.Matches(ctx)) continue;

                totalFought += stat.Fought;
                totalDied += stat.Died;
            }
        }

        return totalFought == 0 ? 10.0 : 100.0 * totalDied / totalFought;
    }

    /// <summary>
    /// Computes the pooled IQR coefficient (IQRC = IQR / median) across all encounters
    /// matching the filter in a given category and biome. For each encounter, aggregates
    /// all DamageValues across matching contexts, computes that encounter's IQRC, then
    /// averages across encounters (weighted equally, skipping encounters with insufficient
    /// data for a meaningful IQR). Falls back to 1.0 if no qualifying encounters.
    /// </summary>
    public static double GetEncounterIqrcBaseline(StatsDb db, AggregationFilter filter, string? category = null, string? biome = null)
    {
        double totalIqrc = 0;
        int count = 0;

        foreach (var (encId, contextMap) in db.Encounters)
        {
            if (category != null && db.EncounterMeta.TryGetValue(encId, out var meta) && meta.Category != category)
                continue;
            if (!MatchesBiome(db, encId, biome)) continue;

            // Aggregate DamageValues across all matching contexts for this encounter
            List<int>? merged = null;
            foreach (var (key, stat) in contextMap)
            {
                var ctx = RunContext.Parse(key);
                if (!filter.Matches(ctx)) continue;
                if (stat.DamageValues is { Count: > 0 })
                {
                    merged ??= new List<int>();
                    merged.AddRange(stat.DamageValues);
                }
            }

            if (merged == null || merged.Count < 4) continue; // need enough data for meaningful IQR

            // Compute this encounter's IQRC
            var tempEvent = new EncounterEvent { DamageValues = merged };
            var median = tempEvent.DamageMedian();
            var iqr = tempEvent.DamageIQR();
            if (median == null || iqr == null || median.Value <= 0) continue;

            totalIqrc += (iqr.Value.p75 - iqr.Value.p25) / median.Value;
            count++;
        }

        return count == 0 ? 1.0 : totalIqrc / count;
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
