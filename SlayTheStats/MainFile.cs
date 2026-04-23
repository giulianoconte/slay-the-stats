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

        // Localization init deferred to NMainMenu._Ready (see MainMenuReadyPatch).
        // LocManager.Instance is null at this point in boot; trying to subscribe or
        // read the current language here NREs and we fall back to the 'eng' table
        // even on non-English locales.

        var config = new SlayTheStatsConfig();
        // BaseLib's ModConfig.Init() ran the cfg load inside the constructor above. Now
        // sanitise any filter properties that might have been left in a degenerate state by
        // older versions or manual edits, so the next save writes clean values.
        SlayTheStatsConfig.Sanitize();
        ModConfigRegistry.Register(ModId, config);

        // On every boot, discard any unsaved filter tweaks from the previous session
        // and snap the live filter values back to the user's saved defaults. Filter
        // changes only persist across restarts if the user clicked "Save Defaults".
        SlayTheStatsConfig.RestoreDefaults();

        // One-shot config migrations gated on LastSeenModVersion. Each migration
        // decides its own eligibility window from the previously-seen version
        // (empty string for fresh installs and for pre-v0.3.1 upgrades, since
        // LastSeenModVersion didn't exist before v0.3.1). LastSeenModVersion is
        // updated + saved at the end of this block so migrations only fire once
        // per install.
        string priorVersion = SlayTheStatsConfig.LastSeenModVersion;

        // v0.3.1: the bestiary tutorial was rewritten from a single centered
        // explainer into a four-phase walkthrough. Users upgrading from v0.3.0
        // (or any earlier version) with BestiaryTutorialSeen=true need the flag
        // reset once so the new version auto-fires on their next bestiary open.
        // Fresh installs fall into this branch too — no-op since the flag is
        // already false by default. Delete this block in v0.3.2 or later once
        // pre-v0.3.1 installs are a non-concern.
        if (IsVersionOlderThan(priorVersion, "v0.3.1"))
        {
            SlayTheStatsConfig.BestiaryTutorialSeen = false;
            Logger.Info($"v0.3.1 migration: reset BestiaryTutorialSeen (prior version '{priorVersion}') so the new four-phase bestiary tutorial fires on first open.");
        }

        if (priorVersion != StatsDb.CurrentModVersion)
        {
            SlayTheStatsConfig.LastSeenModVersion = StatsDb.CurrentModVersion;
            try { BaseLib.Config.ModConfig.SaveDebounced<SlayTheStatsConfig>(); }
            catch (Exception e) { Logger.Warn($"LastSeenModVersion save failed: {e.Message}"); }
        }
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

    /// <summary>Returns true when <paramref name="version"/> is lexically older
    /// than <paramref name="target"/> under "vMAJOR.MINOR.PATCH" parsing. Empty,
    /// null, or malformed <paramref name="version"/> counts as older than every
    /// target (treats the field as "unrecorded"). A plain string-compare would
    /// order "v0.10.0" before "v0.3.0"; parsing each component as an int keeps
    /// semver ordering correct past single-digit minor/patch values.</summary>
    internal static bool IsVersionOlderThan(string? version, string target)
    {
        if (!TryParseVersion(version, out var v)) return true;
        if (!TryParseVersion(target,  out var t)) return false;
        if (v.major != t.major) return v.major < t.major;
        if (v.minor != t.minor) return v.minor < t.minor;
        return v.patch < t.patch;
    }

    private static bool TryParseVersion(string? s, out (int major, int minor, int patch) v)
    {
        v = (0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.TrimStart('v', 'V');
        var parts = trimmed.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var major)) return false;
        if (!int.TryParse(parts[1], out var minor)) return false;
        if (!int.TryParse(parts[2], out var patch)) return false;
        v = (major, minor, patch);
        return true;
    }
}
