using System.Text.Json.Serialization;

namespace SlayTheStats;

/// <summary>
/// Pre-aggregated encounter stats per [encounterId][contextKey].
/// Stores sums for computing averages and sum-of-squares for variance.
/// </summary>
public class EncounterEvent
{
    [JsonPropertyName("fought")]             public int Fought           { get; set; }
    [JsonPropertyName("died")]               public int Died             { get; set; }
    [JsonPropertyName("won_run")]            public int WonRun           { get; set; }
    [JsonPropertyName("turns_taken_sum")]    public int TurnsTakenSum    { get; set; }
    [JsonPropertyName("damage_taken_sum")]   public int DamageTakenSum   { get; set; }
    [JsonPropertyName("damage_taken_sq_sum")]public int DamageTakenSqSum { get; set; }
    [JsonPropertyName("hp_entering_sum")]    public int HpEnteringSum    { get; set; }
    [JsonPropertyName("max_hp_sum")]         public int MaxHpSum         { get; set; }
    [JsonPropertyName("potions_used_sum")]   public int PotionsUsedSum   { get; set; }
    [JsonPropertyName("dmg_pct_sum")]        public double DmgPctSum     { get; set; }
    [JsonPropertyName("dmg_pct_sq_sum")]     public double DmgPctSqSum   { get; set; }

    /// <summary>
    /// Per-fight absolute damage values, appended during run parsing. Used
    /// to compute median and percentiles (p25/p75) at display time — the
    /// sum/sq-sum fields above only give mean/variance. Persisted to the
    /// JSON db; typically ~4 bytes × fights-per-context entries. Null-safe:
    /// old serialised dbs that predate this field deserialise as null, and
    /// display code falls back to the mean when the list is null/empty.
    /// </summary>
    [JsonPropertyName("damage_values")]
    public List<int>? DamageValues { get; set; }

    [JsonPropertyName("turns_values")]
    public List<int>? TurnsValues { get; set; }

    [JsonPropertyName("potions_values")]
    public List<int>? PotionsValues { get; set; }

    public double? DamageMedian() => MedianOf(DamageValues);

    public (double p25, double p75)? DamageIQR()
    {
        if (DamageValues == null || DamageValues.Count < 2) return null;
        var sorted = DamageValues.OrderBy(v => v).ToList();
        return (Percentile(sorted, 0.25), Percentile(sorted, 0.75));
    }

    /// <summary>
    /// Returns the percentile rank of <paramref name="value"/> within the
    /// given per-fight list. Result is 0–100 where 50 = median. Uses the
    /// "percentage of values strictly less than" formula: rank = count(v &lt; value) / n × 100.
    /// Returns null if the list is null or empty.
    /// </summary>
    public static double? PercentileRank(List<int>? values, int value)
    {
        if (values == null || values.Count == 0) return null;
        int below = 0;
        int equal = 0;
        foreach (var v in values)
        {
            if (v < value) below++;
            else if (v == value) equal++;
        }
        return (below + equal * 0.5) / values.Count * 100.0;
    }

    private static double? MedianOf(List<int>? values)
    {
        if (values == null || values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static double Percentile(List<int> sorted, double p)
    {
        double rank = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        double frac = rank - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }
}

/// <summary>
/// Encounter-weighted aggregate of a pool of encounters. Each metric is the average
/// of per-encounter values, so every encounter type contributes equally regardless of
/// how often the player has fought it. Total Fought and Died are still summed across
/// encounters (for the N column and Deaths cell display). Computed by
/// <see cref="StatsAggregator.AggregateEncounterPoolWeighted"/>.
/// </summary>
public struct PoolMetrics
{
    public int Fought;
    public int Died;
    public double Median;
    public (double p25, double p75)? IQR;
    public double AvgTurns;
    public double AvgPots;
    public double AvgDmgPct;  // 0–100, used for damage-cell color baseline
    public double DeathRate;  // 0–100, used for deaths-cell color baseline
    public double Iqrc;       // (p75-p25)/median, used for mid-50% color baseline
}

/// <summary>
/// Metadata about an encounter type, keyed by encounter ID (not per-context).
/// Populated on first occurrence during run parsing.
/// </summary>
public class EncounterMeta
{
    [JsonPropertyName("monster_ids")] public List<string> MonsterIds { get; set; } = new();
    [JsonPropertyName("category")]    public string Category         { get; set; } = "unknown";
    [JsonPropertyName("biome")]       public string Biome            { get; set; } = "";
    [JsonPropertyName("act")]         public int Act                 { get; set; }
}

public static class EncounterCategory
{
    private static readonly (string suffix, string category)[] Suffixes =
    {
        ("_EVENT_ENCOUNTER", "event"),
        ("_BOSS", "boss"),
        ("_ELITE", "elite"),
        ("_NORMAL", "normal"),
        ("_WEAK", "weak"),
    };

    // Encounter ids that don't follow the suffix convention but we know belong to a specific
    // category. Treat these as game-data inconsistencies.
    private static readonly Dictionary<string, string> CategoryOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENCOUNTER.OVERGROWTH_CRAWLERS"] = "normal",
    };

    /// <summary>
    /// Derives encounter category from the model_id suffix.
    /// Checks longest suffixes first to avoid partial matches.
    /// </summary>
    public static string Derive(string modelId)
    {
        if (CategoryOverrides.TryGetValue(modelId, out var overrideCat))
            return overrideCat;

        var name = modelId.StartsWith("ENCOUNTER.") ? modelId["ENCOUNTER.".Length..] : modelId;
        foreach (var (suffix, category) in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return category;
        }
        return "unknown";
    }

    /// <summary>
    /// Strips ENCOUNTER. prefix and category suffix, then title-cases with spaces.
    /// e.g. "ENCOUNTER.LAGAVULIN_MATRIARCH_BOSS" -> "Lagavulin Matriarch"
    /// </summary>
    public static string FormatName(string encounterId)
    {
        var name = encounterId.StartsWith("ENCOUNTER.") ? encounterId["ENCOUNTER.".Length..] : encounterId;

        foreach (var (suffix, _) in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        // Title-case with spaces: LAGAVULIN_MATRIARCH -> Lagavulin Matriarch
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpper(words[i][0]) + words[i][1..].ToLower();
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// Localized display label for an encounter category (our mod's own
    /// taxonomy: weak / normal / elite / boss / event). Keyed as
    /// <c>category.{lower}</c> in the loc table; unknown categories fall
    /// back to the title-cased input so modded encounter categories render
    /// readable-ish until someone adds a translation.
    /// </summary>
    public static string FormatCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return L.T("category.unknown");
        var lower = category.ToLowerInvariant();
        // L.T returns the key itself on miss; detect that and fall through
        // to the title-cased fallback so modded categories don't render as
        // the literal key. Also why we don't just return L.T unconditionally.
        var key = "category." + lower;
        var translated = L.T(key);
        if (translated != key) return translated;
        return char.ToUpper(category[0]) + category[1..].ToLower();
    }
}
