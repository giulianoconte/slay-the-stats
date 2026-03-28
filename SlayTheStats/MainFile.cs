using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace SlayTheStats;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "SlayTheStats"; //At the moment, this is used only for the Logger and harmony names.

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static string SavePath => System.IO.Path.Combine(OS.GetUserDataDir(), "slay-the-stats.json");

    public static StatsDb Db { get; private set; } = new();

    public static void Initialize()
    {
        Harmony harmony = new(ModId);
        harmony.PatchAll();

        Db = StatsDb.Load(SavePath, msg => Logger.Warn(msg));
        RunParser.ProcessNewRuns(Db);
    }
}
