using Godot;

namespace SlayTheStats;

/// <summary>
/// Canonical text-style constants for the mod's UI. Centralizing these keeps the
/// design language consistent across bestiary, tooltips, compendium filter, and
/// popups. Prefer these over ad-hoc hex literals or RGB Color constructors.
/// Each color has a BBCode hex form (for `[color=...]` tags) and a `Color`
/// form (for `AddThemeColorOverride` and other `Color`-typed APIs).
/// </summary>
internal static class ThemeStyle
{
    // ── Colors (BBCode hex strings; use directly in [color=...] tags) ──

    /// <summary>Primary body text. Warm off-white borrowed from the game's tooltip family.</summary>
    public const string Cream = "#fef6e2";

    /// <summary>Golden accent for titles, keyword highlights, hover/active button text.</summary>
    public const string Gold = "#efc851";

    /// <summary>Slightly darker gold used for in-bestiary chrome titles and the card/relic tooltip header (pre-theft default). Borrowed from the game's keyword-panel title color; distinct from <see cref="Gold"/> by roughly one 5% brightness step. Keep both — reconciling them changes visual identity.</summary>
    public const string TitleGold = "#eabe51";

    /// <summary>Muted grey for footer / filter-context / hover-prompt text. One source of truth for "secondary metadata".</summary>
    public const string FooterGrey = "#686868";

    /// <summary>Column-header grey for stats tables. Warmer than footer grey so headers sit visually above data.</summary>
    public const string HeaderGrey = "#8e8676";

    /// <summary>Neutral placeholder for below-sample-threshold cells and dashes. Shared across card/relic/encounter tooltips.</summary>
    public const string NeutralShade = "#b5b5b5";

    // ── Colors (Godot Color values; use directly in AddThemeColorOverride, StyleBoxFlat.BgColor, etc.) ──

    public static readonly Color CreamColor        = new(Cream);
    public static readonly Color GoldColor         = new(Gold);
    public static readonly Color TitleGoldColor    = new(TitleGold);
    public static readonly Color FooterGreyColor   = new(FooterGrey);
    public static readonly Color HeaderGreyColor   = new(HeaderGrey);
    public static readonly Color NeutralShadeColor = new(NeutralShade);

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
