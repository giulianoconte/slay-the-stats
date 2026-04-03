using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
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
/// Sets run state as soon as a run starts. Reads character from the RunState parameter
/// (fully initialized before the method is called) rather than from the NGame instance
/// (which may not be fully set up yet).
/// </summary>
[HarmonyPatch(typeof(NGame), "StartRun")]
public static class StartRunPatch
{
    static void Prefix(RunState runState)
    {
        CardHoverShowPatch.IsInRun = true;
        if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] StartRun fired: IsInRun=true");
        try
        {
            var players = runState?.Players;
            if (players == null || players.Count == 0) { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] StartRun: no players in runState"); return; }
            var id = CharacterIdHelper.Extract(players[0].Character);
            if (id == null) { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] StartRun: character id was null"); return; }
            CardHoverShowPatch.RunCharacter = id;
            if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info($"[SlayTheStats] StartRun: character set to '{id}'");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] StartRun character extraction failed: {e.Message}");
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
        if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] LoadRun fired: IsInRun=true");
        try
        {
            var players = runState?.Players;
            if (players == null || players.Count == 0) { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] LoadRun: no players in runState"); return; }
            var id = CharacterIdHelper.Extract(players[0].Character);
            if (id == null) { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] LoadRun: character id was null"); return; }
            CardHoverShowPatch.RunCharacter = id;
            if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info($"[SlayTheStats] LoadRun: character set to '{id}'");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] LoadRun reflection failed: {e.Message}");
        }
    }
}

/// <summary>
/// Single cleanup and data refresh point. Fires on game boot and after every run ends
/// (returning to the main menu always loads this node). Clears run state and triggers
/// RunParser to process any new .run files (the just-completed run, or runs from before
/// the mod was installed on first boot).
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class MainMenuReadyPatch
{
    static void Postfix()
    {
        CardHoverShowPatch.RunCharacter = null;
        CardHoverShowPatch.IsInRun = false;
        if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] MainMenuReady: RunCharacter cleared, IsInRun=false");
        RunParser.ProcessNewRuns(MainFile.Db, MainFile.SavePath, msg => { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info(msg); }, msg => MainFile.Logger.Warn(msg));
    }
}
