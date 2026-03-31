using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace SlayTheStats;

/// <summary>
/// Shows a card stats tooltip when a card is hovered on a reward screen (or any non-combat screen).
/// Only fires for NGridCardHolder (reward/shop/etc.) — NHandCardHolder (in-combat hand) is skipped.
/// </summary>
[HarmonyPatch(typeof(NCardHolder), "CreateHoverTips")]
public static class CardHoverShowPatch
{
    private static bool         _warnedOnce;
    private static bool         _upgradeWarnedOnce;
    private static bool         _ownerWarnedOnce;
    private static NCardHolder? _activeHolder;

    internal static string? CurrentCharacter;

    static void Postfix(NCardHolder __instance)
    {
        try
        {
            if (__instance is NHandCardHolder) return;

            TooltipHelper.EnsurePanelExists();

            var rawId = GetRawCardId(__instance);
            if (rawId == null) return;

            var upgradeLevel = GetUpgradeLevel(__instance);
            var lookupId = FindCardLookupId(rawId, upgradeLevel);

            // Derive character context first — needed for contextual "no data" messages
            // regardless of whether the card has stats data.
            var character = CurrentCharacter ?? GetCharacterFromOwner(__instance);
            if (character != null && CurrentCharacter == null)
                CurrentCharacter = character;

            var maxAscension = SlayTheStatsConfig.OnlyHighestWonAscension
                ? StatsAggregator.GetHighestWonAscension(MainFile.Db, character)
                : (int?)null;

            string statsText;
            if (lookupId == null)
            {
                statsText = NoDataText(character, maxAscension);
            }
            else
            {
                var showBuysLayout      = IsColorlessCard(__instance.CardModel);
                var contextMap          = GetContextMap(lookupId);
                var actStats            = StatsAggregator.AggregateByAct(contextMap, character, gameMode: "standard", onlyAscension: maxAscension);
                var characterWR         = character != null ? StatsAggregator.GetCharacterWR(MainFile.Db, character) : StatsAggregator.GetGlobalWR(MainFile.Db);
                var characterLabel      = character != null ? FormatCharacterName(character) : "All chars";
                var pickRateBaseline    = StatsAggregator.GetPickRateBaseline(MainFile.Db);
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db);
                statsText = actStats.Count == 0 ? NoDataText(character, maxAscension) : BuildStatsText(actStats, characterWR, pickRateBaseline, characterLabel, maxAscension, showBuysLayout, shopBuyRateBaseline);
            }

            TooltipHelper.TrySceneTheftOnce();
            _activeHolder = __instance;
            TooltipHelper.ShowPanel(statsText);
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"SlayTheStats: card tooltip unavailable — {e.Message}");
                _warnedOnce = true;
            }
        }
    }

    // Called from MainFile._Ready() as belt-and-suspenders panel setup.
    internal static void EnsurePanelExists() => TooltipHelper.EnsurePanelExists();

    internal static Control? GetPanelPublic() => TooltipHelper.GetPanelPublic();

    internal static bool IsActiveHover() => _activeHolder != null;

    internal static NCardHolder? ActiveHolder => _activeHolder;

    /// <summary>Returns the active holder cast to Control for layout purposes, or null if unavailable.</summary>
    internal static Control? ActiveHolderControl => _activeHolder as Control;

    internal static void HideTooltip(NCardHolder? source = null)
    {
        if (source != null && source != _activeHolder) return;

        _activeHolder = null;
        TooltipHelper.HasActiveHover = false;
        TooltipHelper.HideWithDelay();
    }

    /// <summary>
    /// Resolves a raw card ID and upgrade level to the key used in the stats DB.
    /// Tries CARD.-prefixed and bare forms, with and without the + suffix.
    /// When GroupCardUpgrades is enabled and only the upgraded variant exists (e.g. a card
    /// always acquired pre-upgraded), falls back to the + entry so it can be merged.
    /// </summary>
    internal static string? FindCardLookupId(string rawId, int upgradeLevel)
    {
        var suffix = upgradeLevel > 0 ? "+" : "";
        return MainFile.Db.Cards.ContainsKey("CARD." + rawId + suffix) ? "CARD." + rawId + suffix
             : MainFile.Db.Cards.ContainsKey("CARD." + rawId)          ? "CARD." + rawId
             : MainFile.Db.Cards.ContainsKey(rawId + suffix)           ? rawId + suffix
             : MainFile.Db.Cards.ContainsKey(rawId)                    ? rawId
             : SlayTheStatsConfig.GroupCardUpgrades && upgradeLevel == 0 && MainFile.Db.Cards.ContainsKey("CARD." + rawId + "+") ? "CARD." + rawId + "+"
             : SlayTheStatsConfig.GroupCardUpgrades && upgradeLevel == 0 && MainFile.Db.Cards.ContainsKey(rawId + "+")           ? rawId + "+"
             : null;
    }

    /// <summary>
    /// Returns the context map for a card. When GroupCardUpgrades is enabled, merges the base
    /// and upgraded versions (e.g. CARD.STRIKE_R and CARD.STRIKE_R+) into a single map by
    /// summing their per-context counters.
    /// </summary>
    internal static Dictionary<string, CardStat> GetContextMap(string lookupId)
    {
        if (!SlayTheStatsConfig.GroupCardUpgrades)
            return MainFile.Db.Cards[lookupId];

        var pairedId = lookupId.EndsWith("+") ? lookupId[..^1] : lookupId + "+";
        if (!MainFile.Db.Cards.TryGetValue(pairedId, out var pairedMap))
            return MainFile.Db.Cards[lookupId];

        var merged = new Dictionary<string, CardStat>(MainFile.Db.Cards[lookupId]);
        foreach (var (key, stat) in pairedMap)
        {
            if (merged.TryGetValue(key, out var existing))
                merged[key] = new CardStat
                {
                    Offered        = existing.Offered        + stat.Offered,
                    Picked         = existing.Picked         + stat.Picked,
                    Won            = existing.Won            + stat.Won,
                    RunsOffered    = existing.RunsOffered    + stat.RunsOffered,
                    RunsPicked     = existing.RunsPicked     + stat.RunsPicked,
                    RunsPresent    = existing.RunsPresent    + stat.RunsPresent,
                    RunsWon        = existing.RunsWon        + stat.RunsWon,
                    RunsShopSeen   = existing.RunsShopSeen   + stat.RunsShopSeen,
                    RunsShopBought = existing.RunsShopBought + stat.RunsShopBought,
                };
            else
                merged[key] = stat;
        }
        return merged;
    }

    /// <summary>
    /// Returns true if this card should use the colorless layout (Buys column instead of Pick%).
    /// Uses the game's own CardModel.Pool.IsColorless — authoritative and covers ColorlessCardPool,
    /// EventCardPool, TokenCardPool, and DeprecatedCardPool.
    /// </summary>
    internal static bool IsColorlessCard(CardModel? model)
    {
        return model?.Pool?.IsColorless ?? false;
    }

    internal static string FormatCharacterName(string character)
    {
        var name = character.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
            ? character.Substring("CHARACTER.".Length)
            : character;
        if (name.Length == 0) return character;
        return char.ToUpper(name[0]) + name.Substring(1).ToLower();
    }

    private static string? GetRawCardId(NCardHolder holder)
    {
        var cardNode = AccessTools.Property(typeof(NCardHolder), "CardNode")?.GetValue(holder);
        if (cardNode == null) return null;
        var model = AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode);
        if (model == null) return null;
        var id = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
        if (id == null) return null;
        return AccessTools.Field(id.GetType(), "Entry")?.GetValue(id) as string
            ?? AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string
            ?? id.ToString();
    }

    private static int GetUpgradeLevel(NCardHolder holder)
    {
        try
        {
            var cardNode = AccessTools.Property(typeof(NCardHolder), "CardNode")?.GetValue(holder);
            var model = cardNode != null ? AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode) : null;
            if (model == null) return 0;
            return AccessTools.Property(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                ?? AccessTools.Field(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                ?? 0;
        }
        catch (Exception e)
        {
            if (!_upgradeWarnedOnce) { MainFile.Logger.Warn($"SlayTheStats: GetUpgradeLevel failed — {e.Message}"); _upgradeWarnedOnce = true; }
            return 0;
        }
    }

    private static string? GetCharacterFromOwner(NCardHolder holder)
    {
        try
        {
            var cardNode  = AccessTools.Property(typeof(NCardHolder), "CardNode")?.GetValue(holder);
            var model     = cardNode != null ? AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode) : null;
            var owner     = model != null ? AccessTools.Property(model.GetType(), "Owner")?.GetValue(model) : null;
            var character = owner != null ? AccessTools.Property(owner.GetType(), "Character")?.GetValue(owner) : null;
            var id        = character != null ? AccessTools.Property(character.GetType(), "Id")?.GetValue(character) : null;
            if (id == null) return null;
            var category = AccessTools.Property(id.GetType(), "Category")?.GetValue(id) as string;
            var entry    = AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string;
            return category != null && entry != null ? $"{category}.{entry}".ToUpper() : null;
        }
        catch (Exception e)
        {
            if (!_ownerWarnedOnce) { MainFile.Logger.Warn($"SlayTheStats: GetCharacterFromOwner failed — {e.Message}"); _ownerWarnedOnce = true; }
            return null;
        }
    }

    /// <summary>
    /// "No data for A6 Ironclad" / "No data for Ironclad" / "No A7 data for all characters" / "No data for all characters"
    /// depending on the current filter context.
    /// </summary>
    internal static string NoDataText(string? character, int? maxAscension)
    {
        string msg;
        if (character != null)
        {
            var name = FormatCharacterName(character);
            var ctx = maxAscension != null ? $"A{maxAscension} {name}" : name;
            msg = $"No data for {ctx}";
        }
        else
        {
            msg = maxAscension != null ? $"No A{maxAscension} data for all characters" : "No data for all characters";
        }
        return $"[font=res://themes/kreon_regular_glyph_space_one.tres][color={TooltipHelper.NeutralShade}]{msg}[/color][/font]";
    }

    internal static string BuildStatsText(Dictionary<int, CardStat> actStats, double characterWR = 50.0, double pickRateBaseline = 100.0 / 3.0, string characterLabel = "All chars", int? maxAscension = null, bool showBuysLayout = false, double shopBuyRateBaseline = 20.0)
    {
        var sb = new StringBuilder();

        if (showBuysLayout)
            return BuildBuysStatsText(actStats, characterWR, characterLabel, maxAscension, shopBuyRateBaseline);

        // Class card columns: Act(3)  Runs(7)  Pick%(5)  Win%(4)
        // Runs shows RunsPresent/RunsOffered, e.g. "12/30"
        sb.Append("Act     Runs  Pick%  Win%\n");

        int totOffered = 0, totPicked = 0, totPresent = 0, totWon = 0;

        for (int act = 1; act <= 3; act++)
        {
            if (actStats.TryGetValue(act, out var stat))
            {
                totOffered += stat.RunsOffered;
                totPicked  += stat.RunsPicked;
                totPresent += stat.RunsPresent;
                totWon     += stat.RunsWon;

                var prPct = stat.RunsOffered > 0 ? 100.0 * stat.RunsPicked / stat.RunsOffered : -1;
                var wrPct = stat.RunsPresent > 0 ? 100.0 * stat.RunsWon    / stat.RunsPresent : -1;
                var pr    = prPct >= 0 ? $"{Math.Round(prPct):F0}%" : "-";
                var wr    = wrPct >= 0 ? $"{Math.Round(wrPct):F0}%" : "-";

                // Pad before wrapping in color tags — BBCode tags are invisible to layout but
                // would break fixed-width padding if included in the format string width.
                // Pick% significance uses RunsOffered (total trials), not RunsPicked (successes).
                // Picks: numerator colored by ColN (bold if strong), /denominator always neutral.
                var numStr = $"{stat.RunsPresent}";
                var denStr = $"{stat.RunsOffered}";
                var cPicks = $"{new string(' ', Math.Max(0, 7 - numStr.Length - 1 - denStr.Length))}"
                           + $"{TooltipHelper.ColN(numStr, stat.RunsPresent)}"
                           + $"[color={TooltipHelper.NeutralShade}]/{denStr}[/color]";
                var cPr    = prPct >= 0 ? TooltipHelper.ColPR($"{pr,5}", prPct, stat.RunsOffered, pickRateBaseline) : $"[color={TooltipHelper.NeutralShade}]{"-",5}[/color]";
                var cWr    = wrPct >= 0 ? TooltipHelper.ColWR($"{wr,4}", wrPct, stat.RunsPresent, characterWR) : $"[color={TooltipHelper.NeutralShade}]{"-",4}[/color]";

                sb.Append($"{act,3}  {cPicks}  {cPr}  {cWr}\n");
            }
            else
            {
                sb.Append($"{act,3}  [color={TooltipHelper.NeutralShade}]{"-",7}  {"-",5}  {"-",4}[/color]\n");
            }
        }

        // Total row — aggregated across all acts
        var totPrPct  = totOffered > 0 ? 100.0 * totPicked  / totOffered : -1;
        var totWrPct  = totPresent > 0 ? 100.0 * totWon     / totPresent : -1;
        var totPr     = totPrPct >= 0 ? $"{Math.Round(totPrPct):F0}%" : "-";
        var totWr     = totWrPct >= 0 ? $"{Math.Round(totWrPct):F0}%" : "-";
        var totNumStr  = $"{totPresent}";
        var totDenStr  = $"{totOffered}";
        var cTotPicks  = $"{new string(' ', Math.Max(0, 7 - totNumStr.Length - 1 - totDenStr.Length))}"
                       + $"{TooltipHelper.ColN(totNumStr, totPresent)}"
                       + $"[color={TooltipHelper.NeutralShade}]/{totDenStr}[/color]";
        var cTotPr    = totPrPct >= 0 ? TooltipHelper.ColPR($"{totPr,5}", totPrPct, totOffered, pickRateBaseline) : $"[color={TooltipHelper.NeutralShade}]{"-",5}[/color]";
        var cTotWr    = totWrPct >= 0 ? TooltipHelper.ColWR($"{totWr,4}", totWrPct, totPresent, characterWR) : $"[color={TooltipHelper.NeutralShade}]{"-",4}[/color]";
        sb.Append($"All  {cTotPicks}  {cTotPr}  {cTotWr}");

        var ascPrefix = maxAscension != null ? $"A{maxAscension} " : "";
        var prBaseStr = $"{Math.Round(pickRateBaseline):F0}%";
        var wrStr     = $"{Math.Round(characterWR):F0}%";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{ascPrefix}{characterLabel} Pick%: {prBaseStr}  Win%: {wrStr}[/color][/font_size][/font]");

        return sb.ToString();
    }

    /// <summary>
    /// Colorless card layout: Act | Runs | Buys | Win%
    /// Runs shows RunsPresent only (no fight-reward denominator).
    /// Buys shows RunsShopBought/RunsShopSeen (purchases / shop appearances).
    /// Win% is placed last, consistent across all stat tables.
    /// </summary>
    private static string BuildBuysStatsText(Dictionary<int, CardStat> actStats, double characterWR, string characterLabel, int? maxAscension, double shopBuyRateBaseline)
    {
        var sb = new StringBuilder();
        // Columns: Act(3)  Runs(5)  Buys(7)  Win%(4)
        // "Buys" right-aligned in 7-char field: "   Buys"
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

                var wrPct   = stat.RunsPresent  > 0 ? 100.0 * stat.RunsWon          / stat.RunsPresent  : -1;
                var buysPct = stat.RunsShopSeen > 0 ? 100.0 * stat.RunsShopBought   / stat.RunsShopSeen : -1;
                var wr      = wrPct >= 0 ? $"{Math.Round(wrPct):F0}%" : "-";

                var cPicks = TooltipHelper.ColN($"{stat.RunsPresent,5}", stat.RunsPresent);
                var cBuys  = FormatBuysCell(stat.RunsShopBought, stat.RunsShopSeen, buysPct, shopBuyRateBaseline);
                var cWr    = wrPct >= 0 ? TooltipHelper.ColWR($"{wr,4}", wrPct, stat.RunsPresent, characterWR) : $"[color={TooltipHelper.NeutralShade}]{"-",4}[/color]";

                sb.Append($"{act,3} {cPicks}  {cBuys}  {cWr}\n");
            }
            else
            {
                sb.Append($"{act,3} [color={TooltipHelper.NeutralShade}]{"-",5}  {"-",7}  {"-",4}[/color]\n");
            }
        }

        var totWrPct   = totPresent  > 0 ? 100.0 * totWon        / totPresent  : -1;
        var totBuysPct = totShopSeen > 0 ? 100.0 * totShopBought / totShopSeen : -1;
        var totWr      = totWrPct >= 0 ? $"{Math.Round(totWrPct):F0}%" : "-";

        var cTotPicks = TooltipHelper.ColN($"{totPresent,5}", totPresent);
        var cTotBuys  = FormatBuysCell(totShopBought, totShopSeen, totBuysPct, shopBuyRateBaseline);
        var cTotWr    = totWrPct >= 0 ? TooltipHelper.ColWR($"{totWr,4}", totWrPct, totPresent, characterWR) : $"[color={TooltipHelper.NeutralShade}]{"-",4}[/color]";
        sb.Append($"All {cTotPicks}  {cTotBuys}  {cTotWr}");

        var ascPrefix    = maxAscension != null ? $"A{maxAscension} " : "";
        var wrStr        = $"{Math.Round(characterWR):F0}%";
        var buysBaseStr  = $"{Math.Round(shopBuyRateBaseline):F0}%";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{ascPrefix}{characterLabel} Buys: {buysBaseStr}  Win%: {wrStr}[/color][/font_size][/font]");

        return sb.ToString();
    }

    /// <summary>
    /// Formats a Buys cell as "bought/seen" right-aligned in a 7-char visual field,
    /// with the numerator colored by buy-rate significance. Returns neutral "-" when no data.
    /// </summary>
    internal static string FormatBuysCell(int bought, int seen, double pct, double baseline)
    {
        if (pct < 0)
            return $"[color={TooltipHelper.NeutralShade}]{"-",7}[/color]";
        var numStr = $"{bought}";
        var denStr = $"{seen}";
        return $"{new string(' ', Math.Max(0, 7 - numStr.Length - 1 - denStr.Length))}"
             + $"{TooltipHelper.ColBuys(numStr, pct, seen, baseline)}"
             + $"[color={TooltipHelper.NeutralShade}]/{denStr}[/color]";
    }
}

[HarmonyPatch(typeof(NCardHolder), "ClearHoverTips")]
public static class CardHoverHidePatch
{
    private static bool _warnedOnce;

    static void Postfix(NCardHolder __instance)
    {
        try { CardHoverShowPatch.HideTooltip(__instance); }
        catch (Exception e)
        {
            if (!_warnedOnce) { MainFile.Logger.Warn($"SlayTheStats: CardHoverHidePatch failed — {e.Message}"); _warnedOnce = true; }
        }
    }
}

/// <summary>
/// Safety net: hides the panel when a card is returned to the pool without going through
/// ClearHoverTips. Skips if any hover (card or relic) is still active.
/// Uses HideWithDelay (not direct hide) so that a relic OnFocus postfix firing in the same
/// frame can cancel the hide by incrementing ShowGen before the timer fires.
/// </summary>
[HarmonyPatch(typeof(NCard), "OnFreedToPool")]
public static class CardFreedToPoolPatch
{
    private static bool _warnedOnce;

    static void Postfix()
    {
        try
        {
            var node = TooltipHelper.GetPanelPublic();
            if (node == null || !node.Visible) return;
            if (TooltipHelper.HasActiveHover) return;
            TooltipHelper.HideWithDelay();
        }
        catch (Exception e)
        {
            if (!_warnedOnce) { MainFile.Logger.Warn($"SlayTheStats: CardFreedToPoolPatch failed — {e.Message}"); _warnedOnce = true; }
        }
    }
}

[HarmonyPatch(typeof(NGame), "ReturnToMainMenuAfterRun")]
public static class ClearCurrentCharacterPatch
{
    static void Prefix()
    {
        CardHoverShowPatch.CurrentCharacter = null;
    }
}

/// <summary>
/// Shows card stats in the inspect screen (opened by right-clicking a card on a reward/shop screen).
/// NInspectCardScreen.UpdateCardDisplay fires whenever the displayed card changes (on open, on
/// left/right navigation, and on upgrade-toggle). We re-derive the card ID from _card.Model
/// with the same reflection chain used for hovering, so no duplicate logic is needed.
/// </summary>
[HarmonyPatch(typeof(NInspectCardScreen), "UpdateCardDisplay")]
public static class InspectCardDisplayPatch
{
    private static bool _warnedOnce;

    static void Postfix(NInspectCardScreen __instance)
    {
        try
        {
            TooltipHelper.EnsurePanelExists();

            // _card is a private NCard field on NInspectCardScreen.
            var card  = AccessTools.Field(typeof(NInspectCardScreen), "_card")?.GetValue(__instance);
            if (card == null) return;
            var model = AccessTools.Property(card.GetType(), "Model")?.GetValue(card);
            if (model == null) return;
            var id    = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
            if (id == null) return;
            var rawId = AccessTools.Field(id.GetType(), "Entry")?.GetValue(id) as string
                     ?? AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string
                     ?? id.ToString();
            if (rawId == null) return;

            var upgradeLevel = AccessTools.Property(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                            ?? AccessTools.Field(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                            ?? 0;
            var lookupId = CardHoverShowPatch.FindCardLookupId(rawId, upgradeLevel);

            var character    = CardHoverShowPatch.CurrentCharacter;
            var maxAscension = SlayTheStatsConfig.OnlyHighestWonAscension
                ? StatsAggregator.GetHighestWonAscension(MainFile.Db, character)
                : (int?)null;

            string statsText;
            if (lookupId == null)
            {
                statsText = CardHoverShowPatch.NoDataText(character, maxAscension);
            }
            else
            {
                var showBuysLayout      = CardHoverShowPatch.IsColorlessCard(model as CardModel);
                var contextMap          = CardHoverShowPatch.GetContextMap(lookupId);
                var actStats            = StatsAggregator.AggregateByAct(contextMap, character, gameMode: "standard", onlyAscension: maxAscension);
                var characterWR         = character != null ? StatsAggregator.GetCharacterWR(MainFile.Db, character) : StatsAggregator.GetGlobalWR(MainFile.Db);
                var characterLabel      = character != null ? CardHoverShowPatch.FormatCharacterName(character) : "All chars";
                var pickRateBaseline    = StatsAggregator.GetPickRateBaseline(MainFile.Db);
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db);
                statsText = actStats.Count == 0
                    ? CardHoverShowPatch.NoDataText(character, maxAscension)
                    : CardHoverShowPatch.BuildStatsText(actStats, characterWR, pickRateBaseline, characterLabel, maxAscension, showBuysLayout, shopBuyRateBaseline);
            }

            TooltipHelper.TrySceneTheftOnce();
            TooltipHelper.InspectActive = true;
            TooltipHelper.ShowPanel(statsText);
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"SlayTheStats: inspect tooltip unavailable — {e.Message}");
                _warnedOnce = true;
            }
        }
    }
}

/// <summary>
/// Hides the stats panel when the inspect screen closes.
/// </summary>
[HarmonyPatch(typeof(NInspectCardScreen), "Close")]
public static class InspectCardClosePatch
{
    static void Postfix()
    {
        try
        {
            TooltipHelper.InspectActive = false;
            CardHoverShowPatch.HideTooltip();
        }
        catch { }
    }
}

/// <summary>
/// Shows card stats when hovering a card in the shop. NMerchantCard extends NMerchantSlot
/// and uses CreateHoverTip/ClearHoverTip rather than the NCardHolder.CreateHoverTips mechanism.
/// </summary>
[HarmonyPatch(typeof(NMerchantCard), "CreateHoverTip")]
public static class MerchantCardCreateHoverTipPatch
{
    private static bool _warnedOnce;

    internal static Control? ActiveMerchantCard;

    static void Postfix(NMerchantCard __instance)
    {
        try
        {
            ActiveMerchantCard = __instance;
            TooltipHelper.EnsurePanelExists();

            // Get the card via the private _cardNode field (NCard?)
            var cardNode = AccessTools.Field(typeof(NMerchantCard), "_cardNode")?.GetValue(__instance);
            if (cardNode == null) return;

            var model = AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode);
            if (model == null) return;

            var id = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
            if (id == null) return;

            var rawId = AccessTools.Field(id.GetType(), "Entry")?.GetValue(id) as string
                     ?? AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string
                     ?? id.ToString();
            if (rawId == null) return;

            var upgradeLevel = AccessTools.Property(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                            ?? AccessTools.Field(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                            ?? 0;
            var lookupId = CardHoverShowPatch.FindCardLookupId(rawId, upgradeLevel);

            var character    = CardHoverShowPatch.CurrentCharacter;
            var maxAscension = SlayTheStatsConfig.OnlyHighestWonAscension
                ? StatsAggregator.GetHighestWonAscension(MainFile.Db, character)
                : (int?)null;

            string statsText;
            if (lookupId == null)
            {
                statsText = CardHoverShowPatch.NoDataText(character, maxAscension);
            }
            else
            {
                // In the shop, always use the Runs/Buys layout regardless of card class
                var contextMap          = CardHoverShowPatch.GetContextMap(lookupId);
                var actStats            = StatsAggregator.AggregateByAct(contextMap, character, gameMode: "standard", onlyAscension: maxAscension);
                var characterWR         = character != null ? StatsAggregator.GetCharacterWR(MainFile.Db, character) : StatsAggregator.GetGlobalWR(MainFile.Db);
                var characterLabel      = character != null ? CardHoverShowPatch.FormatCharacterName(character) : "All chars";
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db);
                statsText = actStats.Count == 0
                    ? CardHoverShowPatch.NoDataText(character, maxAscension)
                    : CardHoverShowPatch.BuildStatsText(actStats, characterWR, 0, characterLabel, maxAscension, showBuysLayout: true, shopBuyRateBaseline);
            }

            TooltipHelper.TrySceneTheftOnce();
            TooltipHelper.ShowPanel(statsText);
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"SlayTheStats: merchant card tooltip unavailable — {e.Message}");
                _warnedOnce = true;
            }
        }
    }
}

[HarmonyPatch(typeof(NMerchantCard), "ClearHoverTip")]
public static class MerchantCardClearHoverTipPatch
{
    static void Postfix(NMerchantCard __instance)
    {
        if (MerchantCardCreateHoverTipPatch.ActiveMerchantCard == __instance)
            MerchantCardCreateHoverTipPatch.ActiveMerchantCard = null;
    }
}
