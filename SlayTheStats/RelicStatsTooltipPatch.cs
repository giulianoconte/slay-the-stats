using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace SlayTheStats;

/// <summary>
/// Shows a relic stats tooltip when a relic is hovered in any context
/// (in-run top bar, reward screen, shop, compendium).
/// Patches NRelicBasicHolder, which is the base for all relic holder nodes.
/// </summary>
[HarmonyPatch(typeof(NRelicBasicHolder), "CreateHoverTips")]
public static class RelicHoverShowPatch
{
    private static bool               _warnedOnce;
    private static NRelicBasicHolder? _activeHolder;

    internal static bool IsActiveHover() => _activeHolder != null;

    static void Postfix(NRelicBasicHolder __instance)
    {
        try
        {
            TooltipHelper.EnsurePanelExists();

            var rawId = GetRelicId(__instance);
            if (rawId == null) return;

            var lookupId = MainFile.Db.Relics.ContainsKey("RELIC." + rawId) ? "RELIC." + rawId
                         : MainFile.Db.Relics.ContainsKey(rawId)             ? rawId
                         : null;

            if (lookupId == null) return;

            var contextMap = MainFile.Db.Relics[lookupId];
            var actStats = StatsAggregator.AggregateRelicsByAct(contextMap, character: null, gameMode: "standard");
            if (actStats.Count == 0) return;

            TooltipHelper.ConnectFontTheft();
            _activeHolder = __instance;
            TooltipHelper.ShowPanel("SlayTheStats", BuildStatsText(actStats));
            MainFile.Logger.Info($"[SlayTheStats] ShowTooltip(relic): anchor={__instance.GetHashCode()} id={lookupId}");
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"SlayTheStats: relic tooltip unavailable — {e.Message}");
                _warnedOnce = true;
            }
        }
    }

    internal static void HideTooltip(NRelicBasicHolder? source = null)
    {
        var skip = source != null && source != _activeHolder;
        MainFile.Logger.Info($"[SlayTheStats] HideTooltip(relic): source={source?.GetHashCode()} active={_activeHolder?.GetHashCode()} skip={skip}");
        if (skip) return;

        _activeHolder = null;
        TooltipHelper.HasActiveHover = false;
        TooltipHelper.HideWithDelay();
    }

    /// <summary>
    /// Walks NRelicBasicHolder → NRelic (_relic field) → RelicModel (_model field) → Id.Entry.
    /// </summary>
    private static string? GetRelicId(NRelicBasicHolder holder)
    {
        try
        {
            var relicNode = AccessTools.Field(typeof(NRelicBasicHolder), "_relic")?.GetValue(holder)
                         ?? AccessTools.Property(typeof(NRelicBasicHolder), "Relic")?.GetValue(holder);
            if (relicNode == null) return null;

            var model = AccessTools.Field(relicNode.GetType(), "_model")?.GetValue(relicNode);
            if (model == null) return null;

            var id = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
            if (id == null) return null;

            return AccessTools.Field(id.GetType(), "Entry")?.GetValue(id) as string
                ?? AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string
                ?? id.ToString();
        }
        catch { return null; }
    }

    private static string BuildStatsText(Dictionary<int, RelicStat> actStats)
    {
        var sb = new StringBuilder();
        sb.Append("Act    N   WR%\n");
        for (int act = 1; act <= 3; act++)
        {
            if (actStats.TryGetValue(act, out var stat) && stat.RunsPresent > 0)
            {
                var wr = $"{Math.Round(100.0 * stat.RunsWon / stat.RunsPresent):F0}%";
                sb.Append($"{act,3}  {stat.RunsPresent,3}  {wr,4}\n");
            }
            else
            {
                sb.Append($"{act,3}    -     -\n");
            }
        }
        return sb.ToString().TrimEnd();
    }
}

[HarmonyPatch(typeof(NRelicBasicHolder), "ClearHoverTips")]
public static class RelicHoverHidePatch
{
    static void Postfix(NRelicBasicHolder __instance)
    {
        try { RelicHoverShowPatch.HideTooltip(__instance); }
        catch { }
    }
}
