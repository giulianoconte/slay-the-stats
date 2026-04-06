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

    /// <summary>
    /// Sentinel "Lowest" — no lower bound, includes whatever the lowest
    /// ascension in the data is. Survives mods that introduce negative
    /// ascensions without needing user reconfiguration.
    /// </summary>
    public const int AscensionLowest = int.MinValue;

    /// <summary>
    /// Sentinel "Highest" — no upper bound, includes whatever the highest
    /// ascension in the data is. Survives mods/future patches that introduce
    /// 11+ ascensions without needing user reconfiguration.
    /// </summary>
    public const int AscensionHighest = int.MaxValue;

    /// <summary>
    /// Minimum ascension to include in aggregation. AscensionLowest = -∞ (no
    /// lower bound, auto-tracks new low ascensions); any other int = explicit
    /// floor (won't auto-include newly-discovered lower ascensions).
    /// </summary>
    public static int AscensionMin { get; set; } = AscensionLowest;

    /// <summary>
    /// Maximum ascension to include in aggregation. AscensionHighest = +∞ (no
    /// upper bound, auto-tracks new high ascensions); any other int = explicit
    /// ceiling (won't auto-include newly-discovered higher ascensions).
    /// </summary>
    public static int AscensionMax { get; set; } = AscensionHighest;

    /// <summary>Minimum game version to include (empty = no min). Compared semantically.</summary>
    public static string VersionMin { get; set; } = "";

    /// <summary>Maximum game version to include (empty = no max). Compared semantically.</summary>
    public static string VersionMax { get; set; } = "";

    /// <summary>
    /// Class filter selection. Values:
    /// - "" → All characters
    /// - "__class__" → Class-specific (use the card's owning class; colorless/curse/etc. show all-char stats)
    /// - "CHARACTER.X" → Filter to a specific character (e.g. "CHARACTER.IRONCLAD")
    /// </summary>
    public static string ClassFilter { get; set; } = "";

    /// <summary>Sentinel value for ClassFilter meaning "use the card's owning class".</summary>
    public const string ClassFilterClassSpecific = "__class__";

    /// <summary>Legacy convenience accessor — true iff ClassFilter is the class-specific sentinel.</summary>
    public static bool ClassSpecificStats
    {
        get => ClassFilter == ClassFilterClassSpecific;
        set => ClassFilter = value ? ClassFilterClassSpecific : "";
    }

    /// <summary>
    /// Profile to filter by (e.g. "profile1"). Empty = all profiles.
    /// </summary>
    public static string FilterProfile { get; set; } = "";

    // ── Persisted user defaults ────────────────────────────────────────────
    // These are the "saved defaults" the user can set via "Save as Defaults".
    // They persist across restarts via SimpleModConfig serialization.

    public static int DefaultAscensionMin { get; set; } = AscensionLowest;
    public static int DefaultAscensionMax { get; set; } = AscensionHighest;
    public static string DefaultVersionMin { get; set; } = "";
    public static string DefaultVersionMax { get; set; } = "";
    public static string DefaultClassFilter { get; set; } = "";
    public static string DefaultFilterProfile { get; set; } = "";
    public static bool DefaultGroupCardUpgrades { get; set; } = true;

    internal static void SaveDefaults()
    {
        DefaultAscensionMin = AscensionMin;
        DefaultAscensionMax = AscensionMax;
        DefaultVersionMin = VersionMin;
        DefaultVersionMax = VersionMax;
        DefaultClassFilter = ClassFilter;
        DefaultFilterProfile = FilterProfile;
        DefaultGroupCardUpgrades = GroupCardUpgrades;
    }

    internal static void RestoreDefaults()
    {
        AscensionMin = DefaultAscensionMin;
        AscensionMax = DefaultAscensionMax;
        VersionMin = DefaultVersionMin;
        VersionMax = DefaultVersionMax;
        ClassFilter = DefaultClassFilter;
        FilterProfile = DefaultFilterProfile;
        GroupCardUpgrades = DefaultGroupCardUpgrades;
    }

    internal static void ClearAllFilters()
    {
        AscensionMin = AscensionLowest;
        AscensionMax = AscensionHighest;
        VersionMin = "";
        VersionMax = "";
        ClassFilter = "";
        FilterProfile = "";
        GroupCardUpgrades = true;
    }

    internal static bool IsNonDefault(string field) => field switch
    {
        "AscensionMin" => AscensionMin != DefaultAscensionMin,
        "AscensionMax" => AscensionMax != DefaultAscensionMax,
        "VersionMin" => VersionMin != DefaultVersionMin,
        "VersionMax" => VersionMax != DefaultVersionMax,
        "ClassFilter" => ClassFilter != DefaultClassFilter,
        "FilterProfile" => FilterProfile != DefaultFilterProfile,
        "GroupUpgrades" => GroupCardUpgrades != DefaultGroupCardUpgrades,
        _ => false,
    };
}
