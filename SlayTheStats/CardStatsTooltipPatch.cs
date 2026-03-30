using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace SlayTheStats;

/// <summary>
/// Shows a card stats tooltip when a card is hovered on a reward screen (or any non-combat screen).
/// Only fires for NGridCardHolder (reward/shop/etc.) — NHandCardHolder (in-combat hand) is skipped.
/// </summary>
[HarmonyPatch(typeof(NCardHolder), "CreateHoverTips")]
public static class CardHoverShowPatch
{
    private static bool         _warnedOnce;
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
            var suffix = upgradeLevel > 0 ? "+" : "";

            var lookupId = MainFile.Db.Cards.ContainsKey("CARD." + rawId + suffix) ? "CARD." + rawId + suffix
                         : MainFile.Db.Cards.ContainsKey("CARD." + rawId)          ? "CARD." + rawId
                         : MainFile.Db.Cards.ContainsKey(rawId + suffix)           ? rawId + suffix
                         : MainFile.Db.Cards.ContainsKey(rawId)                    ? rawId
                         : null;

            string statsText;
            if (lookupId == null)
            {
                statsText = $"[font=res://themes/kreon_regular_glyph_space_one.tres][color={TooltipHelper.NeutralShade}]No data[/color][/font]";
            }
            else
            {
                var contextMap = GetContextMap(lookupId);
                var character = CurrentCharacter ?? GetCharacterFromOwner(__instance);
                if (character != null && CurrentCharacter == null)
                    CurrentCharacter = character;

                var maxAscension = SlayTheStatsConfig.OnlyHighestWonAscension
                    ? StatsAggregator.GetHighestWonAscension(MainFile.Db, character)
                    : (int?)null;

                var actStats         = StatsAggregator.AggregateByAct(contextMap, character, gameMode: "standard", onlyAscension: maxAscension);
                var characterWR      = character != null ? StatsAggregator.GetCharacterWR(MainFile.Db, character) : StatsAggregator.GetGlobalWR(MainFile.Db);
                var characterLabel   = character != null ? FormatCharacterName(character) : "All chars";
                var pickRateBaseline = StatsAggregator.GetPickRateBaseline(MainFile.Db);
                statsText = actStats.Count == 0 ? $"[font=res://themes/kreon_regular_glyph_space_one.tres][color={TooltipHelper.NeutralShade}]No data[/color][/font]" : BuildStatsText(actStats, characterWR, pickRateBaseline, characterLabel, maxAscension);
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

    internal static void HideTooltip(NCardHolder? source = null)
    {
        if (source != null && source != _activeHolder) return;

        _activeHolder = null;
        TooltipHelper.HasActiveHover = false;
        TooltipHelper.HideWithDelay();
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
                    Offered     = existing.Offered     + stat.Offered,
                    Picked      = existing.Picked      + stat.Picked,
                    Won         = existing.Won         + stat.Won,
                    RunsOffered = existing.RunsOffered + stat.RunsOffered,
                    RunsPicked  = existing.RunsPicked  + stat.RunsPicked,
                    RunsWon     = existing.RunsWon     + stat.RunsWon,
                };
            else
                merged[key] = stat;
        }
        return merged;
    }

    internal static string FormatCharacterName(string character)
    {
        var name = character.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
            ? character.Substring("CHARACTER.".Length)
            : character;
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
        catch { return 0; }
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
        catch { return null; }
    }

    internal static string BuildStatsText(Dictionary<int, CardStat> actStats, double characterWR = 50.0, double pickRateBaseline = 100.0 / 3.0, string characterLabel = "All chars", int? maxAscension = null)
    {
        var sb = new StringBuilder();
        sb.Append("Act  Picks  Pick%  Win%\n");

        int totOffered = 0, totPicked = 0, totWon = 0;

        for (int act = 1; act <= 3; act++)
        {
            if (actStats.TryGetValue(act, out var stat))
            {
                totOffered += stat.RunsOffered;
                totPicked  += stat.RunsPicked;
                totWon     += stat.RunsWon;

                var prPct   = stat.RunsOffered > 0 ? 100.0 * stat.RunsPicked / stat.RunsOffered : -1;
                var wrPct   = stat.RunsPicked  > 0 ? 100.0 * stat.RunsWon    / stat.RunsPicked  : -1;
                var pr      = prPct >= 0 ? $"{Math.Round(prPct):F0}%" : "-";
                var wr      = wrPct >= 0 ? $"{Math.Round(wrPct):F0}%" : "-";
                var pickOff = $"{stat.RunsPicked}/{stat.RunsOffered}";

                var pickN = stat.RunsPicked;

                // Pad before wrapping in color tags — BBCode tags are invisible to layout but
                // would break fixed-width padding if included in the format string width.
                var cPickOff = TooltipHelper.ColN($"{pickOff,5}", pickN);
                var cPr      = prPct >= 0 ? TooltipHelper.ColPR($"{pr,4}", prPct, pickN, pickRateBaseline) : $"[color={TooltipHelper.NeutralShade}]{pr,4}[/color]";
                var cWr      = wrPct >= 0 ? TooltipHelper.ColWR($"{wr,4}", wrPct, stat.RunsPicked, characterWR) : $"[color={TooltipHelper.NeutralShade}]{wr,4}[/color]";

                sb.Append($"{act,3}  {cPickOff}   {cPr}  {cWr}\n");
            }
            else
            {
                sb.Append($"{act,3}  [color={TooltipHelper.NeutralShade}]    -      -     -[/color]\n");
            }
        }

        // Total row — aggregated across all acts
        var totPrPct   = totOffered > 0 ? 100.0 * totPicked / totOffered : -1;
        var totWrPct   = totPicked  > 0 ? 100.0 * totWon    / totPicked  : -1;
        var totPr      = totPrPct >= 0 ? $"{Math.Round(totPrPct):F0}%" : "-";
        var totWr      = totWrPct >= 0 ? $"{Math.Round(totWrPct):F0}%" : "-";
        var totPickOff = $"{totPicked}/{totOffered}";
        var totPickN   = totPicked / 3;
        var cTotPickOff = TooltipHelper.ColN($"{totPickOff,5}", totPickN);
        var cTotPr      = totPrPct >= 0 ? TooltipHelper.ColPR($"{totPr,4}", totPrPct, totPickN, pickRateBaseline) : $"[color={TooltipHelper.NeutralShade}]{totPr,4}[/color]";
        var cTotWr      = totWrPct >= 0 ? TooltipHelper.ColWR($"{totWr,4}", totWrPct, totPicked / 3, characterWR) : $"[color={TooltipHelper.NeutralShade}]{totWr,4}[/color]";
        sb.Append($"All  {cTotPickOff}   {cTotPr}  {cTotWr}");

        var ascPrefix = maxAscension != null ? $"A{maxAscension} " : "";
        var prBaseStr = $"{Math.Round(pickRateBaseline * 100):F0}%";
        var wrStr     = $"{Math.Round(characterWR):F0}%";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][color=#686868]{ascPrefix}{characterLabel} Pick%: {prBaseStr}  Win%: {wrStr}[/color][/font]");

        return sb.ToString();
    }
}

[HarmonyPatch(typeof(NCardHolder), "ClearHoverTips")]
public static class CardHoverHidePatch
{
    static void Postfix(NCardHolder __instance)
    {
        try { CardHoverShowPatch.HideTooltip(__instance); }
        catch { }
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
    static void Postfix()
    {
        try
        {
            var node = TooltipHelper.GetPanelPublic();
            if (node == null || !node.Visible) return;
            if (TooltipHelper.HasActiveHover) return;
            TooltipHelper.HideWithDelay();
        }
        catch { }
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
            var suffix = upgradeLevel > 0 ? "+" : "";

            var lookupId = MainFile.Db.Cards.ContainsKey("CARD." + rawId + suffix) ? "CARD." + rawId + suffix
                         : MainFile.Db.Cards.ContainsKey("CARD." + rawId)          ? "CARD." + rawId
                         : MainFile.Db.Cards.ContainsKey(rawId + suffix)           ? rawId + suffix
                         : MainFile.Db.Cards.ContainsKey(rawId)                    ? rawId
                         : null;

            string statsText;
            if (lookupId == null)
            {
                statsText = $"[font=res://themes/kreon_regular_glyph_space_one.tres][color={TooltipHelper.NeutralShade}]No data[/color][/font]";
            }
            else
            {
                var contextMap       = CardHoverShowPatch.GetContextMap(lookupId);
                var character        = CardHoverShowPatch.CurrentCharacter;
                var maxAscension     = SlayTheStatsConfig.OnlyHighestWonAscension
                    ? StatsAggregator.GetHighestWonAscension(MainFile.Db, character)
                    : (int?)null;
                var actStats         = StatsAggregator.AggregateByAct(contextMap, character, gameMode: "standard", onlyAscension: maxAscension);
                var characterWR      = character != null ? StatsAggregator.GetCharacterWR(MainFile.Db, character) : StatsAggregator.GetGlobalWR(MainFile.Db);
                var characterLabel   = character != null ? CardHoverShowPatch.FormatCharacterName(character) : "All chars";
                var pickRateBaseline = StatsAggregator.GetPickRateBaseline(MainFile.Db);
                statsText = actStats.Count == 0
                    ? $"[font=res://themes/kreon_regular_glyph_space_one.tres][color={TooltipHelper.NeutralShade}]No data[/color][/font]"
                    : CardHoverShowPatch.BuildStatsText(actStats, characterWR, pickRateBaseline, characterLabel, maxAscension);
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
