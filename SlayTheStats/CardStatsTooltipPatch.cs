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
                var characterWR         = effectiveChar != null ? StatsAggregator.GetCharacterWR(MainFile.Db, effectiveChar, filter: filter) : StatsAggregator.GetGlobalWR(MainFile.Db, filter: filter);
                var pickRateBaseline    = StatsAggregator.GetPickRateBaseline(MainFile.Db, filter);
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db, filter);
                statsText = actStats.Count == 0 ? NoDataText(filter) : BuildStatsText(actStats, characterWR, pickRateBaseline, characterLabel, filter.AscensionMin, filter.AscensionMax, showBuysLayout, shopBuyRateBaseline, filter, isCompendium);
            }

            TooltipHelper.TrySceneTheftOnce();
            _activeHolder = __instance;
            float? widthOverride = (isCompendium && SlayTheStatsConfig.ShowExperimentalInsights) ? 740f : null;
            TooltipHelper.ShowPanel(statsText, __instance as Control, widthOverride);
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
                    PickedWon         = existing.PickedWon         + stat.PickedWon,
                    OfferedWon        = existing.OfferedWon        + stat.OfferedWon,
                    OfferedSkipped    = existing.OfferedSkipped    + stat.OfferedSkipped,
                    OfferedSkippedWon = existing.OfferedSkippedWon + stat.OfferedSkippedWon,
                    RunsEverPresent   = existing.RunsEverPresent   + stat.RunsEverPresent,
                    RunsEverWon       = existing.RunsEverWon       + stat.RunsEverWon,
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
        var entry = character.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
            ? character.Substring("CHARACTER.".Length)
            : character;
        if (entry.Length == 0) return character;
        // Fallback: title-cased entry (e.g. "IRONCLAD" → "Ironclad"). Used
        // if the game's "characters" loc table has no entry for this ID
        // (modded characters that didn't register a titleObject string).
        var fallback = char.ToUpper(entry[0]) + entry.Substring(1).ToLower();
        return L.CharacterName(entry, fallback);
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
        var (ascMin, ascMax) = SlayTheStatsConfig.ResolveAscensionBounds(SlayTheStatsConfig.AscensionMin, SlayTheStatsConfig.AscensionMax);
        var filter = new AggregationFilter
        {
            GameMode = SlayTheStatsConfig.IncludeMultiplayer ? null! : "standard",
            AscensionMin = ascMin,
            AscensionMax = ascMax,
            VersionMin = IsVersionLowerBound(SlayTheStatsConfig.VersionMin) ? null : SlayTheStatsConfig.VersionMin,
            VersionMax = IsVersionUpperBound(SlayTheStatsConfig.VersionMax) ? null : SlayTheStatsConfig.VersionMax,
            Profile = string.IsNullOrEmpty(SlayTheStatsConfig.FilterProfile) ? null : SlayTheStatsConfig.FilterProfile,
            Display = new FilterDisplayRaw
            {
                RawAscMin  = SlayTheStatsConfig.AscensionMin,
                RawAscMax  = SlayTheStatsConfig.AscensionMax,
                RawVerMin  = SlayTheStatsConfig.VersionMin  ?? "",
                RawVerMax  = SlayTheStatsConfig.VersionMax  ?? "",
                RawProfile = SlayTheStatsConfig.FilterProfile ?? "",
            },
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
        var (ascMin, ascMax) = SlayTheStatsConfig.ResolveAscensionBounds(SlayTheStatsConfig.DefaultAscensionMin, SlayTheStatsConfig.DefaultAscensionMax);
        var filter = new AggregationFilter
        {
            GameMode = SlayTheStatsConfig.DefaultIncludeMultiplayer ? null! : "standard",
            Character = runCharacter,
            AscensionMin = ascMin,
            AscensionMax = ascMax,
            VersionMin = IsVersionLowerBound(SlayTheStatsConfig.DefaultVersionMin) ? null : SlayTheStatsConfig.DefaultVersionMin,
            VersionMax = IsVersionUpperBound(SlayTheStatsConfig.DefaultVersionMax) ? null : SlayTheStatsConfig.DefaultVersionMax,
            Profile = string.IsNullOrEmpty(SlayTheStatsConfig.DefaultFilterProfile) ? null : SlayTheStatsConfig.DefaultFilterProfile,
            Display = new FilterDisplayRaw
            {
                RawAscMin  = SlayTheStatsConfig.DefaultAscensionMin,
                RawAscMax  = SlayTheStatsConfig.DefaultAscensionMax,
                RawVerMin  = SlayTheStatsConfig.DefaultVersionMin  ?? "",
                RawVerMax  = SlayTheStatsConfig.DefaultVersionMax  ?? "",
                RawProfile = SlayTheStatsConfig.DefaultFilterProfile ?? "",
            },
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
        sb.Append($"[color={TooltipHelper.NeutralShade}]{L.T("tooltip.no_data")}[/color]");
        sb.Append(TooltipHelper.FormatFooter(filterCtx));
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
        return ch != null ? FormatCharacterName(ch) : L.T("filter.all_characters");
    }

    // Cache of per-character display BBCode (icon + name, or plain text
    // fallback) so we don't re-check the resource path on every tooltip
    // repaint. Keyed by character ID (e.g. "CHARACTER.IRONCLAD"). Invalidated
    // on locale change via <see cref="ClearCharacterDisplayCache"/> so the
    // cached name (embedded in the BBCode string) refreshes to the new locale.
    private static readonly Dictionary<string, string> _characterDisplayCache = new();

    /// <summary>Invalidate <see cref="_characterDisplayCache"/>. Called from
    /// <see cref="TooltipHelper.OnLocaleChanged"/> so the next tooltip rebuild
    /// re-resolves character names in the new locale.</summary>
    internal static void ClearCharacterDisplayCache() => _characterDisplayCache.Clear();

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
    /// Builds a filter context string for the footer. Reads the raw
    /// (sentinel-preserving) config values off
    /// <see cref="AggregationFilter.Display"/> so the formatter can
    /// distinguish sentinels from explicit values — the filter's own fields
    /// are post-sanitisation and lose that distinction.
    ///
    /// Format:
    ///   <c>A2-10 · Ironclad · v0.5-v0.6 · profile1</c>
    ///
    /// Segments are separated by <c> · </c> (U+00B7 with surrounding
    /// spaces). Some segments collapse or disappear per the per-field rules
    /// below.
    ///
    /// <b>Ascension:</b>
    /// <list type="bullet">
    /// <item>low == high → <c>A&lt;val&gt;</c> (e.g. "A4")</item>
    /// <item>high is Highest sentinel → <c>A&lt;low&gt;+</c> (e.g. "A2+")</item>
    /// <item>otherwise → <c>A&lt;low&gt;-&lt;high&gt;</c> (e.g. "A2-10")</item>
    /// </list>
    ///
    /// <b>Version:</b>
    /// <list type="number">
    /// <item>Both ends resolve to the current STS2 version (= data's highest): <c>current version</c></item>
    /// <item>Low end is Highest sentinel: <c>&lt;high&gt;</c> (resolved high version, no "+" suffix)</item>
    /// <item>High end is Lowest sentinel: <c>&lt;low&gt;</c> (resolved low version, no "≤" prefix)</item>
    /// <item>Low end is Lowest sentinel and high end is Highest: segment omitted entirely</item>
    /// <item>Low end is Lowest sentinel (but high concrete): <c>≤&lt;high&gt;</c></item>
    /// <item>High end is Highest sentinel (but low concrete): <c>&lt;low&gt;+</c></item>
    /// <item>low == high (both concrete): single version string</item>
    /// <item>otherwise: <c>&lt;low&gt;-&lt;high&gt;</c></item>
    /// </list>
    ///
    /// <b>Profile:</b> omitted when empty or "all".
    /// </summary>
    internal static string BuildFilterContext(string characterLabel, AggregationFilter filter, bool includeCharacter = true)
    {
        var parts = new List<string>();

        // Ascension — always shown (the user didn't list any "omit" rule for asc).
        parts.Add(FormatAscensionContextV2(filter.Display.RawAscMin, filter.Display.RawAscMax));

        // Character — omitted entirely when includeCharacter is false (the focused
        // bestiary view shows the character context per-row via icons, so the footer
        // can drop the redundant segment).
        if (includeCharacter)
        {
            var effectiveChar = GetEffectiveCharacter(filter);
            parts.Add(effectiveChar != null ? GetCharacterDisplay(effectiveChar) : characterLabel);
        }

        // Version — may be omitted entirely (both sentinels unbounded).
        var verPart = FormatVersionContextV2(filter.Display.RawVerMin, filter.Display.RawVerMax);
        if (verPart != null) parts.Add(verPart);

        // Profile — omitted when empty / "all".
        var profile = filter.Display.RawProfile;
        if (!string.IsNullOrEmpty(profile) &&
            !string.Equals(profile, "all", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(profile);
        }

        // Interpunct separator with surrounding spaces.
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Formats the ascension segment per the new footer rules. See the
    /// Ascension bullet list on <see cref="BuildFilterContext"/>.
    ///
    /// The displayed high side is also clamped to the data's actual
    /// ceiling: if the user's filter allows A0-20 but they've only played
    /// up to A10, we show "A0-10" rather than the misleading "A0-20".
    /// As their data expands into higher ascensions (game extensions or
    /// mods) the displayed max grows with it automatically. The clamping
    /// is display-only; the matching-side bounds on
    /// <see cref="AggregationFilter"/> are unaffected.
    /// </summary>
    private static string FormatAscensionContextV2(int rawMin, int rawMax)
    {
        bool minIsLowest  = rawMin == SlayTheStatsConfig.AscensionLowest;
        bool minIsHighest = rawMin == SlayTheStatsConfig.AscensionHighest;
        bool maxIsLowest  = rawMax == SlayTheStatsConfig.AscensionLowest;
        bool maxIsHighest = rawMax == SlayTheStatsConfig.AscensionHighest;

        // Always look up the data's actual ascension range — needed both
        // for sentinel resolution AND for the display-side clamp below.
        // Falls back to A0-20 if the db has no runs (fresh install).
        int dataMin = 0, dataMax = 20;
        try
        {
            var ascs = StatsAggregator.GetDistinctAscensions(MainFile.Db);
            if (ascs.Count > 0) { dataMin = ascs[0]; dataMax = ascs[^1]; }
        }
        catch { /* keep defaults */ }

        int resolvedMin = minIsLowest ? dataMin : (minIsHighest ? dataMax : rawMin);
        int resolvedMax = maxIsHighest ? dataMax : (maxIsLowest ? dataMin : rawMax);

        // Display-side clamp: don't show a max higher than what's actually
        // in the data. Honors explicit lower maxes (if the user sets max=5
        // and data goes to 10, show 5 — but never the other way around).
        if (resolvedMax > dataMax) resolvedMax = dataMax;

        // If the user's min is above the (possibly clamped) max, the range
        // is degenerate (e.g. filter A11-20, data only A0-10 → displayed
        // max clamped to 10, but min stays at 11). Show the min alone so
        // the footer reflects the user's intent without rendering a
        // nonsensical "A11-10".
        if (resolvedMin > resolvedMax) return $"A{resolvedMin}";

        // Rule: low == high → single value.
        if (resolvedMin == resolvedMax) return $"A{resolvedMin}";

        // Rule: high is Highest sentinel AND the clamp hasn't pulled it
        // below the data's ceiling → "A<low>+". Once the clamp kicks in
        // we prefer the explicit range so the user can see the actual
        // high of their data.
        if (maxIsHighest && resolvedMax == dataMax) return $"A{resolvedMin}+";

        return $"A{resolvedMin}-{resolvedMax}";
    }

    /// <summary>
    /// Formats the version segment per the new footer rules. Returns null
    /// to skip the segment entirely (both ends unbounded sentinels).
    /// See the Version numbered list on <see cref="BuildFilterContext"/>.
    /// </summary>
    private static string? FormatVersionContextV2(string rawMin, string rawMax)
    {
        string Lowest  = SlayTheStatsConfig.VersionLowest;   // "__lowest__"
        string Highest = SlayTheStatsConfig.VersionHighest;  // "__highest__"

        bool minIsLowest  = string.IsNullOrEmpty(rawMin) || rawMin == Lowest;
        bool minIsHighest = rawMin == Highest;
        bool maxIsLowest  = rawMax == Lowest;
        bool maxIsHighest = string.IsNullOrEmpty(rawMax) || rawMax == Highest;

        // Resolve sentinels to concrete versions from the data. Each side
        // resolves independently: a "Lowest" sentinel becomes the data's
        // low watermark, a "Highest" sentinel becomes the data's high
        // watermark (regardless of whether it's on the min or max side).
        // The highest concrete version in data is treated as "current STS2
        // version" for rule 1.
        string resolvedMin = "", resolvedMax = "";
        if (minIsLowest || minIsHighest || maxIsLowest || maxIsHighest)
        {
            try
            {
                var versions = StatsAggregator.GetDistinctVersions(MainFile.Db);
                if (versions.Count > 0)
                {
                    string dataLow  = versions[0];
                    string dataHigh = versions[^1];
                    if (minIsLowest)       resolvedMin = dataLow;
                    else if (minIsHighest) resolvedMin = dataHigh;
                    if (maxIsLowest)       resolvedMax = dataLow;
                    else if (maxIsHighest) resolvedMax = dataHigh;
                }
            }
            catch { /* resolvedMin/Max stay "" */ }
        }
        if (!minIsLowest && !minIsHighest) resolvedMin = rawMin;
        if (!maxIsLowest && !maxIsHighest) resolvedMax = rawMax;

        // Rule 4 (order-shifted to the front): both ends unbounded → no segment.
        if (minIsLowest && maxIsHighest) return null;

        // Rule 1: both resolve to the current version → "current version".
        // Current version ≡ data's highest concrete version. Only meaningful
        // when we actually have data.
        string currentVer = "";
        try
        {
            var versions = StatsAggregator.GetDistinctVersions(MainFile.Db);
            if (versions.Count > 0) currentVer = versions[^1];
        }
        catch { }
        if (!string.IsNullOrEmpty(currentVer) &&
            resolvedMin == currentVer && resolvedMax == currentVer)
        {
            return "current version";
        }

        // Rule 2: low end is Highest sentinel → "<high>" (no "+" — the
        // range is pinned at the high version).
        if (minIsHighest) return resolvedMax;

        // Rule 3: high end is Lowest sentinel → "<low>" (no "≤" — the
        // range is pinned at the low version).
        if (maxIsLowest) return resolvedMin;

        // Rule 5: low end is Lowest (high concrete) → "≤<high>".
        if (minIsLowest)
        {
            return $"\u2264{resolvedMax}";
        }

        // Rule 6: high end is Highest (low concrete) → "<low>+".
        if (maxIsHighest) return $"{resolvedMin}+";

        // Both concrete.
        if (rawMin == rawMax) return rawMin;
        return $"{rawMin}-{rawMax}";
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

    internal static string BuildStatsText(Dictionary<int, CardStat> actStats, double characterWR, double pickRateBaseline, string characterLabel, int? ascensionMin = null, int? ascensionMax = null, bool showBuysLayout = false, double shopBuyRateBaseline = 20.0, AggregationFilter? filter = null, bool isCompendium = false)
    {
        var sb = new StringBuilder();

        if (showBuysLayout)
            return BuildBuysStatsText(actStats, characterWR, characterLabel, ascensionMin, ascensionMax, shopBuyRateBaseline, filter, isCompendium);

        // Class card columns: Act | Runs (present/offered) | Pick% | Win%
        sb.Append("[table=4]");
        sb.Append(TooltipHelper.HdrCell(L.T("tooltip.col.act"), TooltipHelper.ColPadOuter));
        sb.Append(TooltipHelper.HdrCell(L.T("tooltip.col.runs"), TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell(L.T("tooltip.col.pick_pct"), TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell(L.T("tooltip.col.win_pct"), TooltipHelper.ColPadLast));

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

                var cNum  = TooltipHelper.ColN($"{stat.RunsPresent}", stat.RunsPresent);
                var cPr   = prPct >= 0 ? TooltipHelper.ColPR(pr, prPct, stat.RunsOffered, pickRateBaseline) : $"[color={TooltipHelper.NeutralShade}]-[/color]";
                var cWr   = wrPct >= 0 ? TooltipHelper.ColWR(wr, wrPct, stat.RunsPresent, characterWR) : $"[color={TooltipHelper.NeutralShade}]-[/color]";

                sb.Append(TooltipHelper.DataCell($"{act}", TooltipHelper.ColPadOuter));
                sb.Append(TooltipHelper.FractionCell(cNum, $"{stat.RunsOffered}", TooltipHelper.ColPadInner));
                sb.Append(TooltipHelper.DataCell(cPr, TooltipHelper.ColPadInner));
                sb.Append(TooltipHelper.DataCell(cWr, TooltipHelper.ColPadLast));
            }
            else
            {
                sb.Append(TooltipHelper.DataCell($"{act}", TooltipHelper.ColPadOuter));
                sb.Append(TooltipHelper.EmptyCell(TooltipHelper.ColPadInner));
                sb.Append(TooltipHelper.EmptyCell(TooltipHelper.ColPadInner));
                sb.Append(TooltipHelper.EmptyCell(TooltipHelper.ColPadLast));
            }
        }

        // Total row — aggregated across all acts
        var totPrPct  = totOffered > 0 ? 100.0 * totPicked  / totOffered : -1;
        var totWrPct  = totPresent > 0 ? 100.0 * totWon     / totPresent : -1;
        var totPr     = totPrPct >= 0 ? $"{Math.Round(totPrPct):F0}%" : "-";
        var totWr     = totWrPct >= 0 ? $"{Math.Round(totWrPct):F0}%" : "-";
        var cTotNum   = TooltipHelper.ColN($"{totPresent}", totPresent);
        var cTotPr    = totPrPct >= 0 ? TooltipHelper.ColPR(totPr, totPrPct, totOffered, pickRateBaseline) : $"[color={TooltipHelper.NeutralShade}]-[/color]";
        var cTotWr    = totWrPct >= 0 ? TooltipHelper.ColWR(totWr, totWrPct, totPresent, characterWR) : $"[color={TooltipHelper.NeutralShade}]-[/color]";
        sb.Append(TooltipHelper.DataCell(TooltipHelper.TotalLabel(), TooltipHelper.ColPadOuter));
        sb.Append(TooltipHelper.FractionCell(cTotNum, $"{totOffered}", TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(cTotPr, TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(cTotWr, TooltipHelper.ColPadLast));

        sb.Append("[/table]");

        if (isCompendium && SlayTheStatsConfig.ShowExperimentalInsights)
            sb.Append(BuildExperimentalCardSubTable(actStats, characterWR));

        // Baseline line below the table (plain text — avoids in-table column overflow).
        // NaN baselines (filter matched zero runs/contexts) render as "—".
        // Baseline character is already implied by the filter-context footer (which
        // renders the active character icon + name), so the baseline line itself
        // doesn't need a "character" marker — just "(baseline)" is enough.
        var prBaseStr = double.IsNaN(pickRateBaseline) ? "—" : $"{Math.Round(pickRateBaseline):F0}%";
        var wrStr     = double.IsNaN(characterWR)     ? "—" : $"{Math.Round(characterWR):F0}%";
        var baselineText = L.T("tooltip.baseline.pick", ("pick", prBaseStr), ("win", wrStr));
        sb.Append(TooltipHelper.FormatBaselineLine(baselineText));

        var filterCtx = filter != null ? BuildFilterContext(characterLabel, filter) : "";
        sb.Append(TooltipHelper.FormatFooter(filterCtx));

        return sb.ToString();
    }

    /// <summary>
    /// Colorless card layout: Act | Runs | Buys | Win%
    /// Runs shows RunsPresent only (no fight-reward denominator).
    /// Buys shows RunsShopBought/RunsShopSeen (purchases / shop appearances).
    /// Win% is placed last, consistent across all stat tables.
    /// </summary>
    private static string BuildBuysStatsText(Dictionary<int, CardStat> actStats, double characterWR, string characterLabel, int? ascensionMin, int? ascensionMax, double shopBuyRateBaseline, AggregationFilter? filter = null, bool isCompendium = false)
    {
        var sb = new StringBuilder();
        // Columns: Act | Runs | Buys (bought/seen) | Win%
        sb.Append("[table=4]");
        sb.Append(TooltipHelper.HdrCell(L.T("tooltip.col.act"), TooltipHelper.ColPadOuter));
        sb.Append(TooltipHelper.HdrCell(L.T("tooltip.col.runs"), TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell(L.T("tooltip.col.buys"), TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell(L.T("tooltip.col.win_pct"), TooltipHelper.ColPadLast));

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

                var cRuns = TooltipHelper.ColN($"{stat.RunsPresent}", stat.RunsPresent);
                var cWr   = wrPct >= 0 ? TooltipHelper.ColWR(wr, wrPct, stat.RunsPresent, characterWR) : $"[color={TooltipHelper.NeutralShade}]-[/color]";

                sb.Append(TooltipHelper.DataCell($"{act}", TooltipHelper.ColPadOuter));
                sb.Append(TooltipHelper.DataCell(cRuns, TooltipHelper.ColPadInner));
                sb.Append(FormatBuysFractionCell(stat.RunsShopBought, stat.RunsShopSeen, buysPct, shopBuyRateBaseline, TooltipHelper.ColPadInner));
                sb.Append(TooltipHelper.DataCell(cWr, TooltipHelper.ColPadLast));
            }
            else
            {
                sb.Append(TooltipHelper.DataCell($"{act}", TooltipHelper.ColPadOuter));
                sb.Append(TooltipHelper.EmptyCell(TooltipHelper.ColPadInner));
                sb.Append(TooltipHelper.EmptyCell(TooltipHelper.ColPadInner));
                sb.Append(TooltipHelper.EmptyCell(TooltipHelper.ColPadLast));
            }
        }

        var totWrPct   = totPresent  > 0 ? 100.0 * totWon        / totPresent  : -1;
        var totBuysPct = totShopSeen > 0 ? 100.0 * totShopBought / totShopSeen : -1;
        var totWr      = totWrPct >= 0 ? $"{Math.Round(totWrPct):F0}%" : "-";

        var cTotRuns = TooltipHelper.ColN($"{totPresent}", totPresent);
        var cTotWr   = totWrPct >= 0 ? TooltipHelper.ColWR(totWr, totWrPct, totPresent, characterWR) : $"[color={TooltipHelper.NeutralShade}]-[/color]";
        sb.Append(TooltipHelper.DataCell(TooltipHelper.TotalLabel(), TooltipHelper.ColPadOuter));
        sb.Append(TooltipHelper.DataCell(cTotRuns, TooltipHelper.ColPadInner));
        sb.Append(FormatBuysFractionCell(totShopBought, totShopSeen, totBuysPct, shopBuyRateBaseline, TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(cTotWr, TooltipHelper.ColPadLast));

        sb.Append("[/table]");

        if (isCompendium && SlayTheStatsConfig.ShowExperimentalInsights)
            sb.Append(BuildExperimentalCardSubTable(actStats, characterWR));

        // Baseline line below the table (plain text — avoids in-table column overflow).
        // NaN baselines (filter matched zero runs/contexts) render as "—".
        var wrStr        = double.IsNaN(characterWR)         ? "—" : $"{Math.Round(characterWR):F0}%";
        var buysBaseStr  = double.IsNaN(shopBuyRateBaseline) ? "—" : $"{Math.Round(shopBuyRateBaseline):F0}%";
        var baselineText = L.T("tooltip.baseline.buys", ("buys", buysBaseStr), ("win", wrStr));
        sb.Append(TooltipHelper.FormatBaselineLine(baselineText));

        var filterCtx    = filter != null ? BuildFilterContext(characterLabel, filter) : "";
        sb.Append(TooltipHelper.FormatFooter(filterCtx));

        return sb.ToString();
    }

    /// <summary>
    /// Formats a Buys cell as "bought/seen" inside a [cell] tag,
    /// with the numerator colored by buy-rate significance. Returns neutral "-" when no data.
    /// </summary>
    internal static string FormatBuysFractionCell(int bought, int seen, double pct, double baseline, string padding)
    {
        if (pct < 0)
            return TooltipHelper.EmptyCell(padding);
        var cNum = TooltipHelper.ColBuys($"{bought}", pct, seen, baseline);
        return TooltipHelper.FractionCell(cNum, $"{seen}", padding);
    }

    /// <summary>Legacy wrapper — kept for any external callers during migration.</summary>
    internal static string FormatBuysCell(int bought, int seen, double pct, double baseline)
        => FormatBuysFractionCell(bought, seen, pct, baseline, TooltipHelper.ColPadInner);

    // ─── Experimental insights sub-table (cf. insights.md #1, #2) ───
    //
    // Layout: Act | Pick% | vs Alt% | Δ | vs Skip% | Δ
    //   Pick%   = WinRatePicked          = picked_won / picked
    //   vs Alt% = WinRateOfferedNotPicked = (offered_won − picked_won) / (offered − picked)
    //   Δ (alt) = Pick% − vs Alt%   ← within-offer delta (technique #1)
    //   vs Skip%= WinRateSkipped        = offered_skipped_won / offered_skipped
    //   Δ (skip)= Pick% − vs Skip%  ← skip-as-control delta (technique #2)
    //
    // Existing main table is left untouched — coloration in this sub-table only.
    // Absolute Win% cells color-code vs the character WR baseline using ColWR.
    // Δ cells color-code vs a 0 baseline (positive = good = green, negative = bad).

    internal static string BuildExperimentalCardSubTable(Dictionary<int, CardStat> actStats, double characterWR)
    {
        var sb = new StringBuilder();
        sb.Append($"\n[font_size=11][color={TooltipHelper.NeutralShade}]experimental[/color][/font_size]");

        sb.Append("[table=9]");
        sb.Append(TooltipHelper.HdrCell("Act",   TooltipHelper.ColPadOuter));
        sb.Append(TooltipHelper.HdrCell("N",     TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Pick",  TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Alt",   TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Δ",     TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Skip",  TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Δ",     TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Ever",  TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Δ",     TooltipHelper.ColPadLast));

        var total = new CardStat();
        for (int act = 1; act <= 3; act++)
        {
            sb.Append(TooltipHelper.DataCell($"{act}", TooltipHelper.ColPadOuter));
            if (actStats.TryGetValue(act, out var stat) && stat.Picked > 0)
            {
                AccumulateExpStat(total, stat);
                sb.Append(BuildExpCardRow(stat, characterWR));
            }
            else
            {
                AppendEmptyExpCells(sb);
            }
        }

        sb.Append(TooltipHelper.DataCell(TooltipHelper.TotalLabel(), TooltipHelper.ColPadOuter));
        if (total.Picked > 0)
            sb.Append(BuildExpCardRow(total, characterWR));
        else
            AppendEmptyExpCells(sb);

        sb.Append("[/table]");
        return sb.ToString();
    }

    internal static string BuildExperimentalRelicSubTable(Dictionary<int, RelicStat> actStats, double wrBaseline)
    {
        var sb = new StringBuilder();
        sb.Append($"\n[font_size=11][color={TooltipHelper.NeutralShade}]experimental[/color][/font_size]");

        sb.Append("[table=9]");
        sb.Append(TooltipHelper.HdrCell("Act",   TooltipHelper.ColPadOuter));
        sb.Append(TooltipHelper.HdrCell("N",     TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Pick",  TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Alt",   TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Δ",     TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Skip",  TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Δ",     TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Ever",  TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.HdrCell("Δ",     TooltipHelper.ColPadLast));

        var total = new RelicStat();
        for (int act = 1; act <= 3; act++)
        {
            sb.Append(TooltipHelper.DataCell($"{act}", TooltipHelper.ColPadOuter));
            if (actStats.TryGetValue(act, out var stat) && stat.Picked > 0)
            {
                AccumulateExpStat(total, stat);
                sb.Append(BuildExpRelicRow(stat, wrBaseline));
            }
            else
            {
                AppendEmptyExpCells(sb);
            }
        }

        sb.Append(TooltipHelper.DataCell(TooltipHelper.TotalLabel(), TooltipHelper.ColPadOuter));
        if (total.Picked > 0)
            sb.Append(BuildExpRelicRow(total, wrBaseline));
        else
            AppendEmptyExpCells(sb);

        sb.Append("[/table]");
        return sb.ToString();
    }

    private static void AccumulateExpStat(CardStat dst, CardStat src)
    {
        dst.Offered           += src.Offered;
        dst.Picked            += src.Picked;
        dst.PickedWon         += src.PickedWon;
        dst.OfferedWon        += src.OfferedWon;
        dst.OfferedSkipped    += src.OfferedSkipped;
        dst.OfferedSkippedWon += src.OfferedSkippedWon;
        dst.RunsPresent       += src.RunsPresent;
        dst.RunsWon           += src.RunsWon;
        dst.RunsEverPresent   += src.RunsEverPresent;
        dst.RunsEverWon       += src.RunsEverWon;
    }

    private static void AccumulateExpStat(RelicStat dst, RelicStat src)
    {
        dst.Offered           += src.Offered;
        dst.Picked            += src.Picked;
        dst.PickedWon         += src.PickedWon;
        dst.OfferedWon        += src.OfferedWon;
        dst.OfferedSkipped    += src.OfferedSkipped;
        dst.OfferedSkippedWon += src.OfferedSkippedWon;
        dst.RunsPresent       += src.RunsPresent;
        dst.RunsWon           += src.RunsWon;
        dst.RunsEverPresent   += src.RunsEverPresent;
        dst.RunsEverWon       += src.RunsEverWon;
    }

    private static string BuildExpCardRow(CardStat s, double wrBaseline)
    {
        var sb = new StringBuilder();
        var pickPct    = StatsAggregator.WinRatePicked(s);
        var notPickPct = StatsAggregator.WinRateOfferedNotPicked(s);
        var skipPct    = StatsAggregator.WinRateSkipped(s);
        var dAlt       = StatsAggregator.WithinOfferDelta(s);
        var dSkip      = StatsAggregator.SkipDelta(s);
        var finalPct   = s.RunsPresent     > 0 ? 100.0 * s.RunsWon     / s.RunsPresent     : double.NaN;
        var everPct    = s.RunsEverPresent > 0 ? 100.0 * s.RunsEverWon / s.RunsEverPresent : double.NaN;
        var dEver      = everPct - finalPct;

        int alts = s.Offered - s.Picked - s.OfferedSkipped;
        sb.Append(TooltipHelper.DataCell(FormatExpNCell(s.Picked, alts, s.OfferedSkipped, s.Offered), TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpPctCell(pickPct, s.Picked, wrBaseline),  TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpPlainPctCell(notPickPct),                TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpDeltaCell(dAlt, s.Picked),               TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpPlainPctCell(skipPct),                   TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpDeltaCell(dSkip, s.Picked),              TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpPctCell(everPct, s.RunsEverPresent, wrBaseline), TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpDeltaCell(dEver, s.RunsEverPresent),     TooltipHelper.ColPadLast));
        return sb.ToString();
    }

    private static string BuildExpRelicRow(RelicStat s, double wrBaseline)
    {
        var sb = new StringBuilder();
        var pickPct    = StatsAggregator.WinRatePicked(s);
        var notPickPct = StatsAggregator.WinRateOfferedNotPicked(s);
        var skipPct    = StatsAggregator.WinRateSkipped(s);
        var dAlt       = StatsAggregator.WithinOfferDelta(s);
        var dSkip      = StatsAggregator.SkipDelta(s);
        var finalPct   = s.RunsPresent     > 0 ? 100.0 * s.RunsWon     / s.RunsPresent     : double.NaN;
        var everPct    = s.RunsEverPresent > 0 ? 100.0 * s.RunsEverWon / s.RunsEverPresent : double.NaN;
        var dEver      = everPct - finalPct;

        int alts = s.Offered - s.Picked - s.OfferedSkipped;
        sb.Append(TooltipHelper.DataCell(FormatExpNCell(s.Picked, alts, s.OfferedSkipped, s.Offered), TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpPctCell(pickPct, s.Picked, wrBaseline),  TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpPlainPctCell(notPickPct),                TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpDeltaCell(dAlt, s.Picked),               TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpPlainPctCell(skipPct),                   TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpDeltaCell(dSkip, s.Picked),              TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpPctCell(everPct, s.RunsEverPresent, wrBaseline), TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.DataCell(FormatExpDeltaCell(dEver, s.RunsEverPresent),     TooltipHelper.ColPadLast));
        return sb.ToString();
    }

    // N column for the experimental sub-table — packs the four event-level
    // counts that drive each adjacent column. The three event buckets are disjoint
    // so picks + alts + skips = offered:
    //   picks  — picked X
    //   alts   — picked a different card from the same offer (Offered − Picked − OfferedSkipped)
    //   skips  — screen-skipped the entire reward (OfferedSkipped)
    //   offered — Offered (sum of the three)
    // Picks colored by sample-size brightness via ColN; remaining counts neutral
    // so the eye lands on the binding number first.
    private static string FormatExpNCell(int picks, int alts, int skips, int offered)
    {
        var sep = $"[color={TooltipHelper.NeutralShade}]/[/color]";
        var pickStr = TooltipHelper.ColN($"{picks}", picks);
        var rest = $"[color={TooltipHelper.NeutralShade}]{alts}[/color]{sep}[color={TooltipHelper.NeutralShade}]{skips}[/color]{sep}[color={TooltipHelper.NeutralShade}]{offered}[/color]";
        return $"{pickStr}{sep}{rest}";
    }

    private static void AppendEmptyExpCells(StringBuilder sb)
    {
        for (int i = 0; i < 7; i++) sb.Append(TooltipHelper.EmptyCell(TooltipHelper.ColPadInner));
        sb.Append(TooltipHelper.EmptyCell(TooltipHelper.ColPadLast));
    }

    private static string FormatExpPctCell(double pct, int n, double baseline)
    {
        if (double.IsNaN(pct))
            return $"[color={TooltipHelper.NeutralShade}]-[/color]";
        var text = $"{Math.Round(pct):F0}%";
        return TooltipHelper.ColWR(text, pct, n, baseline);
    }

    // Uncolored Win% — used for Alt and Skip columns where the underlying runs
    // didn't have this card in play. Win/loss there says nothing about *this card's*
    // performance, so significance coloration would mislead. Plain default text.
    private static string FormatExpPlainPctCell(double pct)
    {
        if (double.IsNaN(pct))
            return $"[color={TooltipHelper.NeutralShade}]-[/color]";
        return $"{Math.Round(pct):F0}%";
    }

    private static string FormatExpDeltaCell(double delta, int n)
    {
        if (double.IsNaN(delta))
            return $"[color={TooltipHelper.NeutralShade}]-[/color]";
        var rounded = Math.Round(delta);
        var text = rounded >= 0 ? $"+{rounded:F0}" : $"{rounded:F0}";
        // ColWR on a 0 baseline: positive → green, negative → red, magnitude × n drives intensity.
        return TooltipHelper.ColWR(text, delta, n, 0.0);
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
                var characterWR         = effectiveChar != null ? StatsAggregator.GetCharacterWR(MainFile.Db, effectiveChar, filter: inspFilter) : StatsAggregator.GetGlobalWR(MainFile.Db, filter: inspFilter);
                var pickRateBaseline    = StatsAggregator.GetPickRateBaseline(MainFile.Db, inspFilter);
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db, inspFilter);
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
                var characterWR         = effectiveChar != null ? StatsAggregator.GetCharacterWR(MainFile.Db, effectiveChar, filter: filter) : StatsAggregator.GetGlobalWR(MainFile.Db, filter: filter);
                var shopBuyRateBaseline = StatsAggregator.GetShopBuyRateBaseline(MainFile.Db, filter);
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
