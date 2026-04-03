using BaseLib.Config;
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

        // Apply patches individually so a missing target in one class doesn't abort the rest.
        foreach (var type in typeof(MainFile).Assembly.GetTypes())
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e) { Logger.Warn($"SlayTheStats: Patch {type.Name} skipped — {e.Message}"); }
        }

        var config = new SlayTheStatsConfig();
        ModConfigRegistry.Register(ModId, config);
        ModConfigBridge.DeferredRegister();

        TooltipHelper.TryLoadModFonts();

        Db = StatsDb.Load(SavePath, msg => Logger.Warn(msg));
        // RunParser.ProcessNewRuns is called from MainMenuReadyPatch (NMainMenu._Ready),
        // which fires on boot and after every run ends.
    }

    public override void _Ready()
    {
        // Belt-and-suspenders: create the tooltip panel early if GUMM adds MainFile to the scene tree.
        // EnsurePanelExists is the authoritative creation path used by the hover patch.
        CardHoverShowPatch.EnsurePanelExists();
    }
}
