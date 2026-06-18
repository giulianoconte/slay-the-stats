using System;
using BaseLib.Config;
using SlayTheStats.Community;

namespace SlayTheStats;

internal class SlayTheStatsConfig : SimpleModConfig
{
    /// <summary>
    /// When true, base and upgraded card stats are merged in tooltips
    /// (e.g. Strike and Strike+ count as one entry).
    /// </summary>
    [ConfigHideInUI] public static bool GroupCardUpgrades { get; set; } = true;

    /// <summary>
    /// When true, runs with non-standard game_mode (e.g. multiplayer) are included
    /// alongside standard runs. When false (default), only game_mode="standard"
    /// runs are counted — matches every filter builder's historical behaviour.
    /// </summary>
    [ConfigHideInUI] public static bool IncludeMultiplayer { get; set; } = false;

    /// <summary>
    /// When true, uses a color-blind-friendly palette for stat coloring.
    /// When false, uses red/green coloring.
    /// </summary>
    public static bool ColorBlindMode { get; set; } = false;

    /// <summary>
    /// When true (default), stats are shaded green/red by significance (how far
    /// they sit from the baseline). When false, every stat renders in the neutral
    /// shade — bold/weight emphasis is kept, only the color grading is dropped.
    /// </summary>
    public static bool SignificanceColoring { get; set; } = true;

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
    /// Mod version that was running on this install's most recent boot.
    /// Written at the end of <c>MainFile.Initialize</c> after any one-shot
    /// config migrations run. Empty string on fresh installs and on upgrades
    /// from versions before this field existed (v0.3.0 and earlier) — both
    /// cases are treated as "older than any version" by migration checks.
    /// Persisted so subsequent boots short-circuit migrations that have
    /// already been applied on this install.
    /// </summary>
    [ConfigHideInUI] public static string LastSeenModVersion { get; set; } = "";

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
    /// Live: live Spine SubViewport with idle animation (higher GPU cost).
    /// None: no preview rendering at all (lowest GPU cost, for players who
    /// only care about the stats table).
    /// </summary>
    public enum BestiaryPreviewModeEnum
    {
        Live,
        None,
    }

    public static BestiaryPreviewModeEnum BestiaryPreviewMode { get; set; } = BestiaryPreviewModeEnum.Live;

    /// <summary>Shorthand: true iff the preview area renders at all (i.e. Live, not None).</summary>
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
    /// to aid troubleshooting without requiring log inspection. The toggle is hidden from
    /// BaseLib's mod settings UI on release builds — its visible effects (sparkline debug
    /// panel, RichTextLabel cell wireframe overlay) are gated on <c>!BuildInfo.IsRelease</c>
    /// at runtime, so leaving the toggle exposed on release would be a UI knob with no
    /// observable consequence.
    /// </summary>
#if !DEV_BUILD
    [ConfigHideInUI]
#endif
    public static bool DebugMode { get; set; } = false;

    // ── Community stats (Spire Codex integration, #29) ──────────────────────
    // Declared last among the UI-visible properties so the [ConfigSection] header
    // groups exactly these rows — a section runs until the next [ConfigSection],
    // and everything below here is [ConfigHideInUI], so nothing else is swept in.

    /// <summary>
    /// Community stats (Spire Codex integration) participation level. (#34)
    /// Off: no network calls at all. ReadOnly: pull community baselines and show
    /// them in tooltips. ReadShare: also submit completed runs to the community
    /// corpus. Default Off — opt-in only; enabling makes outbound calls to
    /// spire-codex.com. (The share half of ReadShare wires up in #36; until then
    /// ReadShare reads exactly like ReadOnly and uploads nothing.)
    /// </summary>
    // Order here drives the mod-config dropdown order (BaseLib renders enum members
    // top-to-bottom): the promoted "share" option first, plain Off last. Persistence is
    // by name, the default is pinned explicitly below, and every comparison is equality
    // (never ordinal), so this order is purely cosmetic.
    public enum CommunityMode
    {
        ReadShare,
        ReadOnly,
        Off,
    }

    [ConfigSection("CommunityStats")]
    [ConfigHoverTip]
    public static CommunityMode Community { get; set; } = CommunityMode.Off;

    /// <summary>
    /// Which community reference cohort the baseline row shows (Area 4/6): the broad
    /// "everyone" line (All) or ascension-10+ (A10). A coarse reference, never matched
    /// to the player's active filters. Only meaningful while community stats are on,
    /// so the row is hidden when <see cref="Community"/> is Off.
    /// </summary>
    public enum CommunityCohort
    {
        All,
        A10,
    }

    [ConfigVisibleIf(nameof(Community), CommunityMode.ReadOnly, CommunityMode.ReadShare)]
    [ConfigHoverTip]
    public static CommunityCohort CommunityReferenceCohort { get; set; } = CommunityCohort.All;

    /// <summary>
    /// [ConfigButton] Force an immediate community-stats refresh now, bypassing the
    /// normal staleness/cadence gate (but still honoring a server 429 Retry-After).
    /// Shown only when community stats are enabled.
    /// </summary>
    [ConfigButton("CommunityRefreshNowButton")]
    [ConfigVisibleIf(nameof(Community), CommunityMode.ReadOnly, CommunityMode.ReadShare)]
    private static void CommunityRefreshNow() => CommunityStats.ForceRefresh();

    /// <summary>
    /// [ConfigButton] Opens spire-codex.com — the visible attribution / data-source
    /// link required by the integration (Area 7). Shown only when community stats
    /// are enabled. Always points at the public site, never a dev override URL.
    /// </summary>
    [ConfigButton("CommunityAttributionButton")]
    [ConfigVisibleIf(nameof(Community), CommunityMode.ReadOnly, CommunityMode.ReadShare)]
    private static void CommunityOpenWebsite() => CommunityStats.OpenWebsite();

#if DEV_BUILD
    /// <summary>
    /// [ConfigButton] DEV-ONLY: reset all Spire Codex state to a fresh-install baseline so
    /// the onboarding popup and read/write paths can be re-tested without hand-editing the
    /// config on the host. Sets mode → Off and consent → Unset (re-arms the popup), clears
    /// the launch counter, and deletes the cached community data + submission ledger.
    /// Compiled out of release builds entirely — always visible in dev, including when
    /// Community is Off (that's the state you reset back to). The popup re-arms for the next
    /// main-menu entry (back out to the menu, or relaunch).
    /// </summary>
    [ConfigButton("CommunityResetStateButton")]
    private static void CommunityResetState()
    {
        Community = CommunityMode.Off;
        CommunityConsentState = ConsentFlow.State.Unset;
        CommunityConsentDismissedLaunches = 0;
        try { ModConfig.SaveDebounced<SlayTheStatsConfig>(); }
        catch (Exception e) { MainFile.Logger.Warn($"Spire Codex reset: config save failed: {e.Message}"); }

        CommunityStats.ResetCacheAndState();
        RunSubmitter.ResetLedgerAndState();
        CommunityConsentPrompt.ResetForReshow();

        MainFile.Logger.Info("Spire Codex state reset to fresh-install (mode→Off, consent→Unset, cache+ledger cleared). Popup re-arms on next main-menu entry.");
    }
#endif

    /// <summary>
    /// Persisted state of the Spire Codex onboarding consent flow (#35). Drives whether
    /// the first-run consent popup appears: Unset → not yet shown; Dismissed → shown once
    /// and decide-later (re-prompts once after <see cref="CommunityConsentDismissedLaunches"/>
    /// reaches <see cref="ConsentFlow.ReshowAfterLaunches"/>); Resolved → terminal
    /// (Enable/Decline, the re-prompt's dismissal, or any explicit Community settings toggle).
    /// Hidden from the settings UI — pure bookkeeping; the transition logic lives in
    /// <see cref="ConsentFlow"/>.
    /// </summary>
    [ConfigHideInUI] public static ConsentFlow.State CommunityConsentState { get; set; } = ConsentFlow.State.Unset;

    /// <summary>
    /// Launches elapsed since the consent popup was decide-later dismissed. Only ticks
    /// while <see cref="CommunityConsentState"/> is Dismissed; the popup re-shows once it
    /// reaches <see cref="ConsentFlow.ReshowAfterLaunches"/>. Hidden bookkeeping.
    /// </summary>
    [ConfigHideInUI] public static int CommunityConsentDismissedLaunches { get; set; } = 0;

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
    [ConfigHideInUI] public static bool DefaultIncludeMultiplayer { get; set; } = false;

    /// <summary>
    /// One-line description of the live filter state, matching the parenthetical
    /// content of the boot log line so filter-change logs grep alongside it.
    /// Shared by the boot line, the live filter-change log, and Save Defaults.
    /// </summary>
    internal static string DescribeLiveFilters() =>
        $"asc {AscensionMin}..{AscensionMax}, " +
        $"ver {VersionMin}..{VersionMax}, " +
        $"class '{ClassFilter}', profile '{FilterProfile}', " +
        $"groupUpgrades {GroupCardUpgrades}, " +
        $"includeMultiplayer {IncludeMultiplayer}";

    internal static void SaveDefaults()
    {
        DefaultAscensionMin = AscensionMin;
        DefaultAscensionMax = AscensionMax;
        DefaultVersionMin = VersionMin;
        DefaultVersionMax = VersionMax;
        DefaultClassFilter = ClassFilter;
        DefaultFilterProfile = FilterProfile;
        DefaultGroupCardUpgrades = GroupCardUpgrades;
        DefaultIncludeMultiplayer = IncludeMultiplayer;
        MainFile.Logger.Info($"Save Defaults: saved live filters as defaults ({DescribeLiveFilters()})");
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
        IncludeMultiplayer = DefaultIncludeMultiplayer;
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
        IncludeMultiplayer = false;
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
        "IncludeMultiplayer" => IncludeMultiplayer != DefaultIncludeMultiplayer,
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

    private static readonly string[] BadVersionSentinels =
        { "", "__lowest__", "__highest__", "__none__", "any", "Any" };

    /// <summary>
    /// Resolves raw config ascension bounds (which use <see cref="AscensionLowest"/>
    /// = int.MinValue and <see cref="AscensionHighest"/> = int.MaxValue as sentinels
    /// for "lowest in data" / "highest in data") into the typed nullable bounds
    /// the AggregationFilter expects. Single source of truth — both
    /// <see cref="BuildSafeFilterCore"/> and the card-tooltip filter builders
    /// share this so display, sentinel resolution, and matching all agree.
    ///
    /// Sentinel combinations:
    ///   (Lowest,  Highest) → (null, null)        full range, no constraint
    ///   (Lowest,  N)       → (null, N)           bounded above only
    ///   (M,       Highest) → (M, null)           bounded below only
    ///   (M,       N)       → (M, N)              explicit range
    ///   (Lowest,  Lowest)  → (dataMin, dataMin)  resolves to data's lowest
    ///   (Highest, Highest) → (dataMax, dataMax)  resolves to data's highest
    ///   (Highest, N)       → (null, N)           defensive: nonsense combo, drop lower bound
    ///   (M,       Lowest)  → (M, null)           defensive: nonsense combo, drop upper bound
    /// </summary>
    public static (int? min, int? max) ResolveAscensionBounds(int rawMin, int rawMax)
    {
        bool minIsLowest  = rawMin == AscensionLowest;
        bool minIsHighest = rawMin == AscensionHighest;
        bool maxIsLowest  = rawMax == AscensionLowest;
        bool maxIsHighest = rawMax == AscensionHighest;

        if (minIsLowest && maxIsLowest)
        {
            var data = StatsAggregator.GetDistinctAscensions(MainFile.Db);
            int dataMin = data.Count > 0 ? data[0] : 0;
            return (dataMin, dataMin);
        }

        if (minIsHighest && maxIsHighest)
        {
            var data = StatsAggregator.GetDistinctAscensions(MainFile.Db);
            int dataMax = data.Count > 0 ? data[^1] : 10;
            return (dataMax, dataMax);
        }

        int? min = (minIsLowest || minIsHighest) ? null : (int?)rawMin;
        int? max = (maxIsLowest || maxIsHighest) ? null : (int?)rawMax;
        return (min, max);
    }

    /// <summary>
    /// Version analogue of <see cref="ResolveAscensionBounds"/>: resolves the raw
    /// version sentinels into concrete filter bounds, symmetrically — a "Highest"
    /// or "Lowest" sentinel is handled whether it sits on the min or the max side.
    ///   (Lowest,  Lowest)  → (dataLow,  dataLow)   pin to the oldest build in data
    ///   (Highest, Highest) → (dataHigh, dataHigh)  pin to the newest build in data
    ///   otherwise each side independently: a sentinel/garbage bound → null
    ///   (unbounded), a real version → itself.
    /// Mirrors the footer's <c>FormatVersionContextV2</c> so the displayed range
    /// and the applied filter agree. Without this, a "Highest" used as the LOWER
    /// bound leaked through as the literal "__highest__", which
    /// <see cref="AggregationFilter.CompareVersions"/> read as version 0 — silently
    /// dropping the lower bound (#8).
    /// </summary>
    public static (string? min, string? max) ResolveVersionBounds(string? rawMin, string? rawMax)
    {
        bool minIsLowest  = string.IsNullOrEmpty(rawMin) || rawMin == VersionLowest;
        bool minIsHighest = rawMin == VersionHighest;
        bool maxIsLowest  = rawMax == VersionLowest;
        bool maxIsHighest = string.IsNullOrEmpty(rawMax) || rawMax == VersionHighest;

        if (minIsLowest && maxIsLowest)
        {
            var data = StatsAggregator.GetDistinctVersions(MainFile.Db);
            return data.Count > 0 ? (data[0], data[0]) : (null, null);
        }

        if (minIsHighest && maxIsHighest)
        {
            var data = StatsAggregator.GetDistinctVersions(MainFile.Db);
            return data.Count > 0 ? (data[^1], data[^1]) : (null, null);
        }

        // IsUnboundedFilterVersion already encodes "empty / sentinel / not a real
        // version → unbounded", so each side maps a sentinel or garbage value to
        // null and keeps a genuine version verbatim.
        string? min = IsUnboundedFilterVersion(rawMin) ? null : rawMin;
        string? max = IsUnboundedFilterVersion(rawMax) ? null : rawMax;
        return (min, max);
    }

    /// <summary>
    /// Builds an AggregationFilter from the current LIVE static properties,
    /// defensively clamping out-of-range values and ignoring sentinel strings
    /// so callers always get a sane filter. Used by surfaces that should
    /// reflect the user's in-session pane edits — currently the bestiary
    /// page (so changing a filter on the pane re-renders the bestiary
    /// instantly).
    /// </summary>
    public static AggregationFilter BuildSafeFilter() =>
        BuildSafeFilterCore(AscensionMin, AscensionMax, VersionMin, VersionMax, FilterProfile, IncludeMultiplayer);

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
        BuildSafeFilterCore(DefaultAscensionMin, DefaultAscensionMax, DefaultVersionMin, DefaultVersionMax, DefaultFilterProfile, DefaultIncludeMultiplayer);

    private static AggregationFilter BuildSafeFilterCore(int ascMin, int ascMax, string versionMin, string versionMax, string profile, bool includeMultiplayer)
    {
        var filter = new AggregationFilter();
        if (includeMultiplayer) filter.GameMode = null!;

        // Stash raw values verbatim for footer rendering — FilterDisplayRaw
        // preserves the sentinels that ResolveAscensionBounds collapses.
        filter.Display.RawAscMin   = ascMin;
        filter.Display.RawAscMax   = ascMax;
        filter.Display.RawVerMin   = versionMin ?? "";
        filter.Display.RawVerMax   = versionMax ?? "";
        filter.Display.RawProfile  = profile ?? "";

        var (resolvedMin, resolvedMax) = ResolveAscensionBounds(ascMin, ascMax);
        if (resolvedMin.HasValue && resolvedMax.HasValue && resolvedMin > resolvedMax)
            (resolvedMin, resolvedMax) = (null, null);
        filter.AscensionMin = resolvedMin;
        filter.AscensionMax = resolvedMax;

        var (resolvedVerMin, resolvedVerMax) = ResolveVersionBounds(versionMin, versionMax);
        filter.VersionMin = resolvedVerMin;
        filter.VersionMax = resolvedVerMax;
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
        // No range clamp on ascension: the data drives what's valid (mods may
        // introduce negative or 20+ ascensions). Only the cross-check stays
        // — a min > max is unambiguously broken; reset to unbounded sentinels.
        if (AscensionMin > AscensionMax) { AscensionMin = AscensionLowest; AscensionMax = AscensionHighest; }
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
