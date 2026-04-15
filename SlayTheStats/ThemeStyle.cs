namespace SlayTheStats;

/// <summary>
/// Canonical text-style constants for the mod's UI. Centralizing these keeps the
/// design language consistent across bestiary, tooltips, compendium filter, and
/// popups. Prefer these over ad-hoc hex literals or RGB Color constructors.
/// </summary>
internal static class ThemeStyle
{
    // ── Colors (BBCode hex strings; use directly in [color=...] tags) ──

    /// <summary>Primary body text. Warm off-white borrowed from the game's tooltip family.</summary>
    public const string Cream = "#fef6e2";

    /// <summary>Golden accent for titles, keyword highlights, hover/active button text.</summary>
    public const string Gold = "#efc851";

    /// <summary>Muted grey for footer / filter-context / hover-prompt text. One source of truth for "secondary metadata".</summary>
    public const string FooterGrey = "#686868";

    /// <summary>Column-header grey for stats tables. Warmer than footer grey so headers sit visually above data.</summary>
    public const string HeaderGrey = "#8e8676";

    /// <summary>Neutral placeholder for below-sample-threshold cells and dashes. Shared across card/relic/encounter tooltips.</summary>
    public const string NeutralShade = "#b5b5b5";

    // ── Title hierarchy (font sizes in px) ──
    // Primary = main panel title ("Bestiary", "Filters").
    // Secondary = secondary panel title ("All Encounters", tooltip header).
    // Subsection = popup title, table-context title, legend header.

    public const int TitlePrimary    = 26;
    public const int TitleSecondary  = 22;
    public const int TitleSubsection = 18;

    // ── Hint text ──
    // Matches the "(click anywhere to dismiss)" footer in the first-run
    // tutorial panels. Not italic, game font, 13px, warm muted grey.

    public const int HintSize = 13;

    /// <summary>Brand label ("SlayTheStats") text size in tooltip headers.</summary>
    public const int BrandSize = 14;

    /// <summary>RGBA tuple for hint text; apply via `new Color(r, g, b, 1f)`.</summary>
    public const float HintR = 0.75f;
    public const float HintG = 0.72f;
    public const float HintB = 0.65f;
}
