using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using System.Reflection;

namespace SlayTheStats;

/// <summary>
/// Shows encounter stats tooltip when hovering a monster during combat.
/// Patches NCreature.OnFocus/OnUnfocus to inject our tooltip alongside the game's hover tips.
/// </summary>
[HarmonyPatch(typeof(NCreature), "OnFocus")]
public static class CreatureFocusPatch
{
    private static bool _warnedOnce;

    static void Postfix(NCreature __instance)
    {
        if (SlayTheStatsConfig.DisableTooltipsEntirely) return;
        if (!CardHoverShowPatch.IsInRun) return;

        try
        {
            var encounterId = GetEncounterId(__instance);
            if (encounterId == null) return;

            if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap))
                return;

            var effectiveChar = CardHoverShowPatch.RunCharacter;
            var filter = BuildFilter(effectiveChar);
            var actStats = StatsAggregator.AggregateEncountersByAct(contextMap, filter);

            string? category = null;
            if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
                category = meta.Category;

            var categoryLabel = category != null ? EncounterCategory.FormatCategory(category) : "";
            var characterLabel = effectiveChar != null
                ? FormatCharacterName(effectiveChar)
                : "All";

            double deathRateBaseline = StatsAggregator.GetEncounterDeathRateBaseline(MainFile.Db, filter, category);
            double dmgPctBaseline = StatsAggregator.GetEncounterDmgPctBaseline(MainFile.Db, filter, category);

            // Sum all acts into a single row for the current character
            var combined = new EncounterEvent();
            foreach (var stat in actStats.Values)
            {
                combined.Fought           += stat.Fought;
                combined.Died             += stat.Died;
                combined.WonRun           += stat.WonRun;
                combined.TurnsTakenSum    += stat.TurnsTakenSum;
                combined.DamageTakenSum   += stat.DamageTakenSum;
                combined.DamageTakenSqSum += stat.DamageTakenSqSum;
                combined.HpEnteringSum    += stat.HpEnteringSum;
                combined.MaxHpSum         += stat.MaxHpSum;
                combined.PotionsUsedSum   += stat.PotionsUsedSum;
                combined.DmgPctSum        += stat.DmgPctSum;
                combined.DmgPctSqSum      += stat.DmgPctSqSum;
            }

            string statsText;
            if (combined.Fought == 0)
            {
                statsText = EncounterTooltipHelper.NoDataText(characterLabel, filter.AscensionMin, filter.AscensionMax);
            }
            else
            {
                statsText = EncounterTooltipHelper.BuildEncounterStatsTextSingleRow(
                    combined, deathRateBaseline, dmgPctBaseline,
                    characterLabel, filter.AscensionMin, filter.AscensionMax, categoryLabel);
            }

            var encounterName = EncounterCategory.FormatName(encounterId);
            TooltipHelper.TrySceneTheftOnce();
            TooltipHelper.EnsurePanelExists();
            TooltipHelper.ShowPanel($"[b]{encounterName}[/b]\n{statsText}", widthOverride: TooltipHelper.EncounterTooltipWidth);
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"[SlayTheStats] Encounter tooltip failed: {e.Message}");
                _warnedOnce = true;
            }
        }
    }

    /// <summary>
    /// Extracts the encounter model ID from an NCreature via reflection:
    /// NCreature.Entity.CombatState.Encounter.Id -> "ENCOUNTER.X"
    /// </summary>
    private static string? GetEncounterId(NCreature creature)
    {
        // Entity is a public property
        var entity = creature.GetType()
            .GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(creature);
        if (entity == null) return null;

        // CombatState is a public property on Creature
        var combatState = entity.GetType()
            .GetProperty("CombatState", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(entity);
        if (combatState == null) return null;

        // Encounter is a public property on CombatState
        var encounter = combatState.GetType()
            .GetProperty("Encounter", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(combatState);
        if (encounter == null) return null;

        // Id is on the model, Entry is on the ModelId
        var encId = encounter.GetType()
            .GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(encounter);
        if (encId == null) return null;

        var category = encId.GetType()
            .GetProperty("Category", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(encId) as string;
        var entry = encId.GetType()
            .GetProperty("Entry", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(encId) as string;

        return category != null && entry != null ? $"{category}.{entry}".ToUpper() : null;
    }

    private static AggregationFilter BuildFilter(string? character)
    {
        var filter = new AggregationFilter { Character = character };

        if (SlayTheStatsConfig.AscensionMin > 0)
            filter.AscensionMin = SlayTheStatsConfig.AscensionMin;
        if (SlayTheStatsConfig.AscensionMax < 20)
            filter.AscensionMax = SlayTheStatsConfig.AscensionMax;
        if (!string.IsNullOrEmpty(SlayTheStatsConfig.VersionMin))
            filter.VersionMin = SlayTheStatsConfig.VersionMin;
        if (!string.IsNullOrEmpty(SlayTheStatsConfig.VersionMax))
            filter.VersionMax = SlayTheStatsConfig.VersionMax;
        if (!string.IsNullOrEmpty(SlayTheStatsConfig.FilterProfile))
            filter.Profile = SlayTheStatsConfig.FilterProfile;

        return filter;
    }

    private static string FormatCharacterName(string characterId)
    {
        // "CHARACTER.IRONCLAD" -> "Ironclad"
        var dotIdx = characterId.IndexOf('.');
        var name = dotIdx >= 0 ? characterId[(dotIdx + 1)..] : characterId;
        if (name.Length == 0) return characterId;
        return char.ToUpper(name[0]) + name[1..].ToLower().Replace('_', ' ');
    }
}

[HarmonyPatch(typeof(NCreature), "OnUnfocus")]
public static class CreatureUnfocusPatch
{
    static void Postfix()
    {
        TooltipHelper.HasActiveHover = false;
        TooltipHelper.HideWithDelay();
    }
}
