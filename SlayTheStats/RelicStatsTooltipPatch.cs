using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

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

    internal static Control? ActiveHolderControl => _activeHolder as Control;

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
        if (!SlayTheStatsConfig.ShowInRunStats) return;

        MainFile.Logger.Info($"[SlayTheStats] RelicHover.Show: type={holder.GetType().Name} activeHolder={((object?)_activeHolder == null ? "null" : _activeHolder.GetType().Name)} hasActiveHover={TooltipHelper.HasActiveHover}");
        ShowCore(holder, GetRelicId(holder));
        ShouldPushCardContainer = _activeHolder == holder;
        HideIfNotActive(holder);
    }

    internal static void ShowMerchant(object holder)
    {
        if (!SlayTheStatsConfig.ShowInRunStats) return;

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
    /// Shows stats for a relic option on an ancient event choice screen (NEventOptionButton).
    /// Only fires when the option carries a relic (Option.Relic != null).
    /// Only fires when the option carries a relic (Option.Relic != null).
    /// </summary>
    internal static void ShowAncientOption(object holder)
    {
        if (!SlayTheStatsConfig.ShowInRunStats) return;

        ShowCore(holder, GetRelicIdFromEventOption(holder));
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
            if (SlayTheStatsConfig.DisableTooltipsEntirely) return;

            TooltipHelper.EnsurePanelExists();

            if (rawId == null) return;

            var lookupId = MainFile.Db.Relics.ContainsKey("RELIC." + rawId) ? "RELIC." + rawId
                         : MainFile.Db.Relics.ContainsKey(rawId)             ? rawId
                         : null;

            var filter         = CardHoverShowPatch.BuildFilter(CardHoverShowPatch.RunCharacter);
            var effectiveChar  = CardHoverShowPatch.GetEffectiveCharacter(filter);
            var characterLabel = CardHoverShowPatch.GetCharacterLabel(filter);

            string statsText;
            if (lookupId == null)
            {
                statsText = CardHoverShowPatch.NoDataText(effectiveChar, filter.AscensionMin, filter.AscensionMax);
            }
            else
            {
                var actStats = StatsAggregator.AggregateRelicsByAct(
                    MainFile.Db.Relics[lookupId], filter);
                var wrBaseline = effectiveChar != null
                    ? StatsAggregator.GetCharacterWR(MainFile.Db, effectiveChar)
                    : StatsAggregator.GetGlobalWR(MainFile.Db);
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db);
                statsText = actStats.Count == 0 ? CardHoverShowPatch.NoDataText(effectiveChar, filter.AscensionMin, filter.AscensionMax) : BuildStatsText(actStats, wrBaseline, characterLabel, filter.AscensionMin, filter.AscensionMax, shopBuyRateBaseline);
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
        var matched = source == _activeHolder;
        MainFile.Logger.Info($"[SlayTheStats] RelicHover.Hide: type={source.GetType().Name} matched={matched} hasActiveHover={TooltipHelper.HasActiveHover}");
        if (!matched) return;

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
    /// Walks NEventOptionButton.Option → .Relic (RelicModel) → .Id → .Entry.
    /// Returns null when the option carries no relic (non-relic ancient choices).
    /// </summary>
    private static string? GetRelicIdFromEventOption(object holder)
    {
        var option = AccessTools.Property(holder.GetType(), "Option")?.GetValue(holder);
        if (option == null) return null;
        var model = AccessTools.Property(option.GetType(), "Relic")?.GetValue(option);
        if (model == null) return null;
        var id = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
        if (id == null) return null;
        return AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string;
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

    private static string BuildStatsText(Dictionary<int, RelicStat> actStats, double wrBaseline = 50.0, string characterLabel = "All chars", int? ascensionMin = null, int? ascensionMax = null, double shopBuyRateBaseline = 20.0)
    {
        var sb = new StringBuilder();

        // Relics always show the Buys column: Act(3)  Runs(5)  Buys(7)  Win%(4)
        sb.Append("Act  Runs     Buys  Win%\n");

        int totPresent = 0, totWon = 0, totShopSeen = 0, totShopBought = 0;

        for (int act = 1; act <= 3; act++)
        {
            if (actStats.TryGetValue(act, out var stat) && (stat.RunsPresent > 0 || stat.RunsShopSeen > 0))
            {
                totPresent    += stat.RunsPresent;
                totWon        += stat.RunsWon;
                totShopSeen   += stat.RunsShopSeen;
                totShopBought += stat.RunsShopBought;

                var wrPct   = stat.RunsPresent > 0 ? 100.0 * stat.RunsWon          / stat.RunsPresent  : -1;
                var shopPct = stat.RunsShopSeen > 0 ? 100.0 * stat.RunsShopBought  / stat.RunsShopSeen : -1;
                var wr      = wrPct >= 0 ? $"{Math.Round(wrPct):F0}%" : "-";
                var cRuns   = TooltipHelper.ColN($"{stat.RunsPresent,5}", stat.RunsPresent);
                var cWr     = wrPct >= 0 ? TooltipHelper.ColWR($"{wr,4}", wrPct, stat.RunsPresent, wrBaseline) : $"[color={TooltipHelper.NeutralShade}]{"-",4}[/color]";
                var cBuys   = CardHoverShowPatch.FormatBuysCell(stat.RunsShopBought, stat.RunsShopSeen, shopPct, shopBuyRateBaseline);
                sb.Append($"{act,3} {cRuns}  {cBuys}  {cWr}\n");
            }
            else
            {
                sb.Append($"{act,3} [color={TooltipHelper.NeutralShade}]{"-",5}  {"-",7}  {"-",4}[/color]\n");
            }
        }

        // Total row
        var totWrPct   = totPresent  > 0 ? 100.0 * totWon        / totPresent  : -1;
        var totShopPct = totShopSeen > 0 ? 100.0 * totShopBought / totShopSeen : -1;
        var totWr      = totWrPct >= 0 ? $"{Math.Round(totWrPct):F0}%" : "-";
        var cTotRuns   = TooltipHelper.ColN($"{totPresent,5}", totPresent);
        var cTotWr     = totWrPct >= 0 ? TooltipHelper.ColWR($"{totWr,4}", totWrPct, totPresent, wrBaseline) : $"[color={TooltipHelper.NeutralShade}]{"-",4}[/color]";
        var cTotBuys   = CardHoverShowPatch.FormatBuysCell(totShopBought, totShopSeen, totShopPct, shopBuyRateBaseline);
        sb.Append($"All {cTotRuns}  {cTotBuys}  {cTotWr}");

        var ascPrefix   = CardHoverShowPatch.FormatAscensionPrefix(ascensionMin, ascensionMax);
        var wrStr       = $"{Math.Round(wrBaseline):F0}%";
        var buysBaseStr = $"{Math.Round(shopBuyRateBaseline):F0}%";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{ascPrefix}{characterLabel}  Buys: {buysBaseStr}  Win%: {wrStr}[/color][/font_size][/font]");

        return sb.ToString();
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
        else if (__instance is NMerchantCard)
            CardHoverShowPatch.HideTooltip();
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

/// <summary>
/// Shows relic stats when hovering a relic on a chest reward screen (treasure room).
/// NTreasureRoomRelicHolder exposes a Relic property (NRelic) — same chain as NRelicBasicHolder.
/// </summary>
[HarmonyPatch(typeof(NTreasureRoomRelicHolder), "OnFocus")]
public static class TreasureRoomRelicHolderFocusPatch
{
    static void Postfix(NTreasureRoomRelicHolder __instance) => RelicHoverHelper.Show(__instance);
}

[HarmonyPatch(typeof(NTreasureRoomRelicHolder), "OnUnfocus")]
public static class TreasureRoomRelicHolderUnfocusPatch
{
    static void Postfix(NTreasureRoomRelicHolder __instance) => RelicHoverHelper.Hide(__instance);
}

/// <summary>
/// Shows relic stats when hovering an ancient event option that carries a relic reward
/// (e.g. Neow choices). NEventOptionButton.Option.Relic is non-null only for relic options;
/// ShowAncientOption returns early if null so non-relic options are silently skipped.
/// </summary>
[HarmonyPatch(typeof(NEventOptionButton), "OnFocus")]
public static class AncientEventOptionFocusPatch
{
    internal static Control? ActiveAncientOptionControl;

    static void Postfix(NEventOptionButton __instance)
    {
        ActiveAncientOptionControl = __instance;
        RelicHoverHelper.ShowAncientOption(__instance);
    }
}

[HarmonyPatch(typeof(NEventOptionButton), "OnUnfocus")]
public static class AncientEventOptionUnfocusPatch
{
    static void Postfix(NEventOptionButton __instance)
    {
        if (AncientEventOptionFocusPatch.ActiveAncientOptionControl == __instance)
            AncientEventOptionFocusPatch.ActiveAncientOptionControl = null;
        RelicHoverHelper.Hide(__instance);
    }
}
