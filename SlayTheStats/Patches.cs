using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;

namespace SlayTheStats;

/// <summary>
/// Extracts "CATEGORY.ENTRY" from a CharacterModel instance via reflection.
/// Returns null if the chain is missing or throws.
/// </summary>
internal static class CharacterIdHelper
{
    internal static string? Extract(object? character)
    {
        if (character == null) return null;
        var charId = character.GetType()
            .GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(character);
        if (charId == null) return null;
        var category = charId.GetType()
            .GetProperty("Category", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(charId) as string;
        var entry = charId.GetType()
            .GetProperty("Entry", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(charId) as string;
        return category != null && entry != null ? $"{category}.{entry}".ToUpper() : null;
    }
}

/// <summary>
/// Triggers run parsing whenever the player returns to the main menu,
/// which happens after every run ends (win, loss, or quit to menu).
/// </summary>
[HarmonyPatch(typeof(NGame), "ReturnToMainMenuAfterRun")]
public static class RunEndedPatch
{
    static void Prefix()
    {
        RunParser.ProcessNewRuns(MainFile.Db, MainFile.SavePath, msg => MainFile.Logger.Info(msg), msg => MainFile.Logger.Warn(msg));
    }
}

/// <summary>
/// Sets the current run character as soon as a run starts, so the compendium shows the
/// correct character's stats from the first card reward screen (not only after first hover).
/// Best-effort: silently skipped in MainFile if StartRun does not exist in this game version.
/// </summary>
[HarmonyPatch(typeof(NGame), "StartRun")]
public static class StartRunPatch
{
    static void Postfix(NGame __instance)
    {
        CardHoverShowPatch.IsInRun = true;
        MainFile.Logger.Info("[SlayTheStats] StartRun fired: IsInRun=true");
        try
        {
            // NGame → Run → Player → Character → Id (all via reflection — types may change)
            var run       = __instance.GetType().GetProperty("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);
            var player    = run?.GetType().GetProperty("Player", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(run);
            var character = player?.GetType().GetProperty("Character", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(player);
            var id        = CharacterIdHelper.Extract(character);
            if (id == null) { MainFile.Logger.Info("[SlayTheStats] StartRun: character id was null"); return; }
            CardHoverShowPatch.CurrentCharacter = id;
            MainFile.Logger.Info($"[SlayTheStats] StartRun: character set to '{id}'");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] StartRun reflection failed: {e.Message}");
        }
    }
}

/// <summary>
/// Sets the current run character when a saved run is loaded (Continue button, or returning
/// from a boss chest screen). LoadRun fires in all cases where StartRun does not — the two
/// together cover every path into a run.
/// </summary>
[HarmonyPatch(typeof(NGame), "LoadRun")]
public static class LoadRunPatch
{
    static void Prefix(RunState runState)
    {
        CardHoverShowPatch.IsInRun = true;
        MainFile.Logger.Info("[SlayTheStats] LoadRun fired: IsInRun=true");
        try
        {
            var players = runState?.Players;
            if (players == null || players.Count == 0) { MainFile.Logger.Info("[SlayTheStats] LoadRun: no players in runState"); return; }
            var id = CharacterIdHelper.Extract(players[0].Character);
            if (id == null) { MainFile.Logger.Info("[SlayTheStats] LoadRun: character id was null"); return; }
            CardHoverShowPatch.CurrentCharacter = id;
            MainFile.Logger.Info($"[SlayTheStats] LoadRun: character set to '{id}'");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] LoadRun reflection failed: {e.Message}");
        }
    }
}

/// <summary>
/// Belt-and-suspenders: clears any orphaned card/relic tooltip when a new combat room becomes
/// ready. Covers the case where a card-reward overlay was showing when the previous room ended,
/// and the game destroyed the card nodes without firing ClearHoverTips — leaving HasActiveHover
/// stuck True and the panel visible.
/// </summary>
[HarmonyPatch(typeof(NCombatRoom), "_Ready")]
public static class CombatRoomReadyPatch
{
    static void Postfix()
    {
        var panel = TooltipHelper.GetPanelPublic();
        if (panel == null || !panel.Visible) return;
        MainFile.Logger.Info($"[SlayTheStats] CombatRoomReady: panel still visible on room start, force-hiding (hasActiveHover={TooltipHelper.HasActiveHover})");
        TooltipHelper.HasActiveHover = false;
        TooltipHelper.InspectActive  = false;
        CardHoverShowPatch.HideTooltip();
        RelicHoverHelper.ForceHide();
    }
}

/// <summary>
/// Belt-and-suspenders: clears the cached run character whenever the main menu loads,
/// so the compendium always shows "all characters" stats after a run ends.
/// Covers any exit path that ReturnToMainMenuAfterRun might not catch, and also guards
/// against compendium card holders having stale owner data from the previous run.
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class MainMenuReadyPatch
{
    static void Postfix()
    {
        CardHoverShowPatch.CurrentCharacter = null;
        CardHoverShowPatch.IsInRun = false;
        MainFile.Logger.Info("[SlayTheStats] MainMenuReady: CurrentCharacter cleared, IsInRun=false");
    }
}
