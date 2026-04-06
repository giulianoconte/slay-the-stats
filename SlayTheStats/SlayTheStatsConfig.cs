using BaseLib.Config;

namespace SlayTheStats;

internal class SlayTheStatsConfig : SimpleModConfig
{
    /// <summary>
    /// When true, stats are filtered to each character's highest won ascension.
    /// e.g. if Ironclad has won up to A9, only A0–A9 runs are included for Ironclad.
    /// </summary>
    public static bool OnlyHighestWonAscension { get; set; } = false;

    /// <summary>
    /// When true, base and upgraded card stats are merged in tooltips
    /// (e.g. Strike and Strike+ count as one entry).
    /// </summary>
    public static bool GroupCardUpgrades { get; set; } = true;

    /// <summary>
    /// When true, uses a color-blind-friendly palette for stat coloring.
    /// When false, uses red/green coloring.
    /// </summary>
    public static bool ColorBlindMode { get; set; } = false;

    /// <summary>
    /// When true, stat tooltips appear everywhere (in-run card/relic hovers, shop, compendium).
    /// When false, stats only appear in the compendium.
    /// </summary>
    public static bool ShowInRunStats { get; set; } = true;

    /// <summary>
    /// Master switch. When true, all stat tooltips are suppressed across the entire game.
    /// </summary>
    public static bool DisableTooltipsEntirely { get; set; } = false;

    /// <summary>
    /// When true, tooltips surface internal debug state (context key, raw counters, build version)
    /// to aid troubleshooting without requiring log inspection.
    /// </summary>
    public static bool DebugMode { get; set; } = false;

    /// <summary>
    /// Override the root directory where SlayTheSpire2 stores its data
    /// (the folder that contains the "steam" subfolder).
    /// Leave empty to use the platform default.
    /// Example: /home/deck/.local/share/SlayTheSpire2
    /// </summary>
    public static string DataDirectory { get; set; } = "";

    // ── Aggregation filter properties (v0.2.0) ──────────────────────────────

    /// <summary>Minimum ascension to include in aggregation (0 = no min).</summary>
    public static int AscensionMin { get; set; } = 0;

    /// <summary>Maximum ascension to include in aggregation (10 = default max, STS2 max is 20).</summary>
    public static int AscensionMax { get; set; } = 10;

    /// <summary>Minimum game version to include (empty = no min). Compared semantically.</summary>
    public static string VersionMin { get; set; } = "";

    /// <summary>Maximum game version to include (empty = no max). Compared semantically.</summary>
    public static string VersionMax { get; set; } = "";

    /// <summary>
    /// Comma-separated list of character IDs to include (e.g. "CHARACTER.IRONCLAD,CHARACTER.SILENT").
    /// Empty = all characters. Only used as a legacy/internal field now; prefer ClassSpecificStats.
    /// </summary>
    public static string FilterCharacters { get; set; } = "";

    /// <summary>
    /// When true, class cards show stats for their owning class only.
    /// Colorless, curse, event, and quest cards always show all-character stats.
    /// </summary>
    public static bool ClassSpecificStats { get; set; } = false;

    /// <summary>
    /// Profile to filter by (e.g. "profile1"). Empty = all profiles.
    /// </summary>
    public static string FilterProfile { get; set; } = "";

    // ── Persisted user defaults ────────────────────────────────────────────
    // These are the "saved defaults" the user can set via "Save as Defaults".
    // They persist across restarts via SimpleModConfig serialization.

    public static int DefaultAscensionMin { get; set; } = 0;
    public static int DefaultAscensionMax { get; set; } = 10;
    public static string DefaultVersionMin { get; set; } = "";
    public static string DefaultVersionMax { get; set; } = "";
    public static bool DefaultClassSpecificStats { get; set; } = false;
    public static string DefaultFilterProfile { get; set; } = "";
    public static bool DefaultGroupCardUpgrades { get; set; } = true;

    internal static void SaveDefaults()
    {
        DefaultAscensionMin = AscensionMin;
        DefaultAscensionMax = AscensionMax;
        DefaultVersionMin = VersionMin;
        DefaultVersionMax = VersionMax;
        DefaultClassSpecificStats = ClassSpecificStats;
        DefaultFilterProfile = FilterProfile;
        DefaultGroupCardUpgrades = GroupCardUpgrades;
    }

    internal static void RestoreDefaults()
    {
        AscensionMin = DefaultAscensionMin;
        AscensionMax = DefaultAscensionMax;
        VersionMin = DefaultVersionMin;
        VersionMax = DefaultVersionMax;
        ClassSpecificStats = DefaultClassSpecificStats;
        FilterProfile = DefaultFilterProfile;
        GroupCardUpgrades = DefaultGroupCardUpgrades;
    }

    internal static void ClearAllFilters()
    {
        AscensionMin = 0;
        AscensionMax = 10;
        VersionMin = "";
        VersionMax = "";
        ClassSpecificStats = false;
        FilterProfile = "";
        GroupCardUpgrades = true;
    }

    internal static bool IsNonDefault(string field) => field switch
    {
        "AscensionMin" => AscensionMin != DefaultAscensionMin,
        "AscensionMax" => AscensionMax != DefaultAscensionMax,
        "VersionMin" => VersionMin != DefaultVersionMin,
        "VersionMax" => VersionMax != DefaultVersionMax,
        "ClassSpecific" => ClassSpecificStats != DefaultClassSpecificStats,
        "FilterProfile" => FilterProfile != DefaultFilterProfile,
        "GroupUpgrades" => GroupCardUpgrades != DefaultGroupCardUpgrades,
        _ => false,
    };
}
