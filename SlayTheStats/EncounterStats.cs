using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Localization;

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
/// </summary>
/// <remarks>
/// The roster is <em>one sampled observation</em> — the monsters present in a single
/// recorded fight — not the union of every variant. An encounter that can roll
/// (mA,mB) or (mA,mC) is shown as whichever of those we last saw, because that's a
/// set the player can actually meet; the union (mA,mB,mC) is a lineup that never
/// occurs.
///
/// The sample is the <b>most recent</b> one, by the observing run's
/// <c>start_time</c> (<see cref="SampledAt"/>) — not the first seen. Rosters go stale:
/// the game changes an encounter's composition between versions, so a sample from an
/// old build can describe a fight that no longer exists. Most-recent-wins lets newer
/// runs overwrite that.
///
/// It is also what keeps player-side pets (Osty, Byrdpip, Pael's Legion) out of
/// rosters, without the mod ever having to know what a pet is. The save's per-room
/// <c>monster_ids</c> normally records the fight's <em>spawn</em> set, which excludes
/// pets and mid-fight summons alike; only builds around v0.107 wrote the cumulative
/// combatant list instead. So the fix is to distrust that data window
/// (<see cref="IsRosterTrustedBuild"/>) rather than to identify pets — which also
/// catches the same corruption's non-pet face, where a summon like the Fabricator's
/// Zapbot is recorded as an encounter member. No pet allowlist could have caught that
/// one, nor could any have covered mod-added pets.
/// </remarks>
public class EncounterMeta
{
    [JsonPropertyName("monster_ids")] public List<string> MonsterIds { get; set; } = new();
    [JsonPropertyName("category")]    public string Category         { get; set; } = "unknown";
    [JsonPropertyName("biome")]       public string Biome            { get; set; } = "";
    [JsonPropertyName("act")]         public int Act                 { get; set; }

    /// <summary>
    /// <c>start_time</c> of the run this roster was sampled from — the recency half of
    /// the comparison key. Runs are parsed in filesystem order and incrementally across
    /// sessions, so arrival order says nothing about recency; this does. Absent (0) on
    /// entries written before the field existed, which lets any dated observation
    /// replace them.
    /// </summary>
    [JsonPropertyName("sampled_at")]    public long SampledAt        { get; set; }

    /// <summary>
    /// <c>build_id</c> of that run, so <see cref="IsRosterTrustedBuild"/> can be
    /// re-evaluated later without re-reading the save.
    /// </summary>
    [JsonPropertyName("sampled_build")] public string SampledBuild   { get; set; } = "";

    /// <summary>
    /// Ranks a candidate sample: trusted builds beat untrusted ones, and within a tier
    /// the more recent run wins. Comparing this tuple is the whole sampling policy.
    /// </summary>
    public (int trust, long at) SampleRank() => (IsRosterTrustedBuild(SampledBuild) ? 1 : 0, SampledAt);

    /// <summary>
    /// Whether a game build recorded <c>monster_ids</c> the way we need it: the fight's
    /// <em>spawn</em> set. Builds in a window around v0.107 wrote the cumulative
    /// combatant list instead, which pulls in both player-side pets and mid-fight
    /// summons — so a roster sampled there lists monsters that were never part of the
    /// encounter. We prefer any sample from outside that window, however old.
    /// </summary>
    /// <remarks>
    /// Measured against 466 local runs, counting only runs that could produce a pet:
    /// v0.98.0–v0.105.0 leaked in 0 of ~1025 combat rooms, v0.106.1/v0.107.0/v0.107.1
    /// leaked in 22/22, 53/73 and 26/26, and v0.111.0 leaked in 0 of 18. So the window
    /// is confirmed open by v0.106.1, confirmed closed by v0.111.0.
    ///
    /// The bound below is deliberately wider than what was observed, covering v0.106.0
    /// and v0.108–v0.110 — builds absent from that history, so untested either way.
    /// Being too wide only means preferring an older sample that is known-good over a
    /// newer one that might not be; being too narrow means showing phantom enemies.
    /// It cost nothing on the measured corpus: all 88 encounters still had a trusted
    /// sample, so nothing fell back.
    ///
    /// This is transitional by construction. Every encounter refought on a current
    /// build resamples into the trusted tier, after which the window never applies
    /// again. An unparseable or missing build is treated as trusted — it's far more
    /// likely to be a future build than one of these five.
    /// </remarks>
    public static bool IsRosterTrustedBuild(string? buildId)
    {
        var v = ParseBuild(buildId);
        if (v == null) return true;
        var (major, minor, _) = v.Value;
        return !(major == 0 && minor >= 106 && minor <= 110);
    }

    /// <summary>Parses a <c>build_id</c> like "v0.107.1" into (0, 107, 1).</summary>
    private static (int, int, int)? ParseBuild(string? buildId)
    {
        if (string.IsNullOrWhiteSpace(buildId)) return null;
        var parts = buildId.TrimStart('v', 'V').Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return (major, minor, patch);
    }
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
    /// Localized encounter display name. Looks up <c>&lt;entry&gt;.title</c> in the
    /// game's <c>encounters</c> loc table (same source as the in-game combat UI),
    /// so names match whatever language the player has the game set to. Falls
    /// back to a title-cased derivation of the id when the key is missing
    /// (pre-LocManager call, modded encounters without loc entries, etc.).
    /// </summary>
    public static string FormatName(string encounterId)
    {
        var entry = encounterId.StartsWith("ENCOUNTER.") ? encounterId["ENCOUNTER.".Length..] : encounterId;
        try
        {
            var table = LocManager.Instance?.GetTable("encounters");
            var key = entry + ".title";
            if (table != null && table.HasEntry(key))
                return table.GetRawText(key);
        }
        catch (Exception)
        {
            // Fall through to title-cased fallback.
        }
        return FormatNameFallback(entry);
    }

    /// <summary>
    /// Strips the category suffix and title-cases the remainder.
    /// e.g. "LAGAVULIN_MATRIARCH_BOSS" → "Lagavulin Matriarch"
    /// </summary>
    private static string FormatNameFallback(string entry)
    {
        var name = entry;
        foreach (var (suffix, _) in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

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
    ///
    /// <para>We looked at routing <c>boss</c> through the game's own
    /// <c>static_hover_tips.BOSS.title</c> to drop the per-locale guessing,
    /// but that key is a template ("Boss: {BossName}") populated by
    /// <c>NTopBarBossIcon</c> with the specific boss's name — unusable as a
    /// standalone category label. No other uniform game-loc source was
    /// found. Per-locale hardcoding it is.</para>
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
