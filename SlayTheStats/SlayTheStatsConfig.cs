using BaseLib.Config;

namespace SlayTheStats;

internal class SlayTheStatsConfig : SimpleModConfig
{
    /// <summary>
    /// When true, stats are filtered to each character's highest won ascension.
    /// e.g. if Ironclad has won up to A9, only A0–A9 runs are included for Ironclad.
    /// </summary>
    [ConfigHideInUI] public static bool OnlyHighestWonAscension { get; set; } = false;

    /// <summary>
    /// When true, base and upgraded card stats are merged in tooltips
    /// (e.g. Strike and Strike+ count as one entry).
    /// </summary>
    [ConfigHideInUI] public static bool GroupCardUpgrades { get; set; } = true;

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
    /// True once the user has seen the first-run filter tutorial in the
    /// compendium. Persisted via SimpleModConfig so it only shows on the
    /// first compendium visit after installing the mod.
    /// </summary>
    public static bool TutorialSeen { get; set; } = false;

    /// <summary>
    /// True once the user has seen the bestiary tutorial overlay (introduced in
    /// v0.3.0). Independent from the compendium tutorial because the bestiary
    /// has its own controls and stat columns to explain.
    /// </summary>
    public static bool BestiaryTutorialSeen { get; set; } = false;

    /// <summary>
    /// When true, the Stats Bestiary button is not injected into the compendium
    /// bottom row (and the bestiary submenu becomes unreachable from the menu).
    /// For users who don't want the extra button.
    /// </summary>
    public static bool BestiaryButtonDisabled { get; set; } = false;

    /// <summary>
    /// When true, the in-combat encounter stats tooltip (the floating panel that
    /// appears above hovered enemies) is suppressed entirely. The bestiary stats
    /// page still works regardless.
    /// </summary>
    public static bool InCombatEncounterTooltipDisabled { get; set; } = false;

    /// <summary>
    /// Override the root directory where SlayTheSpire2 stores its data
    /// (the folder that contains the "steam" subfolder).
    /// Leave empty to use the platform default.
    /// Example: /home/deck/.local/share/SlayTheSpire2
    /// </summary>
    public static string DataDirectory { get; set; } = "";

    /// <summary>
    /// When true, tooltips surface internal debug state (context key, raw counters, build version)
    /// to aid troubleshooting without requiring log inspection.
    /// </summary>
    public static bool DebugMode { get; set; } = false;

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
    [ConfigHideInUI] public static int AscensionMin { get; set; } = AscensionLowest;

    /// <summary>
    /// Maximum ascension to include in aggregation. AscensionHighest = +∞ (no
    /// upper bound, auto-tracks new high ascensions); any other int = explicit
    /// ceiling (won't auto-include newly-discovered higher ascensions).
    /// </summary>
    [ConfigHideInUI] public static int AscensionMax { get; set; } = AscensionHighest;

    /// <summary>Sentinel "Lowest" for version filter — auto-tracks oldest version in data.</summary>
    public const string VersionLowest = "__lowest__";

    /// <summary>Sentinel "Highest" for version filter — auto-tracks newest version in data.</summary>
    public const string VersionHighest = "__highest__";

    /// <summary>
    /// Minimum game version to include. VersionLowest = -∞ (no lower bound);
    /// any other value = explicit floor (compared semantically, e.g. 0.4.10 &gt; 0.4.9).
    /// </summary>
    [ConfigHideInUI] public static string VersionMin { get; set; } = VersionLowest;

    /// <summary>
    /// Maximum game version to include. VersionHighest = +∞ (no upper bound);
    /// any other value = explicit ceiling (compared semantically).
    /// </summary>
    [ConfigHideInUI] public static string VersionMax { get; set; } = VersionHighest;

    /// <summary>
    /// Class filter selection. Values:
    /// - "" → All characters
    /// - "__class__" → Class-specific (use the card's owning class; colorless/curse/etc. show all-char stats)
    /// - "CHARACTER.X" → Filter to a specific character (e.g. "CHARACTER.IRONCLAD")
    /// Defaults to "__class__" so fresh installs see class-relevant stats by default.
    /// </summary>
    [ConfigHideInUI] public static string ClassFilter { get; set; } = ClassFilterClassSpecific;

    /// <summary>Sentinel value for ClassFilter meaning "use the card's owning class".</summary>
    public const string ClassFilterClassSpecific = "__class__";

    /// <summary>Legacy convenience accessor — true iff ClassFilter is the class-specific sentinel.</summary>
    [ConfigHideInUI] public static bool ClassSpecificStats
    {
        get => ClassFilter == ClassFilterClassSpecific;
        set => ClassFilter = value ? ClassFilterClassSpecific : "";
    }

    /// <summary>
    /// Profile to filter by (e.g. "profile1"). Empty = all profiles.
    /// </summary>
    [ConfigHideInUI] public static string FilterProfile { get; set; } = "";

    // ── Persisted user defaults ────────────────────────────────────────────
    // These are the "saved defaults" the user can set via "Save as Defaults".
    // They persist across restarts via SimpleModConfig serialization.
    // All hidden from BaseLib's auto-generated UI — they're driven by the
    // compendium filter pane's "Save Defaults" action, not the mod settings page.

    [ConfigHideInUI] public static int DefaultAscensionMin { get; set; } = AscensionLowest;
    [ConfigHideInUI] public static int DefaultAscensionMax { get; set; } = AscensionHighest;
    [ConfigHideInUI] public static string DefaultVersionMin { get; set; } = VersionLowest;
    [ConfigHideInUI] public static string DefaultVersionMax { get; set; } = VersionHighest;
    [ConfigHideInUI] public static string DefaultClassFilter { get; set; } = ClassFilterClassSpecific;
    [ConfigHideInUI] public static string DefaultFilterProfile { get; set; } = "";
    [ConfigHideInUI] public static bool DefaultGroupCardUpgrades { get; set; } = true;

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
        VersionMin = VersionLowest;
        VersionMax = VersionHighest;
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

    // ── Filter sanitisation ─────────────────────────────────────────────────
    //
    // Older builds + manual cfg edits could leave the filter properties in a state that
    // silently rejected every encounter (e.g. AscensionMax = int.MaxValue, VersionMax =
    // "__lowest__"). BuildSafeFilter clamps every value into a sensible range and ignores
    // sentinel strings, and Sanitize() pulls the underlying static properties back into a
    // valid range so the next config save writes clean values.
    //
    // BuildSafeFilter is the bestiary's defensive filter constructor. The compendium card/relic
    // tooltips use BuildCompendiumFilter / BuildInRunFilter instead — those rely on Sanitize()
    // having scrubbed bad values at startup. The bestiary wraps a similar pane in v0.3.0;
    // BuildSafeFilter remains as a defensive helper until the bestiary is fully on the new
    // pane.

    private const int    AscMinDefault = 0;
    private const int    AscMaxDefault = 20;
    private static readonly string[] BadVersionSentinels =
        { "", "__lowest__", "__highest__", "__none__", "any", "Any" };

    /// <summary>
    /// Builds an AggregationFilter from the current static properties, defensively clamping
    /// out-of-range values and sentinel strings so callers always get a sane filter.
    /// </summary>
    public static AggregationFilter BuildSafeFilter()
    {
        var filter = new AggregationFilter();

        int ascMin = AscensionMin;
        int ascMax = AscensionMax;
        // Clamp into [0, 20]; reset any garbage values to defaults.
        if (ascMin < 0 || ascMin > 20) ascMin = AscMinDefault;
        if (ascMax < 0 || ascMax > 20) ascMax = AscMaxDefault;
        if (ascMin > ascMax) (ascMin, ascMax) = (AscMinDefault, AscMaxDefault);

        if (ascMin > 0)  filter.AscensionMin = ascMin;
        if (ascMax < 20) filter.AscensionMax = ascMax;

        if (!IsBadVersionSentinel(VersionMin))
            filter.VersionMin = VersionMin;
        if (!IsBadVersionSentinel(VersionMax))
            filter.VersionMax = VersionMax;
        if (!string.IsNullOrEmpty(FilterProfile))
            filter.Profile = FilterProfile;

        return filter;
    }

    private static bool IsBadVersionSentinel(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        foreach (var s in BadVersionSentinels)
            if (string.Equals(value, s, StringComparison.OrdinalIgnoreCase))
                return true;
        // A real version starts with 'v' followed by a digit (e.g. v0.98.3). Anything else is
        // either a sentinel we don't recognise or noise from a botched edit; ignore it.
        if (value.Length < 2 || value[0] != 'v' || !char.IsDigit(value[1])) return true;
        return false;
    }

    /// <summary>
    /// Pulls the static filter properties back into a valid range so the next save writes
    /// sane values. Called once after BaseLib has loaded the config from disk.
    /// </summary>
    public static void Sanitize()
    {
        if (AscensionMin < 0 || AscensionMin > 20) AscensionMin = AscMinDefault;
        if (AscensionMax < 0 || AscensionMax > 20) AscensionMax = AscMaxDefault;
        if (AscensionMin > AscensionMax) { AscensionMin = AscMinDefault; AscensionMax = AscMaxDefault; }
        if (IsBadVersionSentinel(VersionMin)) VersionMin = "";
        if (IsBadVersionSentinel(VersionMax)) VersionMax = "";
    }
}
