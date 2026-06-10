using System;
using System.Collections.Generic;

namespace SlayTheStats.Community;

/// <summary>One entity's community figures — the objective, methodology-neutral
/// fields (Area 4). Rates are nullable: the API returns null where a metric is
/// undefined (e.g. a potion's pick_rate).</summary>
public readonly record struct CommunityMetric(double? WinRate, double? PickRate, int Picks, int Wins);

/// <summary>One encounter's community figures, summed across acts for a given
/// encounter id. DeathRate = Fatal/Total (0..1).</summary>
public readonly record struct CommunityEncounterMetric(int Total, int Fatal, double? AvgDamage, double? AvgTurns)
{
    public double? DeathRate => Total > 0 ? (double)Fatal / Total : null;
}

/// <summary>
/// The thin read adapter (Area 4): looks up a single entity's community figures
/// from a <see cref="CommunityCache"/>. Not a pipeline — the API already hands us
/// aggregates. Filters to official (game) ids, normalizes id prefixes, and exposes
/// only the objective fields; score/tier/elo are deliberately ignored.
///
/// Godot-free and unit-tested; the tooltip layer supplies <see cref="CommunityStats.Current"/>
/// and formats the returned figures into a row.
/// </summary>
public static class CommunityAdapter
{
    public const string CohortAll = "all";
    public const string CohortA10 = "a10";

    /// <summary>True for an official game entity id (UPPER_SNAKE, ASCII). Modded/namespaced
    /// ids are dropped so a community row never appears for a non-game entity.</summary>
    public static bool IsOfficialId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        foreach (var c in id)
            if (!(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_') return false;
        return true;
    }

    /// <summary>Strips a leading "CARD."/"RELIC."/"POTION." namespace and upper-cases,
    /// so a caller can pass either the prefixed db key or the bare game id.</summary>
    public static string NormalizeId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        int dot = id.IndexOf('.');
        var bare = dot >= 0 ? id[(dot + 1)..] : id;
        return bare.ToUpperInvariant();
    }

    private static MetricsResponse? MetricsFor(CommunityCache cache, string entityType, string cohort)
    {
        if (!cache.Cohorts.TryGetValue(cohort, out var c) || c == null) return null;
        return entityType switch
        {
            "cards"   => c.Cards,
            "relics"  => c.Relics,
            "potions" => c.Potions,
            _ => null,
        };
    }

    /// <summary>
    /// Community figures for one card/relic/potion in the given cohort, or null when
    /// there's no cached data, the id is non-official, or the entity isn't in the table.
    /// <paramref name="id"/> may be bare or prefixed. <paramref name="upgraded"/> picks
    /// the variant, falling back to the other if only one side is present.
    /// </summary>
    public static CommunityMetric? GetEntityMetric(CommunityCache cache, string entityType, string id, bool upgraded, string cohort)
    {
        var bare = NormalizeId(id);
        if (!IsOfficialId(bare)) return null;

        var metrics = MetricsFor(cache, entityType, cohort);
        if (metrics == null) return null;

        MetricRow? exact = null, fallback = null;
        foreach (var row in metrics.Rows)
        {
            if (!string.Equals(NormalizeId(row.Id), bare, StringComparison.Ordinal)) continue;
            if (row.Upgraded == upgraded) { exact = row; break; }
            fallback ??= row;
        }
        var hit = exact ?? fallback;
        return hit == null ? null : new CommunityMetric(hit.WinRate, hit.PickRate, hit.Picks, hit.Wins);
    }

    /// <summary>
    /// Community figures for one encounter, summed across acts (an encounter id is
    /// usually single-act anyway). Damage/turns are run-count-weighted means over the
    /// contributing act rows. Null when no cached data or the encounter isn't present.
    /// </summary>
    public static CommunityEncounterMetric? GetEncounterMetric(CommunityCache cache, string encounterId)
    {
        var bare = NormalizeId(encounterId);
        if (!IsOfficialId(bare)) return null;
        if (cache.Encounters.Count == 0) return null;

        int total = 0, fatal = 0;
        double dmgWeighted = 0, turnsWeighted = 0;
        int dmgW = 0, turnsW = 0;
        bool found = false;

        foreach (var row in cache.Encounters)
        {
            if (!string.Equals(NormalizeId(row.EncounterId), bare, StringComparison.Ordinal)) continue;
            found = true;
            total += row.Total;
            fatal += row.Fatal;
            if (row.AvgDamage is { } d) { dmgWeighted += d * row.Total; dmgW += row.Total; }
            if (row.AvgTurns is { } t)  { turnsWeighted += t * row.Total; turnsW += row.Total; }
        }
        if (!found) return null;

        double? avgDmg = dmgW > 0 ? dmgWeighted / dmgW : null;
        double? avgTurns = turnsW > 0 ? turnsWeighted / turnsW : null;
        return new CommunityEncounterMetric(total, fatal, avgDmg, avgTurns);
    }
}
