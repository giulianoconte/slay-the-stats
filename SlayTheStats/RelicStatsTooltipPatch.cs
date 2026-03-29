using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace SlayTheStats;

/// <summary>
/// Shared show/hide logic for both relic holder types.
/// NRelicBasicHolder and NRelicInventoryHolder both override OnFocus/OnUnfocus
/// directly (no CreateHoverTips abstraction), so each gets its own patch class
/// that delegates here.
/// </summary>
internal static class RelicHoverHelper
{
    private static bool    _warnedOnce;
    private static object? _activeHolder;

    internal static bool IsActiveHover() => _activeHolder != null;

    /// <summary>
    /// True only for NRelicBasicHolder / NRelicInventoryHolder, which use SetAlignmentForRelic
    /// and position the card container BELOW the text container.
    /// False for NRelicCollectionEntry / NMerchantRelic, which use SetAlignment(Left|Right)
    /// and position the card container BESIDE the text container at the same Y.
    /// Used by SlayTheStatsPositionFollower to decide whether to push the card container down.
    /// </summary>
    internal static bool ShouldPushCardContainer;

    internal static void Show(object holder)
    {
        ShowCore(holder, GetRelicId(holder));
        ShouldPushCardContainer = _activeHolder == holder;
        HideIfNotActive(holder);
    }

    internal static void ShowMerchant(object holder)
    {
        ShowCore(holder, GetRelicIdFromMerchant(holder));
        ShouldPushCardContainer = false;
        HideIfNotActive(holder);
    }

    internal static void ShowCollection(object holder)
    {
        ShowCore(holder, GetRelicIdFromCollection(holder));
        ShouldPushCardContainer = false;
        HideIfNotActive(holder);
    }

    /// <summary>
    /// If ShowCore returned early (no stats for this relic), immediately hide the panel
    /// so that a previous relic's stale stats don't bleed through during the hide delay.
    /// </summary>
    private static void HideIfNotActive(object holder)
    {
        if (_activeHolder != holder)
        {
            var panel = TooltipHelper.GetPanelPublic();
            if (panel != null) panel.Visible = false;
        }
    }

    private static void ShowCore(object holder, string? rawId)
    {
        try
        {
            TooltipHelper.EnsurePanelExists();

            if (rawId == null) return;

            var lookupId = MainFile.Db.Relics.ContainsKey("RELIC." + rawId) ? "RELIC." + rawId
                         : MainFile.Db.Relics.ContainsKey(rawId)             ? rawId
                         : null;

            string statsText;
            if (lookupId == null)
            {
                statsText = "No data";
            }
            else
            {
                var actStats = StatsAggregator.AggregateRelicsByAct(
                    MainFile.Db.Relics[lookupId], character: null, gameMode: "standard");
                statsText = actStats.Count == 0 ? "No data" : BuildStatsText(actStats);
            }

            TooltipHelper.TrySceneTheftOnce();
            _activeHolder = holder;
            TooltipHelper.ShowPanel(statsText);
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

    internal static void Hide(object source)
    {
        if (source != _activeHolder) return;

        _activeHolder = null;
        ShouldPushCardContainer = false;
        TooltipHelper.HasActiveHover = false;
        TooltipHelper.HideWithDelay();
    }

    /// <summary>
    /// Walks holder.Relic (NRelic) → .Model (RelicModel) → .Id → .Entry.
    /// Both NRelicBasicHolder and NRelicInventoryHolder expose a public Relic property.
    /// </summary>
    private static string? GetRelicId(object holder)
    {
        var relic = AccessTools.Property(holder.GetType(), "Relic")?.GetValue(holder);
        if (relic == null) return null;
        var model = AccessTools.Property(relic.GetType(), "Model")?.GetValue(relic);
        if (model == null) return null;
        var id = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
        if (id == null) return null;
        return AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string
            ?? id.ToString();
    }

    /// <summary>
    /// Walks holder._relicNode (NRelic, private field) → .Model → .Id → .Entry.
    /// NMerchantRelic stores the relic node in a private field rather than a public property.
    /// </summary>
    private static string? GetRelicIdFromMerchant(object holder)
    {
        var relicNode = AccessTools.Field(holder.GetType(), "_relicNode")?.GetValue(holder);
        if (relicNode == null) return null;
        var model = AccessTools.Property(relicNode.GetType(), "Model")?.GetValue(relicNode);
        if (model == null) return null;
        var id = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
        if (id == null) return null;
        return AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string
            ?? id.ToString();
    }

    /// <summary>
    /// Walks holder.relic (RelicModel, public field) → .Id → .Entry.
    /// NRelicCollectionEntry exposes the model directly as a field (no NRelic wrapper).
    /// </summary>
    private static string? GetRelicIdFromCollection(object holder)
    {
        var model = AccessTools.Field(holder.GetType(), "relic")?.GetValue(holder);
        if (model == null) return null;
        var id = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
        if (id == null) return null;
        return AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string
            ?? id.ToString();
    }

    private static string BuildStatsText(Dictionary<int, RelicStat> actStats)
    {
        var sb = new StringBuilder();
        sb.Append("Act Runs  Win%\n");
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

[HarmonyPatch(typeof(NRelicBasicHolder), "OnFocus")]
public static class RelicBasicHolderFocusPatch
{
    static void Postfix(NRelicBasicHolder __instance) => RelicHoverHelper.Show(__instance);
}

[HarmonyPatch(typeof(NRelicBasicHolder), "OnUnfocus")]
public static class RelicBasicHolderUnfocusPatch
{
    static void Postfix(NRelicBasicHolder __instance) => RelicHoverHelper.Hide(__instance);
}

[HarmonyPatch(typeof(NRelicInventoryHolder), "OnFocus")]
public static class RelicInventoryHolderFocusPatch
{
    static void Postfix(NRelicInventoryHolder __instance) => RelicHoverHelper.Show(__instance);
}

[HarmonyPatch(typeof(NRelicInventoryHolder), "OnUnfocus")]
public static class RelicInventoryHolderUnfocusPatch
{
    static void Postfix(NRelicInventoryHolder __instance) => RelicHoverHelper.Hide(__instance);
}

/// <summary>
/// NMerchantRelic extends NMerchantSlot (not NRelicBasicHolder) and uses
/// CreateHoverTip/ClearHoverTip rather than OnFocus/OnUnfocus directly.
/// ClearHoverTip is not overridden on NMerchantRelic so we patch the base class
/// and guard on the instance type.
/// </summary>
[HarmonyPatch(typeof(NMerchantRelic), "CreateHoverTip")]
public static class MerchantRelicCreateHoverTipPatch
{
    static void Postfix(NMerchantRelic __instance) => RelicHoverHelper.ShowMerchant(__instance);
}

[HarmonyPatch(typeof(NMerchantSlot), "ClearHoverTip")]
public static class MerchantSlotClearHoverTipPatch
{
    static void Postfix(NMerchantSlot __instance)
    {
        if (__instance is NMerchantRelic merchantRelic)
            RelicHoverHelper.Hide(merchantRelic);
    }
}

/// <summary>
/// NRelicCollectionEntry is used by the compendium (main-menu relic collection screen).
/// Only show stats for fully-visible relics; locked/unseen entries show a mystery tooltip
/// and should not expose identity through stats.
/// </summary>
[HarmonyPatch(typeof(NRelicCollectionEntry), "OnFocus")]
public static class RelicCollectionEntryFocusPatch
{
    static void Postfix(NRelicCollectionEntry __instance)
    {
        if (__instance.ModelVisibility != ModelVisibility.Visible) return;
        RelicHoverHelper.ShowCollection(__instance);
    }
}

[HarmonyPatch(typeof(NRelicCollectionEntry), "OnUnfocus")]
public static class RelicCollectionEntryUnfocusPatch
{
    static void Postfix(NRelicCollectionEntry __instance) => RelicHoverHelper.Hide(__instance);
}
