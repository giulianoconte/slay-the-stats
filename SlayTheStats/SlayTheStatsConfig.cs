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
    /// Encounter-stats surfaces toggle: controls which of the two encounter-stats
    /// entrypoints (bestiary page + in-combat enemy hover tooltip) are enabled.
    /// Requires a game restart because the bestiary button injection happens at
    /// NCompendiumSubmenu._Ready time.
    /// </summary>
    public enum EncounterStatsMode
    {
        BestiaryAndTooltips,
        Tooltips,
        Disabled,
    }

    public static EncounterStatsMode EncounterStatsRestartRequired { get; set; } = EncounterStatsMode.BestiaryAndTooltips;

    /// <summary>True iff the bestiary submenu button should be injected.</summary>
    public static bool BestiaryEnabled => EncounterStatsRestartRequired == EncounterStatsMode.BestiaryAndTooltips;

    /// <summary>True iff the in-combat enemy-hover stats tooltip should render.</summary>
    public static bool InCombatTooltipEnabled => EncounterStatsRestartRequired != EncounterStatsMode.Disabled;

    /// <summary>
    /// How the bestiary's monster preview area is rendered.
    /// Live: live Spine SubViewport with idle animation (highest GPU cost).
    /// Static: first hover captures a static sprite into an ImageTexture, then
    /// the SubViewport is freed — dramatic GPU cost drop, no idle animation.
    /// None: no preview rendering at all (lowest GPU cost, for players who
    /// only care about the stats table).
    /// </summary>
    public enum BestiaryPreviewModeEnum
    {
        Live,
        Static,
        None,
    }

    public static BestiaryPreviewModeEnum BestiaryPreviewMode { get; set; } = BestiaryPreviewModeEnum.Live;

    /// <summary>Shorthand: true iff the preview area should render static sprites (not live, not off).</summary>
    public static bool BestiaryPreviewStatic => BestiaryPreviewMode == BestiaryPreviewModeEnum.Static;

    /// <summary>Shorthand: true iff the preview area renders at all (Live or Static).</summary>
    public static bool BestiaryPreviewEnabled => BestiaryPreviewMode != BestiaryPreviewModeEnum.None;

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

    // ── Bestiary view state (persisted across sessions) ──────────────────────
    // These remember the player's last bestiary configuration so subsequent
    // opens restore their preferred view. All hidden from BaseLib's UI.

    [ConfigHideInUI] public static string BestiarySelectedBiome { get; set; } = "";
    [ConfigHideInUI] public static string BestiarySortMode { get; set; } = "Name";
    [ConfigHideInUI] public static bool BestiarySortDescending { get; set; } = false;
    /// <summary>Empty = all characters; otherwise a CHARACTER.* id.</summary>
    [ConfigHideInUI] public static string BestiarySortCharacter { get; set; } = "";
    [ConfigHideInUI] public static bool BestiarySortBySignificance { get; set; } = false;

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
    /// Builds an AggregationFilter from the current LIVE static properties,
    /// defensively clamping out-of-range values and ignoring sentinel strings
    /// so callers always get a sane filter. Used by surfaces that should
    /// reflect the user's in-session pane edits — currently the bestiary
    /// page (so changing a filter on the pane re-renders the bestiary
    /// instantly).
    /// </summary>
    public static AggregationFilter BuildSafeFilter() =>
        BuildSafeFilterCore(AscensionMin, AscensionMax, VersionMin, VersionMax, FilterProfile);

    /// <summary>
    /// Same as <see cref="BuildSafeFilter"/> but reads from the PERSISTED
    /// defaults (<c>Default*</c> fields) rather than the live in-session
    /// values. Used by surfaces that should NOT be affected by the user's
    /// in-session pane edits — e.g. the in-combat encounter tooltip, which
    /// matches how the in-run card / relic tooltips work
    /// (<see cref="CardStatsTooltipPatch.BuildInRunFilter"/>). The user
    /// changing a filter on the bestiary pane mid-run shouldn't change what
    /// the combat tooltips show.
    /// </summary>
    public static AggregationFilter BuildSafeFilterFromDefaults() =>
        BuildSafeFilterCore(DefaultAscensionMin, DefaultAscensionMax, DefaultVersionMin, DefaultVersionMax, DefaultFilterProfile);

    private static AggregationFilter BuildSafeFilterCore(int ascMin, int ascMax, string versionMin, string versionMax, string profile)
    {
        var filter = new AggregationFilter();

        // Stash the raw values verbatim for footer rendering before any
        // sanitisation. FilterDisplayRaw preserves sentinel info that the
        // matching-side fields lose after clamping.
        filter.Display.RawAscMin   = ascMin;
        filter.Display.RawAscMax   = ascMax;
        filter.Display.RawVerMin   = versionMin ?? "";
        filter.Display.RawVerMax   = versionMax ?? "";
        filter.Display.RawProfile  = profile ?? "";

        // Clamp into [0, 20]; reset any garbage values to defaults.
        if (ascMin < 0 || ascMin > 20) ascMin = AscMinDefault;
        if (ascMax < 0 || ascMax > 20) ascMax = AscMaxDefault;
        if (ascMin > ascMax) (ascMin, ascMax) = (AscMinDefault, AscMaxDefault);

        if (ascMin > 0)  filter.AscensionMin = ascMin;
        if (ascMax < 20) filter.AscensionMax = ascMax;

        if (!IsUnboundedFilterVersion(versionMin))
            filter.VersionMin = versionMin;
        if (!IsUnboundedFilterVersion(versionMax))
            filter.VersionMax = versionMax;
        if (!string.IsNullOrEmpty(profile))
            filter.Profile = profile;

        return filter;
    }

    /// <summary>
    /// Test whether a version string should be treated as "no bound" when
    /// building a filter. Returns true for empty, invalid, or the named
    /// <see cref="BadVersionSentinels"/> (including <see cref="VersionLowest"/>
    /// and <see cref="VersionHighest"/>). Differs from
    /// <see cref="IsInvalidPersistedVersion"/>, which is the persist-time check
    /// and (correctly) keeps the no-bound sentinels as valid values.
    /// </summary>
    private static bool IsUnboundedFilterVersion(string? value)
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
    /// Test whether a persisted version string is legitimately garbage (should
    /// be wiped by <see cref="Sanitize"/>) vs a legitimate value to keep.
    /// Critically, the "no bound" sentinels <see cref="VersionLowest"/> and
    /// <see cref="VersionHighest"/> are NOT garbage — they're how the user
    /// expresses "auto-track the lowest/highest version in the data". They
    /// differ from <see cref="IsUnboundedFilterVersion"/>, which is the
    /// "should this be treated as an unbounded filter?" test used during
    /// filter construction and (correctly) includes the no-bound sentinels.
    /// </summary>
    private static bool IsInvalidPersistedVersion(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        // Valid bound sentinels — keep as-is.
        if (value == VersionLowest || value == VersionHighest) return false;
        // A real version starts with 'v' followed by a digit (e.g. v0.98.3).
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
        // Reset genuinely broken values to the proper "no bound" sentinel, NOT
        // to empty string — the filter pane dropdowns look up the selected
        // index by matching the persisted value against their option list,
        // and "" isn't in the list (Lowest / Highest are). Sanitizing to ""
        // caused the dropdown to fall back to index 0 ("Lowest") regardless
        // of the stored default, even though BuildSafeFilter still produced
        // the right unbounded filter — so stats rendered correctly but the
        // filter pane UI lied.
        if (IsInvalidPersistedVersion(VersionMin)) VersionMin = VersionLowest;
        if (IsInvalidPersistedVersion(VersionMax)) VersionMax = VersionHighest;
        // Same story for the persisted *defaults*: if they were wiped by a
        // previous build's buggy Sanitize, restore them to the appropriate
        // bound so the dropdowns can find them.
        if (IsInvalidPersistedVersion(DefaultVersionMin)) DefaultVersionMin = VersionLowest;
        if (IsInvalidPersistedVersion(DefaultVersionMax)) DefaultVersionMax = VersionHighest;

        // Migrate legacy "All" ClassFilter ("") to "Auto" (__class__). The "All" option
        // was removed from the filter pane dropdown — users who had it saved would otherwise
        // land on an invalid state. Auto resolves to the card's owning class for class
        // cards, or all-chars for colorless/curse/etc., which is what most users want.
        if (string.IsNullOrEmpty(ClassFilter))        ClassFilter        = ClassFilterClassSpecific;
        if (string.IsNullOrEmpty(DefaultClassFilter)) DefaultClassFilter = ClassFilterClassSpecific;
    }
}
