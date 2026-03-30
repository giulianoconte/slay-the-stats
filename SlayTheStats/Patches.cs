using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using System.Reflection;

namespace SlayTheStats;

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
        try
        {
            // NGame → Run → Player → Character → Id (all via reflection — types may change)
            var run = __instance.GetType()
                .GetProperty("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(__instance);
            if (run == null) return;

            var player = run.GetType()
                .GetProperty("Player", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(run);
            if (player == null) return;

            var character = player.GetType()
                .GetProperty("Character", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(player);
            if (character == null) return;

            var id = character.GetType()
                .GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(character) as string;
            if (id == null) return;

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
    }
}
