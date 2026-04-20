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
        // BaseLib's ModConfig.Init() ran the cfg load inside the constructor above. Now
        // sanitise any filter properties that might have been left in a degenerate state by
        // older versions or manual edits, so the next save writes clean values.
        SlayTheStatsConfig.Sanitize();
        ModConfigRegistry.Register(ModId, config);
        ModConfigBridge.DeferredRegister();

        // On every boot, discard any unsaved filter tweaks from the previous session
        // and snap the live filter values back to the user's saved defaults. Filter
        // changes only persist across restarts if the user clicked "Save Defaults".
        SlayTheStatsConfig.RestoreDefaults();
        Logger.Info($"Boot: reverted live filters to saved defaults " +
            $"(asc {SlayTheStatsConfig.AscensionMin}..{SlayTheStatsConfig.AscensionMax}, " +
            $"ver {SlayTheStatsConfig.VersionMin}..{SlayTheStatsConfig.VersionMax}, " +
            $"class '{SlayTheStatsConfig.ClassFilter}', profile '{SlayTheStatsConfig.FilterProfile}', " +
            $"groupUpgrades {SlayTheStatsConfig.GroupCardUpgrades}, " +
            $"includeMultiplayer {SlayTheStatsConfig.IncludeMultiplayer})");
        Logger.Info($"Boot: encounter_stats_mode={SlayTheStatsConfig.EncounterStatsRestartRequired} " +
            $"(BestiaryEnabled={SlayTheStatsConfig.BestiaryEnabled}, InCombatTooltipEnabled={SlayTheStatsConfig.InCombatTooltipEnabled})");

        if (!BuildInfo.IsRelease)
            Logger.Info($"DEV BUILD {StatsDb.CurrentModVersion} (built {BuildInfo.BuildDate} {BuildInfo.BuildTime})");

        Db = StatsDb.Load(SavePath, msg => Logger.Warn(msg));
        DebugTestData.InjectIfDebug(Db);
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
