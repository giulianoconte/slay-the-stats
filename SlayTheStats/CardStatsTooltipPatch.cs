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
    private static NCardHolder? _activeHolder;

    internal static string? RunCharacter;
    internal static bool    IsInRun;

    static void Postfix(NCardHolder __instance)
    {
        try
        {
            if (__instance is NHandCardHolder) return;
            if (SlayTheStatsConfig.DisableTooltipsEntirely) return;
            if (!SlayTheStatsConfig.ShowInRunStats && !IsInsideCardLibrary(__instance)) return;

            TooltipHelper.EnsurePanelExists();

            var rawId = GetRawCardId(__instance);
            if (rawId == null) return;

            var upgradeLevel = GetUpgradeLevel(__instance);
            var isCompendium = IsInsideCardLibrary(__instance);
            var groupUpgrades = isCompendium ? SlayTheStatsConfig.GroupCardUpgrades : SlayTheStatsConfig.DefaultGroupCardUpgrades;
            var lookupId = FindCardLookupId(rawId, upgradeLevel, groupUpgrades);

            var cardOwner = GetOwningCharacter(__instance.CardModel);
            var filter = isCompendium ? BuildCompendiumFilter(RunCharacter, cardOwner) : BuildInRunFilter(RunCharacter);
            var effectiveChar = GetEffectiveCharacter(filter);
            var characterLabel = GetCharacterLabel(filter);

            string statsText;
            if (lookupId == null)
            {
                statsText = NoDataText(filter);
            }
            else
            {
                var showBuysLayout      = IsColorlessCard(__instance.CardModel);
                var contextMap          = GetContextMap(lookupId, groupUpgrades);
                var actStats            = StatsAggregator.AggregateByAct(contextMap, filter);
                var characterWR         = effectiveChar != null ? StatsAggregator.GetCharacterWR(MainFile.Db, effectiveChar) : StatsAggregator.GetGlobalWR(MainFile.Db);
                var pickRateBaseline    = StatsAggregator.GetPickRateBaseline(MainFile.Db);
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db);
                statsText = actStats.Count == 0 ? NoDataText(filter) : BuildStatsText(actStats, characterWR, pickRateBaseline, characterLabel, filter.AscensionMin, filter.AscensionMax, showBuysLayout, shopBuyRateBaseline, filter);
            }

            TooltipHelper.TrySceneTheftOnce();
            _activeHolder = __instance;
            TooltipHelper.ShowPanel(statsText, __instance as Control);
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
    internal static string? FindCardLookupId(string rawId, int upgradeLevel, bool groupUpgrades)
    {
        var suffix = upgradeLevel > 0 ? "+" : "";
        return MainFile.Db.Cards.ContainsKey("CARD." + rawId + suffix) ? "CARD." + rawId + suffix
             : MainFile.Db.Cards.ContainsKey("CARD." + rawId)          ? "CARD." + rawId
             : MainFile.Db.Cards.ContainsKey(rawId + suffix)           ? rawId + suffix
             : MainFile.Db.Cards.ContainsKey(rawId)                    ? rawId
             : groupUpgrades && upgradeLevel == 0 && MainFile.Db.Cards.ContainsKey("CARD." + rawId + "+") ? "CARD." + rawId + "+"
             : groupUpgrades && upgradeLevel == 0 && MainFile.Db.Cards.ContainsKey(rawId + "+")           ? rawId + "+"
             : null;
    }

    /// <summary>
    /// Returns the context map for a card. When groupUpgrades is true, merges the base
    /// and upgraded versions (e.g. CARD.STRIKE_R and CARD.STRIKE_R+) into a single map by
    /// summing their per-context counters.
    /// </summary>
    internal static Dictionary<string, CardStat> GetContextMap(string lookupId, bool groupUpgrades)
    {
        if (!groupUpgrades)
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
    /// <summary>
    /// Derives the owning character ID from a card's pool type.
    /// Returns null for colorless, curse, event, ancient, and misc cards.
    /// </summary>
    internal static string? GetOwningCharacter(CardModel? model)
    {
        if (model?.Pool == null) return null;
        if (model.Pool.IsColorless) return null;
        var poolName = model.Pool.GetType().Name;
        return poolName switch
        {
            "IroncladCardPool"    => "CHARACTER.IRONCLAD",
            "SilentCardPool"      => "CHARACTER.SILENT",
            "DefectCardPool"      => "CHARACTER.DEFECT",
            "RegentCardPool"      => "CHARACTER.REGENT",
            "NecrobinderCardPool" => "CHARACTER.NECROBINDER",
            _ => null,
        };
    }

    /// Uses the game's own CardModel.Pool.IsColorless — authoritative and covers ColorlessCardPool,
    /// EventCardPool, TokenCardPool, and DeprecatedCardPool.
    /// </summary>
    internal static bool IsColorlessCard(CardModel? model)
    {
        return model?.Pool?.IsColorless ?? false;
    }

    private static bool IsInsideCardLibrary(Control control)
    {
        Node? node = ((Node)control).GetParent();
        while (node != null)
        {
            if (node.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardLibrary")
                return true;
            node = node.GetParent();
        }
        return false;
    }

    internal static string FormatCharacterName(string character)
    {
        var name = character.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
            ? character.Substring("CHARACTER.".Length)
            : character;
        if (name.Length == 0) return character;
        return char.ToUpper(name[0]) + name.Substring(1).ToLower();
    }

    /// <summary>
    /// Builds an AggregationFilter for compendium tooltips — reads all pane filter settings.
    /// Used by compendium card/relic hovers and inspect screens.
    /// </summary>
    /// <summary>
    /// Builds a compendium filter. When ClassSpecificStats is on, cardOwnerCharacter
    /// (derived from the card's pool) is used to filter by owning class.
    /// For non-class cards (colorless, curse, etc.) cardOwnerCharacter is null → all chars.
    /// </summary>
    /// <summary>True iff the given version config value should map to a null lower bound on the filter.</summary>
    private static bool IsVersionLowerBound(string v) =>
        string.IsNullOrEmpty(v) || v == SlayTheStatsConfig.VersionLowest;

    /// <summary>True iff the given version config value should map to a null upper bound on the filter.</summary>
    private static bool IsVersionUpperBound(string v) =>
        string.IsNullOrEmpty(v) || v == SlayTheStatsConfig.VersionHighest;

    internal static AggregationFilter BuildCompendiumFilter(string? runCharacter, string? cardOwnerCharacter = null)
    {
        var (ascMin, ascMax) = ResolveAscensionBounds(SlayTheStatsConfig.AscensionMin, SlayTheStatsConfig.AscensionMax);
        var filter = new AggregationFilter
        {
            GameMode = "standard",
            AscensionMin = ascMin,
            AscensionMax = ascMax,
            VersionMin = IsVersionLowerBound(SlayTheStatsConfig.VersionMin) ? null : SlayTheStatsConfig.VersionMin,
            VersionMax = IsVersionUpperBound(SlayTheStatsConfig.VersionMax) ? null : SlayTheStatsConfig.VersionMax,
            Profile = string.IsNullOrEmpty(SlayTheStatsConfig.FilterProfile) ? null : SlayTheStatsConfig.FilterProfile,
        };

        // Character: derived from ClassFilter.
        //   ""              → all characters (no filter)
        //   "__class__"     → use the card's owning class (null for colorless/curse/etc.)
        //   "CHARACTER.X"   → that specific character, regardless of card type
        // The compendium never falls back to RunCharacter.
        var classFilter = SlayTheStatsConfig.ClassFilter;
        if (classFilter == SlayTheStatsConfig.ClassFilterClassSpecific)
        {
            if (cardOwnerCharacter != null) filter.Character = cardOwnerCharacter;
        }
        else if (!string.IsNullOrEmpty(classFilter))
        {
            filter.Character = classFilter;
        }

        var effectiveChar = GetEffectiveCharacter(filter);
        if (SlayTheStatsConfig.OnlyHighestWonAscension)
        {
            var highest = StatsAggregator.GetHighestWonAscension(MainFile.Db, effectiveChar);
            if (highest != null)
            {
                filter.AscensionMin = highest;
                filter.AscensionMax = highest;
            }
        }

        return filter;
    }

    /// <summary>
    /// Builds a minimal AggregationFilter for in-run tooltips (rewards, shop, relic bar).
    /// Only applies the current run's character and the OnlyHighestWonAscension setting.
    /// Ignores all compendium pane filter settings.
    /// </summary>
    internal static AggregationFilter BuildInRunFilter(string? runCharacter)
    {
        // In-run tooltips always use the user's saved defaults, not the live pane values.
        var (ascMin, ascMax) = ResolveAscensionBounds(SlayTheStatsConfig.DefaultAscensionMin, SlayTheStatsConfig.DefaultAscensionMax);
        var filter = new AggregationFilter
        {
            GameMode = "standard",
            Character = runCharacter,
            AscensionMin = ascMin,
            AscensionMax = ascMax,
            VersionMin = IsVersionLowerBound(SlayTheStatsConfig.DefaultVersionMin) ? null : SlayTheStatsConfig.DefaultVersionMin,
            VersionMax = IsVersionUpperBound(SlayTheStatsConfig.DefaultVersionMax) ? null : SlayTheStatsConfig.DefaultVersionMax,
            Profile = string.IsNullOrEmpty(SlayTheStatsConfig.DefaultFilterProfile) ? null : SlayTheStatsConfig.DefaultFilterProfile,
        };

        if (SlayTheStatsConfig.OnlyHighestWonAscension)
        {
            var highest = StatsAggregator.GetHighestWonAscension(MainFile.Db, runCharacter);
            if (highest != null)
            {
                filter.AscensionMin = highest;
                filter.AscensionMax = highest;
            }
        }

        return filter;
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

    /// <summary>
    /// "No data" message followed by the comma-separated filter context (same
    /// footer as a populated tooltip), so the user can see what filter is
    /// excluding the data without us repeating that info in the headline.
    /// </summary>
    internal static string NoDataText(AggregationFilter filter)
    {
        var characterLabel = GetCharacterLabel(filter);
        var filterCtx = BuildFilterContext(characterLabel, filter);
        var sb = new StringBuilder();
        sb.Append($"[font=res://themes/kreon_regular_glyph_space_one.tres][color={TooltipHelper.NeutralShade}]No data[/color][/font]");
        if (filterCtx.Length > 0)
            sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{filterCtx}[/color][/font_size][/font]");
        return sb.ToString();
    }

    /// <summary>
    /// Formats an ascension range prefix for the footer.
    /// Single value: "A5 ". Range: "A5-9 ". No filter: "".
    /// </summary>
    /// <summary>
    /// Returns the single effective character from the filter for WR baseline lookup.
    /// Returns null if multiple or all characters are selected.
    /// </summary>
    internal static string? GetEffectiveCharacter(AggregationFilter filter)
    {
        if (filter.Character != null) return filter.Character;
        if (filter.Characters != null && filter.Characters.Count == 1)
            return filter.Characters.First();
        return null;
    }

    /// <summary>
    /// Returns a human-readable character label for the footer.
    /// </summary>
    internal static string GetCharacterLabel(AggregationFilter filter)
    {
        var ch = GetEffectiveCharacter(filter);
        return ch != null ? FormatCharacterName(ch) : "All characters";
    }

    // Cache of per-character display BBCode (icon + name, or plain text
    // fallback) so we don't re-check the resource path on every tooltip
    // repaint. Keyed by character ID (e.g. "CHARACTER.IRONCLAD").
    private static readonly Dictionary<string, string> _characterDisplayCache = new();

    /// <summary>
    /// Returns the footer-friendly character display: an inline BBCode [img]
    /// of the top-panel head-shot followed by the formatted character name
    /// (e.g. "[img]...[/img] Ironclad"). Falls back to just the formatted
    /// name if the icon resource couldn't be loaded — covers modded
    /// characters and mismatched IDs.
    /// </summary>
    internal static string GetCharacterDisplay(string character)
    {
        if (_characterDisplayCache.TryGetValue(character, out var cached))
            return cached;

        var name = character.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
            ? character.Substring("CHARACTER.".Length)
            : character;
        var path = $"res://images/ui/top_panel/character_icon_{name.ToLowerInvariant()}.png";
        var formattedName = FormatCharacterName(character);

        string result;
        // ResourceLoader.Exists is cheap and cached internally.
        if (ResourceLoader.Exists(path))
            result = $"[img=28x28]{path}[/img] {formattedName}";
        else
            result = formattedName;

        _characterDisplayCache[character] = result;
        return result;
    }

    /// <summary>
    /// Builds a comma-separated filter context string for the footer.
    /// e.g. "A4-6, Defect, v0.100.0-v0.100.6, profile2"
    /// </summary>
    internal static string BuildFilterContext(string characterLabel, AggregationFilter filter)
    {
        var parts = new List<string>();

        // Always include the ascension range, even when unfiltered ("A0-10").
        parts.Add(FormatAscensionFooter(filter.AscensionMin, filter.AscensionMax));

        // When filtered to a single character, use the character's top-panel
        // head sprite + name. Falls back to the text label for "All characters"
        // or when the icon resource isn't available.
        var effectiveChar = GetEffectiveCharacter(filter);
        parts.Add(effectiveChar != null ? GetCharacterDisplay(effectiveChar) : characterLabel);

        if (filter.VersionMin != null || filter.VersionMax != null)
        {
            var vMin = filter.VersionMin ?? "";
            var vMax = filter.VersionMax ?? "";
            if (vMin.Length > 0 && vMax.Length > 0)
                parts.Add(vMin == vMax ? vMin : $"{vMin}-{vMax}");
            else if (vMin.Length > 0)
                parts.Add($"{vMin}+");
            else
                parts.Add($"≤{vMax}");
        }

        if (filter.Profile != null)
            parts.Add(filter.Profile);

        return parts.Count > 0 ? string.Join(", ", parts) : "";
    }

    /// <summary>
    /// Converts raw cfg ascension bounds (which use AscensionLowest = int.MinValue
    /// and AscensionHighest = int.MaxValue as sentinels for "lowest in data" /
    /// "highest in data") into the typed nullable bounds the AggregationFilter
    /// expects. Handles all sentinel combinations:
    ///
    ///   (Lowest,  Highest) → (null, null)        — full range, no constraint
    ///   (Lowest,  N)       → (null, N)           — bounded above only
    ///   (M,       Highest) → (M, null)           — bounded below only
    ///   (M,       N)       → (M, N)              — explicit range
    ///   (Lowest,  Lowest)  → (dataMin, dataMin)  — single concrete: lowest in data
    ///   (Highest, Highest) → (dataMax, dataMax)  — single concrete: highest in data
    ///   (Highest, N)       → (null, N)           — defensive: invalid combo treated as "no lower bound"
    ///   (M,       Lowest)  → (M, null)           — defensive: invalid combo treated as "no upper bound"
    ///
    /// The (Lowest, Lowest) and (Highest, Highest) resolutions are critical for
    /// two reasons: (a) leaving the raw int.MinValue/int.MaxValue in the filter
    /// breaks the data filter (every ascension is &gt; int.MinValue, so all
    /// records get rejected), and (b) the footer formatter would otherwise
    /// print the literal sentinel as "A-2147483648" or "A2147483647".
    /// </summary>
    internal static (int? min, int? max) ResolveAscensionBounds(int rawMin, int rawMax)
    {
        bool minIsLowest  = rawMin == SlayTheStatsConfig.AscensionLowest;
        bool minIsHighest = rawMin == SlayTheStatsConfig.AscensionHighest;
        bool maxIsLowest  = rawMax == SlayTheStatsConfig.AscensionLowest;
        bool maxIsHighest = rawMax == SlayTheStatsConfig.AscensionHighest;

        // (Lowest, Lowest) → resolve to "the lowest ascension actually in your data"
        if (minIsLowest && maxIsLowest)
        {
            var data = StatsAggregator.GetDistinctAscensions(MainFile.Db);
            int dataMin = data.Count > 0 ? data[0] : 0;
            return (dataMin, dataMin);
        }

        // (Highest, Highest) → resolve to "the highest ascension actually in your data"
        if (minIsHighest && maxIsHighest)
        {
            var data = StatsAggregator.GetDistinctAscensions(MainFile.Db);
            int dataMax = data.Count > 0 ? data[^1] : 10;
            return (dataMax, dataMax);
        }

        // Mixed sentinels and explicit values: convert each side independently.
        // Defensive: "min set to Highest" or "max set to Lowest" are nonsense
        // combos that the cross-clamp logic in the filter pane should prevent,
        // but if anything sneaks them through (corrupt cfg, etc.), treat them
        // as "no constraint on that side" rather than producing an empty range.
        int? min = (minIsLowest || minIsHighest) ? null : (int?)rawMin;
        int? max = (maxIsLowest || maxIsHighest) ? null : (int?)rawMax;
        return (min, max);
    }

    /// <summary>
    /// Footer-only ascension formatter — always returns a value, even when both
    /// bounds are unset. Sentinel/null bounds fall back to the actual ascension
    /// range present in the data so the footer reflects what's really being
    /// aggregated (and stays correct with mods that add negative or 11+
    /// ascensions). When the data is empty, falls back to A0-10.
    /// </summary>
    internal static string FormatAscensionFooter(int? ascMin, int? ascMax)
    {
        int lo, hi;
        if (ascMin == null || ascMax == null)
        {
            var data = StatsAggregator.GetDistinctAscensions(MainFile.Db);
            var dataLo = data.Count > 0 ? data[0]  : 0;
            var dataHi = data.Count > 0 ? data[^1] : 10;
            lo = ascMin ?? dataLo;
            hi = ascMax ?? dataHi;
        }
        else
        {
            lo = ascMin.Value;
            hi = ascMax.Value;
        }
        return lo == hi ? $"A{lo}" : $"A{lo}-{hi}";
    }

    internal static string FormatAscensionPrefix(int? ascMin, int? ascMax)
    {
        if (ascMin == null && ascMax == null) return "";
        if (ascMin != null && ascMax != null)
        {
            if (ascMin == ascMax) return $"A{ascMin} ";
            return $"A{ascMin}-{ascMax} ";
        }
        if (ascMin != null) return $"A{ascMin}+ ";
        return $"A0-{ascMax} ";
    }

    internal static string BuildStatsText(Dictionary<int, CardStat> actStats, double characterWR = 50.0, double pickRateBaseline = 100.0 / 3.0, string characterLabel = "All characters", int? ascensionMin = null, int? ascensionMax = null, bool showBuysLayout = false, double shopBuyRateBaseline = 20.0, AggregationFilter? filter = null)
    {
        var sb = new StringBuilder();

        if (showBuysLayout)
            return BuildBuysStatsText(actStats, characterWR, characterLabel, ascensionMin, ascensionMax, shopBuyRateBaseline, filter);

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

        var filterCtx = filter != null ? BuildFilterContext(characterLabel, filter) : "";
        var prBaseStr = $"{Math.Round(pickRateBaseline):F0}%";
        var wrStr     = $"{Math.Round(characterWR):F0}%";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]Baseline Pick% {prBaseStr}, Win% {wrStr}");
        if (filterCtx.Length > 0)
            sb.Append($"\n{filterCtx}");
        sb.Append("[/color][/font_size][/font]");

        return sb.ToString();
    }

    /// <summary>
    /// Colorless card layout: Act | Runs | Buys | Win%
    /// Runs shows RunsPresent only (no fight-reward denominator).
    /// Buys shows RunsShopBought/RunsShopSeen (purchases / shop appearances).
    /// Win% is placed last, consistent across all stat tables.
    /// </summary>
    private static string BuildBuysStatsText(Dictionary<int, CardStat> actStats, double characterWR, string characterLabel, int? ascensionMin, int? ascensionMax, double shopBuyRateBaseline, AggregationFilter? filter = null)
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

        var filterCtx    = filter != null ? BuildFilterContext(characterLabel, filter) : "";
        var wrStr        = $"{Math.Round(characterWR):F0}%";
        var buysBaseStr  = $"{Math.Round(shopBuyRateBaseline):F0}%";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]Baseline Buys {buysBaseStr}, Win% {wrStr}");
        if (filterCtx.Length > 0)
            sb.Append($"\n{filterCtx}");
        sb.Append("[/color][/font_size][/font]");

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
/// Safety net: when an NGridCardHolder is returned to the pool, clear any tooltip it was showing.
/// NGridCardHolder is used by NCardRewardSelectionScreen (mid-combat and end-of-fight rewards).
/// If the player was hovering a card in this holder when the screen closed — including during
/// abrupt closures where ClearHoverTips was never called — this patch clears the stuck state.
/// </summary>
[HarmonyPatch(typeof(NGridCardHolder), "OnFreedToPool")]
public static class GridCardHolderFreedToPoolPatch
{
    private static bool _warnedOnce;

    static void Postfix(NGridCardHolder __instance)
    {
        try
        {
            // HideTooltip is a no-op if __instance is not the active holder.
            MainFile.Logger.Info($"[SlayTheStats] GridCardHolderFreedToPool: clearing hover for holder (hasActiveHover={TooltipHelper.HasActiveHover})");
            CardHoverShowPatch.HideTooltip(__instance);
        }
        catch (Exception e)
        {
            if (!_warnedOnce) { MainFile.Logger.Warn($"SlayTheStats: GridCardHolderFreedToPoolPatch failed — {e.Message}"); _warnedOnce = true; }
        }
    }
}

/// <summary>
/// Belt-and-suspenders: hides the panel when any NCard is returned to the pool, but only
/// if the active card holder is no longer a valid Godot node. This catches non-poolable
/// holders (e.g. Godot-freed screens) where GridCardHolderFreedToPoolPatch doesn't fire.
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
            if (RelicHoverHelper.IsActiveHover()) return;
            var holder = CardHoverShowPatch.ActiveHolder;
            if (holder != null && GodotObject.IsInstanceValid(holder)) return;
            // Holder is null or Godot-freed — orphaned hover state, force-clear.
            MainFile.Logger.Info($"[SlayTheStats] CardFreedToPool: holder invalid, clearing stuck hover (hasActiveHover={TooltipHelper.HasActiveHover})");
            CardHoverShowPatch.HideTooltip();
        }
        catch (Exception e)
        {
            if (!_warnedOnce) { MainFile.Logger.Warn($"SlayTheStats: CardFreedToPoolPatch failed — {e.Message}"); _warnedOnce = true; }
        }
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
            CompendiumFilterPatch.HideAllPanes();
            if (SlayTheStatsConfig.DisableTooltipsEntirely) return;

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
            // Inspect screen is always compendium context.
            var groupUpgrades  = SlayTheStatsConfig.GroupCardUpgrades;
            var lookupId       = CardHoverShowPatch.FindCardLookupId(rawId, upgradeLevel, groupUpgrades);

            var cardOwner      = CardHoverShowPatch.GetOwningCharacter(model as CardModel);
            var inspFilter     = CardHoverShowPatch.BuildCompendiumFilter(CardHoverShowPatch.RunCharacter, cardOwner);
            var effectiveChar  = CardHoverShowPatch.GetEffectiveCharacter(inspFilter);
            var characterLabel = CardHoverShowPatch.GetCharacterLabel(inspFilter);

            string statsText;
            if (lookupId == null)
            {
                statsText = CardHoverShowPatch.NoDataText(inspFilter);
            }
            else
            {
                var showBuysLayout      = CardHoverShowPatch.IsColorlessCard(model as CardModel);
                var contextMap          = CardHoverShowPatch.GetContextMap(lookupId, groupUpgrades);
                var actStats            = StatsAggregator.AggregateByAct(contextMap, inspFilter);
                var characterWR         = effectiveChar != null ? StatsAggregator.GetCharacterWR(MainFile.Db, effectiveChar) : StatsAggregator.GetGlobalWR(MainFile.Db);
                var pickRateBaseline    = StatsAggregator.GetPickRateBaseline(MainFile.Db);
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db);
                statsText = actStats.Count == 0
                    ? CardHoverShowPatch.NoDataText(inspFilter)
                    : CardHoverShowPatch.BuildStatsText(actStats, characterWR, pickRateBaseline, characterLabel, inspFilter.AscensionMin, inspFilter.AscensionMax, showBuysLayout, shopBuyRateBaseline, inspFilter);
            }

            TooltipHelper.TrySceneTheftOnce();
            TooltipHelper.InspectActive = true;
            TooltipHelper.ShowPanel(statsText, card as Control);
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
            if (SlayTheStatsConfig.DisableTooltipsEntirely) return;
            if (!SlayTheStatsConfig.ShowInRunStats) return;

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
            // Shop is always in-run context — use saved defaults for group upgrades.
            var groupUpgrades  = SlayTheStatsConfig.DefaultGroupCardUpgrades;
            var lookupId       = CardHoverShowPatch.FindCardLookupId(rawId, upgradeLevel, groupUpgrades);

            var filter         = CardHoverShowPatch.BuildInRunFilter(CardHoverShowPatch.RunCharacter);
            var effectiveChar  = CardHoverShowPatch.GetEffectiveCharacter(filter);
            var characterLabel = CardHoverShowPatch.GetCharacterLabel(filter);

            string statsText;
            if (lookupId == null)
            {
                statsText = CardHoverShowPatch.NoDataText(filter);
            }
            else
            {
                // In the shop, always use the Runs/Buys layout regardless of card class
                var contextMap          = CardHoverShowPatch.GetContextMap(lookupId, groupUpgrades);
                var actStats            = StatsAggregator.AggregateByAct(contextMap, filter);
                var characterWR         = effectiveChar != null ? StatsAggregator.GetCharacterWR(MainFile.Db, effectiveChar) : StatsAggregator.GetGlobalWR(MainFile.Db);
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db);
                statsText = actStats.Count == 0
                    ? CardHoverShowPatch.NoDataText(filter)
                    : CardHoverShowPatch.BuildStatsText(actStats, characterWR, 0, characterLabel, filter.AscensionMin, filter.AscensionMax, showBuysLayout: true, shopBuyRateBaseline, filter);
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
