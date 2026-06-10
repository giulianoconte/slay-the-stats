using System;
using System.Globalization;
using SlayTheStats.Community;

namespace SlayTheStats;

/// <summary>
/// Formats the community "second baseline-like" reference row (Area 5) for a
/// tooltip surface. Reads the live <see cref="CommunityStats.Current"/> snapshot
/// through the Godot-free <see cref="CommunityAdapter"/> and renders a single
/// labeled line via <see cref="TooltipHelper.FormatBaselineLine"/> — mirroring the
/// local "(baseline)" line directly below the table.
///
/// Returns <c>null</c> to OMIT the row: community Off, no cached data, or no
/// community figure for this entity (Area 5 empty/disabled states). The row is a
/// reference line, so it's shown neutral — no significance coloration (the entity
/// isn't a sample to be judged against itself).
/// </summary>
internal static class CommunityTooltip
{
    private static bool Enabled => SlayTheStatsConfig.Community != SlayTheStatsConfig.CommunityMode.Off;

    /// <summary>Card community row: "(community {cohort})  Pick% {p}  Win% {w}". Null to omit.</summary>
    internal static string? CardRow(string rawCardId, bool upgraded)
    {
        if (!Enabled) return null;
        var (cohort, label) = Cohort();
        if (CommunityAdapter.GetEntityMetric(CommunityStats.Current, "cards", rawCardId, upgraded, cohort) is not { } m)
            return null;
        var line = L.T("tooltip.community.pick", ("cohort", label), ("pick", Pct(m.PickRate)), ("win", Pct(m.WinRate)));
        return TooltipHelper.FormatBaselineLine(line + AsOfSuffix());
    }

    /// <summary>Relic community row: "(community {cohort})  Win% {w}". Community has no shop-buy
    /// figure, so the relic row carries win-rate only. Null to omit.</summary>
    internal static string? RelicRow(string relicId)
    {
        if (!Enabled) return null;
        var (cohort, label) = Cohort();
        if (CommunityAdapter.GetEntityMetric(CommunityStats.Current, "relics", relicId, upgraded: false, cohort) is not { } m)
            return null;
        var line = L.T("tooltip.community.win", ("cohort", label), ("win", Pct(m.WinRate)));
        return TooltipHelper.FormatBaselineLine(line + AsOfSuffix());
    }

    private static (string cohort, string label) Cohort() =>
        SlayTheStatsConfig.CommunityReferenceCohort == SlayTheStatsConfig.CommunityCohort.A10
            ? (CommunityAdapter.CohortA10, L.T("community.cohort.a10"))
            : (CommunityAdapter.CohortAll, L.T("community.cohort.all"));

    // API rates are already in percent (e.g. 33.5 → "34%").
    private static string Pct(double? v) => v is { } d ? $"{Math.Round(d):F0}%" : "—";

    // Append "(as of <date>)" only when the snapshot is stale (>24h); fresh data stays uncluttered.
    private static string AsOfSuffix()
    {
        var c = CommunityStats.Current;
        if (c.FetchedUtc is not { } f || !c.IsStale(DateTimeOffset.UtcNow)) return "";
        return L.T("tooltip.community.asof", ("date", f.ToString("MMM d", CultureInfo.InvariantCulture)));
    }
}
