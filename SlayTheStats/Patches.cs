using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

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
