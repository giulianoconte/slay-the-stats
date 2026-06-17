using System;
using SlayTheStats.Community;

namespace SlayTheStats;

/// <summary>
/// Supplies the community reference figures (Area 5) for a tooltip surface. Reads the
/// live <see cref="CommunityStats.Current"/> snapshot through the Godot-free
/// <see cref="CommunityAdapter"/> and returns a structured <see cref="CommunityRef"/>
/// (cohort label + pick/win) that the card/relic builders render alongside the local
/// "(baseline)" row in the detached reference table below the stats (#37).
///
/// Returns <c>null</c> to OMIT the row: community Off, no cached data, or no
/// community figure for this entity (Area 5 empty/disabled states). The row is a
/// reference line, so it's shown neutral — no significance coloration (the entity
/// isn't a sample to be judged against itself).
/// </summary>
internal static class CommunityTooltip
{
    private static bool Enabled => SlayTheStatsConfig.Community != SlayTheStatsConfig.CommunityMode.Off;

    /// <summary>Structured community reference figures for the in-tooltip reference table
    /// (#37): a right-aligned cohort label, the pick value, and the win value (any may be
    /// absent). The pick/buys layout split is the caller's concern — the buys layout drops
    /// <see cref="Pick"/> (community has no buy-rate) and shows win only.</summary>
    internal readonly record struct CommunityRef(string Label, string? Pick, string? Win);

    /// <summary>Community card reference figures, or null to omit (Off / no cached data /
    /// no figure for this entity).</summary>
    internal static CommunityRef? CardRow(string rawCardId, bool upgraded)
    {
        if (!Enabled) return null;
        var (cohort, label) = Cohort();
        if (CommunityAdapter.GetEntityMetric(CommunityStats.Current, "cards", rawCardId, upgraded, cohort) is not { } m)
            return null;
        return new CommunityRef(CommunityLabel(label), Pct(m.PickRate), Pct(m.WinRate));
    }

    /// <summary>Community relic reference figures (win-rate only — community has no shop-buy
    /// figure), or null to omit.</summary>
    internal static CommunityRef? RelicRow(string relicId)
    {
        if (!Enabled) return null;
        var (cohort, label) = Cohort();
        if (CommunityAdapter.GetEntityMetric(CommunityStats.Current, "relics", relicId, upgraded: false, cohort) is not { } m)
            return null;
        return new CommunityRef(CommunityLabel(label), Pick: null, Pct(m.WinRate));
    }

    /// <summary>"(community)" — the right-aligned label cell of the reference row. The
    /// cohort still drives which data is shown (see <see cref="Cohort"/>), but is no longer
    /// surfaced in the label; the arg is kept for the template's optional <c>{cohort}</c>.</summary>
    private static string CommunityLabel(string cohortLabel)
        => L.T("tooltip.community.label", ("cohort", cohortLabel));

    /// <summary>Community encounter figures + descriptor for the bestiary row, or null to
    /// omit. Encounter stats aren't cohort-split in the API (one global set), so the row is
    /// always the full-community ("all") reference regardless of the card/relic cohort.</summary>
    internal static (CommunityEncounterMetric metric, string descriptor)? EncounterFigures(string encounterId)
    {
        if (!Enabled) return null;
        if (!CommunityStats.EncounterStatsEnabled) return null; // gated off until the endpoint is reliable (#40)
        if (CommunityAdapter.GetEncounterMetric(CommunityStats.Current, encounterId) is not { } m)
            return null;
        var descriptor = L.T("descriptor.community", ("cohort", L.T("community.cohort.all")));
        return (m, descriptor);
    }

    private static (string cohort, string label) Cohort() =>
        SlayTheStatsConfig.CommunityReferenceCohort == SlayTheStatsConfig.CommunityCohort.A10
            ? (CommunityAdapter.CohortA10, L.T("community.cohort.a10"))
            : (CommunityAdapter.CohortAll, L.T("community.cohort.all"));

    // API rates are already in percent (e.g. 33.5 → "34%").
    private static string Pct(double? v) => v is { } d ? $"{Math.Round(d):F0}%" : "—";
}
