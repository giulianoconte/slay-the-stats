using System.Collections.Generic;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace SlayTheStats;

/// <summary>
/// Generates encounter stats text with rows per character.
///
/// All-characters view (bestiary): [table=8] layout with Dmg%, Mid 50% (of Dmg%),
/// Spread (of Dmg%), Turns, Pots, Deaths. Damage values are normalized by the
/// character's starting max HP so cross-character rows are comparable.
///
/// In-combat single-row: Runs | Dmg | Mid 50% | Spread | Turns (raw damage, no Dmg% —
/// single-character context where HP normalization isn't needed).
///
/// Coloration:
///   Dmg/Dmg% — ColBad against dmgPct baseline. High = orange/red, low = teal/green.
///   Mid 50%  — neutral (informational range).
///   Spread   — ColBad(iqrc, iqrcBaseline). High IQRC = swingy = orange/red.
///   Deaths   — Numerator colored by ColBad(deathRate, deathRateBaseline, n).
///   N, Turns, Pots — neutral.
/// </summary>
public static class EncounterTooltipHelper
{
    // Character display order — fixed list so rows are consistent
    private static readonly (string id, string label)[] CharacterOrder =
    {
        ("CHARACTER.IRONCLAD", "Ironclad"),
        ("CHARACTER.SILENT", "Silent"),
        ("CHARACTER.REGENT", "Regent"),
        ("CHARACTER.NECROBINDER", "Necrobinder"),
        ("CHARACTER.DEFECT", "Defect"),
    };

    /// <summary>Public read-only view of the canonical character roster.</summary>
    public static IReadOnlyList<(string id, string label)> CanonicalCharacters => CharacterOrder;

    /// <summary>
    /// Builds encounter stats text with one row per character + an "All" total row.
    /// </summary>
    /// <param name="filter">When provided, the footer gains a second line containing the
    /// full filter context (asc range, version range, profile) via
    /// CardStatsTooltipPatch.BuildFilterContext. The asc prefix is dropped from the first
    /// footer line in that case to avoid duplication. Mirrors the pattern used by the
    /// compendium card/relic stat tables.</param>
    /// <param name="characterLabel">Character label fed to BuildFilterContext when the
    /// bestiary doesn't filter to a single character (defaults to "All chars").</param>
    /// <summary>Holds the three parts of the all-characters stats table: a sticky
    /// header row, scrollable character rows, and a sticky baseline (All) row.
    /// Each part is a self-contained [table=8] so it can be assigned to a separate
    /// RichTextLabel for independent positioning.</summary>
    internal struct AllCharsTableParts
    {
        public string Header;
        public string CharacterRows;
        public string BaselineRow;
        public string Footer;
        /// <summary>Sparkline textures, in marker-emission order. One
        /// entry per <c>SparklinePoc.SparklineMarker</c> in
        /// <see cref="CharacterRows"/>; nulls allowed for rows with
        /// insufficient data. Consumed by
        /// <c>SparklinePoc.PopulateLabelWithSparklines</c>.</summary>
        public List<ImageTexture?> Sparklines;
    }

    /// <summary>Focused-view table: same BBCode-plus-sparkline pairing
    /// as <see cref="AllCharsTableParts"/> but without the separate
    /// header/baseline/footer slots — the focused view packs everything
    /// into a single BBCode string.</summary>
    internal struct FocusedTableParts
    {
        public string Text;
        public List<ImageTexture?> Sparklines;
    }

    internal static AllCharsTableParts BuildEncounterStatsTextParts(
        Dictionary<string, EncounterEvent> charStats,
        double deathRateBaseline,
        double dmgPctBaseline,
        double iqrcBaseline,
        int? ascensionMin,
        int? ascensionMax,
        string categoryLabel,
        AggregationFilter? filter = null,
        string? characterLabel = null)
    {
        var startingHps = MainFile.Db.CharacterStartingHp;

        // --- Pass 1: collect per-character metrics to compute the All (baseline) row ---
        // We need the baseline values before we can color the per-character rows.
        var perCharMetrics = new List<AllCharsPerCharMetrics>();
        // Single ordered list of every character row to render. Each entry carries a
        // nullable (stat, startingHp) — null means "no data for this encounter, render as
        // empty row". Keeping all characters in one list (in canonical order + modded tail)
        // ensures row order is stable across encounters: whether Silent has data for this
        // encounter or not, Silent always renders in the same slot between Ironclad and
        // Regent. Previously we segregated into charEntries / emptyEntries and rendered
        // each group in order → canonical chars without data were pushed to the bottom,
        // which changed the per-row order per encounter.
        var allEntries = new List<(string charId, EncounterEvent? stat, int startingHp, string descriptor)>();
        int totalFought = 0;
        int totalDied = 0;

        var rendered = new HashSet<string>();
        foreach (var (charId, defaultLabel) in CharacterOrder)
        {
            rendered.Add(charId);
            var icon = CharacterIcon(charId, 30);
            var label = L.CharacterName(charId, defaultLabel);
            var descriptor = $"{icon}{KreonBoldFontTag}{label}[/b]";
            if (charStats.TryGetValue(charId, out var stat) && stat.Fought > 0)
            {
                int startingHp = startingHps.GetValueOrDefault(charId, 0);
                CollectPerCharMetrics(perCharMetrics, stat, startingHp);
                allEntries.Add((charId, stat, startingHp, descriptor));
                totalFought += stat.Fought;
                totalDied += stat.Died;
            }
            else
            {
                allEntries.Add((charId, null, 0, descriptor));
            }
        }

        // Modded characters (not in CharacterOrder) — appended at the end, sorted by id
        // so order is still stable across encounters. Only rendered if they have data.
        foreach (var (charId, stat) in charStats.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (rendered.Contains(charId)) continue;
            if (stat.Fought == 0) continue;
            var icon = CharacterIcon(charId, 30);
            var label = FormatUnknownCharLabel(charId);
            int startingHp = startingHps.GetValueOrDefault(charId, 0);
            CollectPerCharMetrics(perCharMetrics, stat, startingHp);
            allEntries.Add((charId, stat, startingHp, $"{icon}{KreonBoldFontTag}{label}[/b]"));
            totalFought += stat.Fought;
            totalDied += stat.Died;
        }

        // Compute the All row baselines from per-character metrics.
        var allBaseline = ComputeAllRowBaselines(perCharMetrics);

        // Build header + character rows + baseline as ONE [table=8]. A single table
        // guarantees consistent column widths by construction (Godot sizes columns from
        // the same content pool), avoiding the drift we saw with three independent tables.
        // When content exceeds the available height, everything scrolls together — the
        // header and baseline rows scroll with the character rows.
        // Descriptor parts + data parts come from class-level constants; label width is
        // pinned by BestiaryPatch (_statsCharRowsLabel) to AllCharsTableWidthPx so the
        // expand-ratio math resolves to absolute pixel widths per column.
        var P = AllCharsColumns;
        var body = new StringBuilder();
        body.Append("[table=9]");
        AppendSizingRow(body, AllCharsColumns, descParts: AllCharsDescriptorParts);

        // Header row
        AppendDescriptorCell(body, " ", isHeader: true, parts: AllCharsDescriptorParts);
        AppendHeaderCell(body, L.T("tooltip.col.fights"),    P.Fought);
        AppendHeaderCell(body, L.T("tooltip.col.dist"),    P.Spark);  // density distribution sparkline
        AppendHeaderCell(body, L.T("tooltip.col.dmg"), P.Dmg);
        AppendHeaderCell(body, L.T("tooltip.col.mid50"),   P.Mid50);
        AppendHeaderCell(body, L.T("tooltip.col.spread"),  P.Spread);
        AppendHeaderCell(body, L.T("tooltip.col.turns"),   P.Turns);
        AppendHeaderCell(body, L.T("tooltip.col.potions"), P.Pots);
        AppendHeaderCell(body, L.T("tooltip.col.deaths"),  P.Deaths);

        // Character rows — render in canonical order, emitting data cells for chars that
        // have a stat for this encounter and empty cells for those that don't.
        // Every data-cells call (full or empty) emits exactly one Spark
        // marker, so the sparklines list must gain one entry per row —
        // null for no-data rows, a texture per row with values.
        // Two-pass sparkline build: collect Dmg% values per row first, then
        // materialize all textures with a single shared x-domain (see
        // BuildSparklinesFromValues for why).
        var sparklineValues = new List<double[]?>();
        foreach (var (charId, stat, startingHp, descriptor) in allEntries)
        {
            AppendDescriptorCell(body, descriptor, parts: AllCharsDescriptorParts);
            if (stat != null)
            {
                AppendAllCharsDataCells(body, stat!, startingHp, allBaseline);
                sparklineValues.Add(GetDmgPctValuesFromRawValues(stat.DamageValues, startingHp));
            }
            else
            {
                AppendEmptyDataCells(body);
                sparklineValues.Add(null);
            }
        }

        // Baseline (All) row — pool every per-character Dmg% value
        // across the characters that had data. Gives a cross-character
        // density for this encounter alongside the baseline numbers.
        var allIcon = AllCharsIcon(22);
        AppendDescriptorCell(body, $"{allIcon}{KreonBoldFontTag}{TooltipHelper.TotalLabel()}[/b] {L.T("descriptor.baseline_suffix")}", parts: AllCharsDescriptorParts);
        if (perCharMetrics.Count > 0)
        {
            AppendAllRowFromPerCharMetrics(body, perCharMetrics, totalFought);
            sparklineValues.Add(GetPooledAllRowDmgPctValues(allEntries));
        }
        else
        {
            AppendEmptyDataCells(body);
            sparklineValues.Add(null);
        }
        var sparklines = BuildSparklinesFromValues(sparklineValues);

        body.Append("[/table]");

        // --- Footer (kept separate so it can stay sticky below the scroll area) ---
        var footer = new StringBuilder();
        var filterCtx = filter != null
            ? CardHoverShowPatch.BuildFilterContext(characterLabel ?? "All chars", filter)
            : "";
        var footerStr = TooltipHelper.FormatFooter(filterCtx);
        if (footerStr.StartsWith("\n")) footerStr = footerStr.Substring(1);
        footer.Append(footerStr);

        return new AllCharsTableParts
        {
            Header = "",
            CharacterRows = body.ToString(),
            BaselineRow = "",
            Footer = footer.ToString(),
            Sparklines = sparklines,
        };
    }

    /// <summary>
    /// Builds a Dmg% values array from per-fight damage normalized by the
    /// character's starting HP. Dmg% values pool correctly across
    /// characters (in the all-chars baseline row) and match the column
    /// header ("Dmg%"). Returns null when there aren't enough samples.
    /// Texture build is deferred to <see cref="BuildSparklinesFromValues"/>
    /// so all rows in the table can share one x-domain.
    /// </summary>
    private static double[]? GetDmgPctValuesFromRawValues(List<int>? rawDamage, int startingHp)
    {
        if (rawDamage == null || rawDamage.Count < 2) return null;
        if (startingHp <= 0) return null;
        var values = new double[rawDamage.Count];
        for (int i = 0; i < values.Length; i++) values[i] = rawDamage[i] * 100.0 / startingHp;
        return values;
    }

    /// <summary>
    /// Builds a Dmg% values array from an already-pooled list of Dmg%
    /// integers (e.g. from <see cref="StatsAggregator.CollectDmgPctValues"/>).
    /// Null on fewer than 2 samples. Texture build is deferred — see
    /// <see cref="GetDmgPctValuesFromRawValues"/>.
    /// </summary>
    private static double[]? GetDmgPctValuesFromPctList(IReadOnlyList<int>? pctValues)
    {
        if (pctValues == null || pctValues.Count < 2) return null;
        var values = new double[pctValues.Count];
        for (int i = 0; i < values.Length; i++) values[i] = pctValues[i];
        return values;
    }

    /// <summary>
    /// Pools every per-character fight into a single Dmg% values array
    /// for the "All (baseline)" row of the all-chars table. Each
    /// character contributes its fights normalized by its own starting
    /// HP so the distribution is cross-character-comparable.
    /// </summary>
    private static double[]? GetPooledAllRowDmgPctValues(
        List<(string charId, EncounterEvent? stat, int startingHp, string descriptor)> entries)
    {
        var pooled = new List<int>();
        foreach (var (_, stat, startingHp, _) in entries)
        {
            if (stat?.DamageValues == null || startingHp <= 0) continue;
            foreach (var dmg in stat.DamageValues) pooled.Add(dmg * 100 / startingHp);
        }
        return GetDmgPctValuesFromPctList(pooled);
    }

    /// <summary>
    /// Materializes <see cref="ImageTexture"/>s for every collected row's
    /// Dmg% values, using a single x-domain computed from the union of
    /// all values. With one shared x-axis, the polyline's horizontal
    /// color gradient (green→red along x) anchors to the same dmg% in
    /// every row, so the eye can compare distributions cross-row by color
    /// alone. Returns nulls in slots whose values were null.
    /// </summary>
    private static List<ImageTexture?> BuildSparklinesFromValues(IReadOnlyList<double[]?> valuesPerRow)
    {
        var range = SparklinePoc.ComputeSharedXRange(valuesPerRow);
        var result = new List<ImageTexture?>(valuesPerRow.Count);
        foreach (var values in valuesPerRow)
        {
            if (values == null)
            {
                result.Add(null);
                continue;
            }
            result.Add(SparklinePoc.BuildSparklineTexture(
                values,
                BestiarySparklineSize,
                SparklinePoc.MarkerStyle.ShadedIqrMedianRule,
                range));
        }
        return result;
    }

    // Texture render size for the sparkline. 2× the displayed-cell footprint
    // (50×22) so each visible pixel averages 4 source pixels — the AA brush
    // in SparklinePoc gets more room to spread coverage and Godot's bilinear
    // filter (set on the host RichTextLabel via TextureFilter=Linear)
    // downsamples to the cell size cleanly. The display size passed to
    // AddImage stays 50×22 so the column-width math is unchanged.
    private static readonly Vector2I BestiarySparklineSize = new Vector2I(100, 44);
    internal const int BestiarySparklineDisplayW = 50;
    internal const int BestiarySparklineDisplayH = 22;

    // Calibration: U+2003 EM SPACE at [font_size=1] does NOT advance at 1
    // device pixel — empirically it advances at ~1.67 px in Kreon (54 em-spaces
    // in a Spark cell with no other content rendered as a ~90 px column).
    // The sizing-row mechanism below converts target-px into em-space count
    // by dividing by this ratio so `widthPx` actually maps to `widthPx`
    // pixels of column floor. Tune if column widths drift after font or
    // theme changes — verify by setting a known target (e.g.
    // PartsToPx(SparkParts) = 54) and measuring the rendered cell.
    //
    // Locale caveat: this calibration is Kreon-specific. For locales where the
    // RichTextLabel font is swapped to a substitute (rus/pol → Fira Sans
    // Condensed; jpn/kor/zhs/tha → Noto-family), U+2003 renders closer to spec
    // (~1 em = font_size, so ~1 px at fs=1) and the floor would collapse to
    // ~60% of the intended px. Sizing-row em-spaces are therefore wrapped in
    // `[font=KreonRegularPath]` to pin the metric to Kreon in every locale —
    // the content is invisible (fs=1) so font choice has no visual effect.
    private const float EmSpacePxAtFs1 = 1.67f;
    private static readonly string SizingFontOpen  = $"[font={TooltipHelper.KreonRegularPath}]";
    private const           string SizingFontClose = "[/font]";


    // Section colors for focused-character view. Row descriptors (labels like
    // "All (baseline)", pool names, character names) get Cream — they are the
    // primary content of their row and should read clearly. Context-row data
    // cells (rows 2, 3, 4) stay in footer grey so the data recedes relative to
    // the descriptor, and so the colored row 1 (subject) + colored per-character
    // rows in the all-chars table draw the eye. This split cues the reader that
    // context rows are "reference material" while colored rows are the signal.
    // Section colors all currently resolve to ThemeStyle.Cream — named
    // separately so the design can diverge them per-section without a
    // call-site sweep. Context rows use FooterGrey to recede visually.
    private const string BaselineSectionColor = ThemeStyle.Cream;
    private const string PoolSectionColor     = ThemeStyle.Cream;
    private const string AllCharsSectionColor = ThemeStyle.Cream;
    private const string ContextRowDataColor  = ThemeStyle.FooterGrey;

    // BBCode constants used across the focused-view table cells.
    // Descriptor font is slightly bigger than the data cells so it reads as the
    // label for the row; data stays at the base mono font size (18 from the
    // RichTextLabel theme override).
    //
    // Both regular and bold variants inherit the font from the parent
    // RichTextLabel's normal_font / bold_font theme overrides — which route
    // through TooltipHelper.GetKreonFont()/GetKreonBoldFont() and carry the
    // locale-aware FontVariation with CJK fallback. Hardcoding the font path
    // in BBCode bypasses that and breaks CJK glyph coverage.
    private const string KreonRegFontTag  = "[font_size=18]";
    private const string KreonRegClose    = "[/font_size]";
    private const string KreonBoldFontTag = "[b]";
    private const string HeaderColor      = ThemeStyle.HeaderGrey;
    // Minimum baseline for potion coloration. Prevents extreme colors from tiny
    // absolute differences by ensuring the ratio denominator isn't near-zero.
    // Complements PotionKScale — the floor handles small baselines, the k-scale
    // dampens the color intensity at any deviation.
    private const double PotionBaselineFloor = 0.1;
    // Dampens the k factor for the potions column specifically. Potion averages
    // (0.0–1.5 per fight) produce very large pctOfBaseline deviations even for
    // semantically small differences, which tanh saturates quickly at the default
    // k. Scaling the deviation by 0.3 before the significance calculation means
    // only substantially-different potion usage reaches vivid colors.
    private const double PotionKScale = 0.3;
    // Per-cell padding controls inter-column gaps. Godot [cell padding] format
    // is `left,top,right,bottom`. Adjacent cells' right + left pad sum to the
    // visual gap. Three tiers of tightness:
    //   • Tight: columns within a logical triple (Dmg/Mid50/Spread and
    //     Turns/Potions/Deaths) sit close together as a group.
    //   • Normal: gap between Fought↔Dmg and Spread↔Turns — separates groups.
    //   • Descriptor: gap between the descriptor column and Fought.
    private const string TightCellPadding      = "padding=0,0,0,0";   // 0 inter-data-col padding — rely on content widths
    private const string NormalCellPadding      = "padding=6,0,6,0";   // legacy — kept for wide (all-chars) table
    private const string DescriptorCellPadding  = "padding=0,0,0,0";   // descriptor → Fought (gap comes entirely from FoughtCellPadding's left)
    /// <summary>First data column (Runs/Fought) in the focused-view table. LEFT padding
    /// is the only inter-column gap we add — it separates the descriptor column from
    /// the data block. RIGHT padding is 0 so data columns are as tight as possible;
    /// inter-data-col spacing comes entirely from the text content minimums.</summary>
    private const string FoughtCellPadding      = "padding=2,0,0,0";

    /// <summary>Column widths in absolute pixels, expressed as "parts" where one part
    /// = <see cref="PartSizePx"/> / 2 pixels (i.e. ~13.5 px each). The doubled-units
    /// scheme allows ~25% column-width steps (1 part change on a 4-part column) where
    /// the prior single-unit scheme only allowed ~50% steps (1 part change on a
    /// 2-part column). Use <see cref="PartsToPx"/> to compute pixel widths.
    /// Mechanism: each cell carries an <c>expand</c> ratio (Godot requires it), and
    /// the caller pins the RichTextLabel's total width to <c>PartsToPx(total_parts)</c>.
    /// With that pinning, each column's expand allocation resolves to
    /// <c>(parts / total_parts) × tableWidthPx = PartsToPx(parts)</c> — independent
    /// of which table or its total parts. Data columns therefore render pixel-
    /// identical across focused and all-chars tables even when descriptor parts
    /// differ. Leftover label width sits unused to the right.
    /// Width pinning is done in <c>BestiaryPatch</c>: focused via <see cref="FocusedTableWidthPx"/>
    /// on <c>_statsLabel.CustomMinimumSize</c>; all-chars via <see cref="AllCharsTableWidthPx"/>
    /// in <c>SetupStatsCharRowsLayout</c>.
    /// Fallback: if any column's real content width exceeds <c>PartsToPx(parts)</c>
    /// that column grows to fit (or text wraps). Accepted as an edge case — Mid50
    /// "999-999%" ≈ 85px may briefly push 6 parts (~81 px) → grows a few px for
    /// those rows.</summary>
    private const int PartSizePx = 27;

    /// <summary>Converts a part count to pixels under the doubled-units scheme:
    /// <c>parts × PartSizePx / 2</c>. Even part counts are exact (e.g. 4 parts = 54 px),
    /// odd counts truncate by 0.5 px (e.g. 5 parts = 67 px, conceptually 67.5).</summary>
    private static int PartsToPx(int parts) => parts * PartSizePx / 2;

    /// <summary>Per-column parts + padding, parameterized so focused and all-chars views
    /// can use different values per column. Stores the parts alongside the pre-formatted
    /// BBCode cell string so <see cref="AppendSizingRow"/> can derive the em-space pixel
    /// floor for each column without re-specifying the numbers.</summary>
    private struct ColumnPaddings
    {
        public readonly int FoughtParts;
        public readonly int SparkParts;
        public readonly int DmgParts;
        public readonly int Mid50Parts;
        public readonly int SpreadParts;
        public readonly int TurnsParts;
        public readonly int PotsParts;
        public readonly int DeathsParts;

        public readonly string Fought;
        public readonly string Spark;
        public readonly string Dmg;
        public readonly string Mid50;
        public readonly string Spread;
        public readonly string Turns;
        public readonly string Pots;
        public readonly string Deaths;

        public ColumnPaddings(int fought, int spark, int dmg, int mid50, int spread, int turns, int pots, int deaths)
        {
            FoughtParts = fought;
            SparkParts = spark;
            DmgParts = dmg;
            Mid50Parts = mid50;
            SpreadParts = spread;
            TurnsParts = turns;
            PotsParts = pots;
            DeathsParts = deaths;
            Fought = $"expand={fought} {FoughtCellPadding}";
            Spark  = $"expand={spark} {TightCellPadding}";
            Dmg    = $"expand={dmg} {TightCellPadding}";
            Mid50  = $"expand={mid50} {TightCellPadding}";
            Spread = $"expand={spread} {TightCellPadding}";
            Turns  = $"expand={turns} {TightCellPadding}";
            Pots   = $"expand={pots} {TightCellPadding}";
            Deaths = $"expand={deaths} {TightCellPadding}";
        }

        public int SumDataParts => FoughtParts + SparkParts + DmgParts + Mid50Parts + SpreadParts
                                   + TurnsParts + PotsParts + DeathsParts;

        /// <summary>Returns a copy with the named columns overridden, all other columns
        /// preserved. Lets per-locale entries in <see cref="FocusedByLocale"/> /
        /// <see cref="AllCharsByLocale"/> express themselves as deltas off the English
        /// default — e.g. <c>FocusedDefault.With(turns: 3)</c> — instead of restating
        /// every column.</summary>
        public ColumnPaddings With(int? fought = null, int? spark = null, int? dmg = null,
            int? mid50 = null, int? spread = null, int? turns = null, int? pots = null, int? deaths = null)
            => new(
                fought:  fought  ?? FoughtParts,
                spark:   spark   ?? SparkParts,
                dmg:     dmg     ?? DmgParts,
                mid50:   mid50   ?? Mid50Parts,
                spread:  spread  ?? SpreadParts,
                turns:   turns   ?? TurnsParts,
                pots:    pots    ?? PotsParts,
                deaths:  deaths  ?? DeathsParts);
    }

    // English (default) part distribution. Both views share the same data columns so
    // data values render at identical widths across the focused and all-chars tables.
    // Distribution (doubled units; 1 part = ~13.5 px):
    //   Fought:4  Spark:4  Dmg:4  Mid50:6  Spread:5  Turns:5  Pots:4  Deaths:6.
    // Total data parts = 38. All-chars total = 12 desc + 38 data = 50 parts = 675 px;
    // focused total = 22 desc + 38 data = 60 parts = 810 px.
    //
    // Per-column pixel widths (PartsToPx):
    //   Fought=54  Spark=54  Dmg=54  Mid50=81  Spread=67  Turns=67  Pots=54  Deaths=81.
    // Mid50 "X-Y%" can reach 7-8 chars (e.g. "50-100%") so it gets 6 parts; Deaths
    // "12/42" also gets 6. Turns previously sat at 4 parts (54 px) but ran too close
    // to Spread visually — bumped to 5; Spread dropped from 6 to 5 to compensate so
    // the data-parts total stays at 38.
    private static readonly ColumnPaddings FocusedDefault =
        new(fought: 4, spark: 4, dmg: 4, mid50: 6, spread: 5, turns: 5, pots: 4, deaths: 6);
    private static readonly ColumnPaddings AllCharsDefault =
        new(fought: 4, spark: 4, dmg: 4, mid50: 6, spread: 5, turns: 5, pots: 4, deaths: 6);

    // Per-locale overrides. English headers fit the defaults above; substitution-locale
    // headers (rus/pol → Fira Sans Condensed; jpn/kor/zhs/tha → Noto-family) have
    // different per-character widths and per-string lengths, so each language can
    // shrink (or grow) any individual column without touching others. Use
    // <c>FocusedDefault.With(turns: N)</c> for partial overrides. Add entries
    // measurement-driven — open the bestiary in-locale and trim columns with
    // visible slack until the header sits comfortably above the data without
    // crowding its neighbour.
    private static readonly Dictionary<string, ColumnPaddings> FocusedByLocale = new()
    {
        // "Ходы" (Russian "Turns") is appreciably narrower than English "Turns" and
        // sits on top of the same 1-2 digit data values; provisional 3 parts (~40 px)
        // pending visual confirmation. Adjust if data values get clipped.
        ["rus"] = FocusedDefault.With(turns: 3),
    };
    private static readonly Dictionary<string, ColumnPaddings> AllCharsByLocale = new()
    {
        ["rus"] = AllCharsDefault.With(turns: 3),
    };

    private static ColumnPaddings FocusedColumns
    {
        get
        {
            var lang = LocManager.Instance?.Language;
            return lang != null && FocusedByLocale.TryGetValue(lang, out var p) ? p : FocusedDefault;
        }
    }

    private static ColumnPaddings AllCharsColumns
    {
        get
        {
            var lang = LocManager.Instance?.Language;
            return lang != null && AllCharsByLocale.TryGetValue(lang, out var p) ? p : AllCharsDefault;
        }
    }

    /// <summary>Focused-view descriptor parts — must match the default on
    /// <see cref="AppendDescriptorCell"/> and <see cref="AppendSizingRow"/>.</summary>
    public const int FocusedDescriptorParts = 22;
    public static int FocusedTotalParts   => FocusedDescriptorParts + FocusedColumns.SumDataParts;
    public static int FocusedTableWidthPx => PartsToPx(FocusedTotalParts);
    /// <summary>All-chars-view descriptor parts — narrower than focused since character-
    /// name descriptors are shorter than focused's pool labels.</summary>
    public const int AllCharsDescriptorParts = 12;
    public static int AllCharsTotalParts   => AllCharsDescriptorParts + AllCharsColumns.SumDataParts;
    public static int AllCharsTableWidthPx => PartsToPx(AllCharsTotalParts);

    private static string EmptyDataCell(string padding, string? color = null, string align = "right")
    {
        var c = color ?? TooltipHelper.NeutralShade;
        return $"[cell {padding}][{align}][color={c}]-[/color][/{align}][/cell]";
    }

    /// <summary>Descriptor cell in the focused-view / all-chars [table=8] — always the first
    /// cell of each row. `parts` is the column's expand ratio (plus drives the sizing row's
    /// pixel floor via <see cref="AppendSizingRow"/>). Focused view uses parts=11 by default;
    /// all-chars passes parts=5.</summary>
    private static void AppendDescriptorCell(StringBuilder sb, string text, bool isHeader = false, int parts = 22)
    {
        var color = isHeader ? HeaderColor : BaselineSectionColor;
        sb.Append($"[cell expand={parts} {DescriptorCellPadding}]{KreonRegFontTag}[color={color}]{text}[/color]{KreonRegClose}[/cell]");
    }

    /// <summary>Emits an invisible 1px-tall sizing row at the top of a [table=9] to set
    /// column widths in absolute pixels. Each cell contains U+2003 em-space characters at
    /// font_size=1 (calibrated px per em-space — see <see cref="EmSpacePxAtFs1"/>). Widths
    /// are expressed in "parts" where 1 part = <see cref="PartSizePx"/>/2 pixels (doubled-
    /// units scheme), and data-column parts are shared (FoughtParts, DmgParts, …).
    /// Descriptor parts are per-view: focused passes 22, all-chars passes 12. A column's
    /// final width = max(<see cref="PartsToPx"/>(parts), widest real content in that
    /// column); content override can happen when real text at font 18 exceeds the parts-
    /// based floor (Mid50 "999-999%" is the notable case).</summary>
    /// <summary>Overload that selects the focused view's ColumnPaddings by default.</summary>
    private static void AppendSizingRow(StringBuilder sb, int descParts = 22)
    {
        AppendSizingRow(sb, FocusedColumns, descParts);
    }

    private static void AppendSizingRow(StringBuilder sb, ColumnPaddings P, int descParts)
    {
        // Sizing row cells must carry the SAME expand attribute as their real-row counterparts
        // — mismatch would cause Godot to treat the sizing cell as a separate column with
        // different expand, breaking the layout.
        sb.Append($"[cell expand={descParts} {DescriptorCellPadding}]{SizingFontOpen}[font_size=1]{EmSpacesForPx(PartsToPx(descParts))}[/font_size]{SizingFontClose}[/cell]");
        AppendSizingDataCell(sb, P.Fought, PartsToPx(P.FoughtParts));
        AppendSizingDataCell(sb, P.Spark,  PartsToPx(P.SparkParts));
        AppendSizingDataCell(sb, P.Dmg,    PartsToPx(P.DmgParts));
        AppendSizingDataCell(sb, P.Mid50,  PartsToPx(P.Mid50Parts));
        AppendSizingDataCell(sb, P.Spread, PartsToPx(P.SpreadParts));
        AppendSizingDataCell(sb, P.Turns,  PartsToPx(P.TurnsParts));
        AppendSizingDataCell(sb, P.Pots,   PartsToPx(P.PotsParts));
        AppendSizingDataCell(sb, P.Deaths, PartsToPx(P.DeathsParts));
    }

    private static void AppendSizingDataCell(StringBuilder sb, string padding, int widthPx)
    {
        sb.Append($"[cell {padding}]{SizingFontOpen}[font_size=1]{EmSpacesForPx(widthPx)}[/font_size]{SizingFontClose}[/cell]");
    }

    private static string EmSpaces(int count) => new string('\u2003', count);

    // Emit enough em-spaces (at [font_size=1]) to span <paramref name="widthPx"/>
    // pixels of column min-content floor \u2014 see <see cref="EmSpacePxAtFs1"/> for
    // why the count is the target px divided by the per-em-space advance.
    private static string EmSpacesForPx(int widthPx)
        => EmSpaces(widthPx <= 0 ? 0 : (int)System.Math.Round(widthPx / EmSpacePxAtFs1));

    /// <summary>Column header cell. Shares the surrounding [table=N] layout with
    /// data cells so Godot's table layout aligns column widths across rows. Right-
    /// aligned to sit over right-aligned numbers.</summary>
    private static void AppendHeaderCell(StringBuilder sb, string name, string padding = NormalCellPadding, string align = "right")
    {
        sb.Append($"[cell {padding}][{align}][color={HeaderColor}]{name}[/color][/{align}][/cell]");
    }

    /// <summary>Row 1 (subject encounter) data cells. Colored against the
    /// encounter-weighted act-pool baseline using the existing ColBad/
    /// ColBadRelative helpers so the familiar significance gradient still
    /// applies: high=bad (orange/red), low=good (teal/green).</summary>
    private static void AppendRow1DataCells(StringBuilder sb, EncounterEvent stat,
        double medianBase, double iqrcBase, double deathRateBase, double turnsBase, double potsBase)
    {
        int n = stat.Fought;
        var P = FocusedColumns;
        if (n == 0)
        {
            sb.Append(EmptyDataCell(P.Fought));
            AppendSparkMarkerCell(sb, P);
            sb.Append(EmptyDataCell(P.Dmg));
            sb.Append(EmptyDataCell(P.Mid50));  sb.Append(EmptyDataCell(P.Spread));

            sb.Append(EmptyDataCell(P.Turns));  sb.Append(EmptyDataCell(P.Pots));
            sb.Append(EmptyDataCell(P.Deaths));
            return;
        }

        double deathRate = 100.0 * stat.Died / n;
        double avgTurns  = (double)stat.TurnsTakenSum / n;
        double avgPots   = (double)stat.PotionsUsedSum / n;
        double dmgMedian = stat.DamageMedian() ?? (double)stat.DamageTakenSum / n;
        var iqr = stat.DamageIQR();
        string fIqr = iqr.HasValue ? $"{iqr.Value.p25:F0}-{iqr.Value.p75:F0}" : "-";

        var cN       = TooltipHelper.ColN($"{n}", n);
        var cDmg     = ColBadRelative($"{dmgMedian:F0}", dmgMedian, medianBase, n);
        var cMid50   = $"[color={TooltipHelper.NeutralShade}]{fIqr}[/color]";
        var cSpread  = FormatSpreadCell(StatsAggregator.Iqrc(iqr, dmgMedian), n, iqrcBase);
        var cTurns   = ColBadRelative($"{avgTurns:F1}", avgTurns, turnsBase, n);
        var cPots    = ColBadRelative($"{avgPots:F1}", avgPots, potsBase, n, PotionBaselineFloor, PotionKScale);
        var cDeaths  = FormatDeathsCellInner(stat.Died, n, deathRate, deathRateBase);

        sb.Append($"[cell {P.Fought}][right]{cN}[/right][/cell]");
        AppendSparkMarkerCell(sb, P);

        sb.Append($"[cell {P.Dmg}][right]{cDmg}[/right][/cell]");
        sb.Append($"[cell {P.Mid50}][right]{cMid50}[/right][/cell]");
        sb.Append($"[cell {P.Spread}][right]{cSpread}[/right][/cell]");

        sb.Append($"[cell {P.Turns}][right]{cTurns}[/right][/cell]");
        sb.Append($"[cell {P.Pots}][right]{cPots}[/right][/cell]");
        sb.Append($"[cell {P.Deaths}][right]{cDeaths}[/right][/cell]");
    }

    /// <summary>Row 1 data cells when the subject is a PoolMetrics rather than
    /// an EncounterEvent (used by the category table, where row 1 is the
    /// char-filtered biome+category pool and the baseline is the same pool
    /// aggregated across all characters). Colored the same way as the encounter
    /// row-1 variant — ColBadRelative per metric against the baseline pool.</summary>
    private static void AppendRow1PoolDataCells(StringBuilder sb, PoolMetrics subject, PoolMetrics baseline)
    {
        int n = subject.Fought;
        var P = FocusedColumns;
        if (n == 0)
        {
            sb.Append(EmptyDataCell(P.Fought));
            // Still emit a Spark marker on no-data rows so the parts
            // list's 1:1 marker↔sparkline invariant holds. The caller
            // will push a null texture into the list for this slot.
            AppendSparkMarkerCell(sb, P);
            sb.Append(EmptyDataCell(P.Dmg));
            sb.Append(EmptyDataCell(P.Mid50));  sb.Append(EmptyDataCell(P.Spread));

            sb.Append(EmptyDataCell(P.Turns));  sb.Append(EmptyDataCell(P.Pots));
            sb.Append(EmptyDataCell(P.Deaths));
            return;
        }

        var (medianBase, iqrcBase, deathRateBase, turnsBase, potsBase) = DeriveBaselines(baseline);

        string fIqr = subject.IQR.HasValue
            ? $"{subject.IQR.Value.p25:F0}-{subject.IQR.Value.p75:F0}"
            : "-";

        var cN       = TooltipHelper.ColN($"{n}", n);
        var cDmg     = ColBadRelative($"{subject.Median:F0}", subject.Median, medianBase, n);
        var cMid50   = $"[color={TooltipHelper.NeutralShade}]{fIqr}[/color]";
        var cSpread  = FormatSpreadCell(subject.IQR.HasValue ? subject.Iqrc : (double?)null, n, iqrcBase);
        var cTurns   = ColBadRelative($"{subject.AvgTurns:F1}", subject.AvgTurns, turnsBase, n);
        var cPots    = ColBadRelative($"{subject.AvgPots:F1}", subject.AvgPots, potsBase, n, PotionBaselineFloor, PotionKScale);
        var cDeaths  = FormatDeathsCellInner(subject.Died, n, subject.DeathRate, deathRateBase);

        sb.Append($"[cell {P.Fought}][right]{cN}[/right][/cell]");
        AppendSparkMarkerCell(sb, P);

        sb.Append($"[cell {P.Dmg}][right]{cDmg}[/right][/cell]");
        sb.Append($"[cell {P.Mid50}][right]{cMid50}[/right][/cell]");
        sb.Append($"[cell {P.Spread}][right]{cSpread}[/right][/cell]");

        sb.Append($"[cell {P.Turns}][right]{cTurns}[/right][/cell]");
        sb.Append($"[cell {P.Pots}][right]{cPots}[/right][/cell]");
        sb.Append($"[cell {P.Deaths}][right]{cDeaths}[/right][/cell]");
    }

    /// <summary>Pool (rows 2/3/4) data cells. Dull neutral color across the
    /// whole row — no significance coloration, since these are reference rows
    /// that the subject row is being compared against.</summary>
    private static void AppendPoolDataCells(StringBuilder sb, PoolMetrics m)
    {
        int n = m.Fought;
        string color = ContextRowDataColor;
        var P = FocusedColumns;
        if (n == 0)
        {
            sb.Append(EmptyDataCell(P.Fought, color));
            // Marker emitted so parts.Sparklines stays 1:1 with slots.
            AppendSparkMarkerCell(sb, P);
            sb.Append(EmptyDataCell(P.Dmg, color));
            sb.Append(EmptyDataCell(P.Mid50, color));  sb.Append(EmptyDataCell(P.Spread, color));

            sb.Append(EmptyDataCell(P.Turns, color));  sb.Append(EmptyDataCell(P.Pots, color));
            sb.Append(EmptyDataCell(P.Deaths, color));
            return;
        }

        string fIqr    = m.IQR.HasValue ? $"{m.IQR.Value.p25:F0}-{m.IQR.Value.p75:F0}" : "-";
        string fSpread = m.IQR.HasValue ? $"{m.Iqrc * 100:F0}%" : "-";
        var fN         = $"{n}";
        var fDmg       = $"{m.Median:F0}";
        var fTurns     = $"{m.AvgTurns:F1}";
        var fPots      = $"{m.AvgPots:F1}";
        var fDeaths    = $"{m.Died}/{n}";

        sb.Append($"[cell {P.Fought}][right][color={color}]{fN}[/color][/right][/cell]");
        AppendSparkMarkerCell(sb, P);

        sb.Append($"[cell {P.Dmg}][right][color={color}]{fDmg}[/color][/right][/cell]");
        sb.Append($"[cell {P.Mid50}][right][color={color}]{fIqr}[/color][/right][/cell]");
        sb.Append($"[cell {P.Spread}][right][color={color}]{fSpread}[/color][/right][/cell]");

        sb.Append($"[cell {P.Turns}][right][color={color}]{fTurns}[/color][/right][/cell]");
        sb.Append($"[cell {P.Pots}][right][color={color}]{fPots}[/color][/right][/cell]");
        sb.Append($"[cell {P.Deaths}][right][color={color}]{fDeaths}[/color][/right][/cell]");
    }

    /// <summary>Formats the Spread cell for colored row-1 use. IQRC displayed as
    /// percent ("82%"), colored by ColBadLog against the IQRC baseline so ratio
    /// deviations are symmetric — a 4× swingier encounter colors with equal
    /// intensity to one 1/4 as swingy. High = swingy = bad (orange); low =
    /// consistent = good (teal). Returns neutral when there's no data.</summary>
    private static string FormatSpreadCell(double? iqrc, int n, double iqrcBaseline)
    {
        if (iqrc == null)
            return $"[color={TooltipHelper.NeutralShade}]-[/color]";
        string text = $"{iqrc.Value * 100:F0}%";
        return ColBadLog(text, iqrc.Value, iqrcBaseline, n);
    }

    /// <summary>Computes median of dmg% values: each raw DamageValues entry divided by
    /// startingHp, expressed as 0–100. Returns null if DamageValues is null/empty or
    /// startingHp is zero.</summary>
    private static double? DmgPctMedian(EncounterEvent stat, int startingHp)
    {
        if (startingHp <= 0 || stat.DamageValues == null || stat.DamageValues.Count == 0) return null;
        // Dividing by a positive constant preserves order, so median(x/c) = median(x)/c.
        double rawMedian = stat.DamageMedian() ?? 0;
        return rawMedian / startingHp * 100.0;
    }

    /// <summary>Computes IQR of dmg% values: p25 and p75 of raw damage divided by
    /// startingHp, expressed as 0–100. Returns null if insufficient data or startingHp
    /// is zero.</summary>
    private static (double p25, double p75)? DmgPctIQR(EncounterEvent stat, int startingHp)
    {
        if (startingHp <= 0) return null;
        var raw = stat.DamageIQR();
        if (!raw.HasValue) return null;
        return (raw.Value.p25 / startingHp * 100.0, raw.Value.p75 / startingHp * 100.0);
    }

    /// <summary>Per-character metrics collected during the character loop, used to build
    /// the character-weighted All row. Percentile metrics (dmgPct median/IQR) use
    /// median-of-medians; mean metrics (turns, pots, death rate) use mean-of-means.</summary>
    private struct AllCharsPerCharMetrics
    {
        public double DmgPctMedian;
        public double? IqrP25;
        public double? IqrP75;
        public double? Iqrc;        // per-character IQRC = (IqrP75 - IqrP25) / max(DmgPctMedian, 1)
        public double AvgTurns;
        public double AvgPots;
        public double DeathRate;
    }

    /// <summary>Computes per-character metrics and adds them to the list for the All row.</summary>
    private static void CollectPerCharMetrics(List<AllCharsPerCharMetrics> list, EncounterEvent stat, int startingHp)
    {
        if (stat.Fought <= 0) return;
        int n = stat.Fought;
        double? dmgPct = DmgPctMedian(stat, startingHp);
        var iqrPct = DmgPctIQR(stat, startingHp);
        double dmgPctValue = dmgPct ?? (stat.DmgPctSum / n * 100.0);
        double? perCharIqrc = iqrPct.HasValue
            ? (iqrPct.Value.p75 - iqrPct.Value.p25) / Math.Max(dmgPctValue, 1.0)
            : (double?)null;

        list.Add(new AllCharsPerCharMetrics
        {
            DmgPctMedian = dmgPctValue,
            IqrP25 = iqrPct?.p25,
            IqrP75 = iqrPct?.p75,
            Iqrc = perCharIqrc,
            AvgTurns = (double)stat.TurnsTakenSum / n,
            AvgPots = (double)stat.PotionsUsedSum / n,
            DeathRate = 100.0 * stat.Died / n,
        });
    }

    /// <summary>Renders the All (baseline) row from per-character metrics. Percentile
    /// damage columns use median-of-medians (each character contributes equally).
    /// Mean columns (turns, pots, death rate) use mean-of-means.</summary>
    private static void AppendAllRowFromPerCharMetrics(StringBuilder sb,
        List<AllCharsPerCharMetrics> metrics, int totalFought)
    {
        int charCount = metrics.Count;
        string color = ContextRowDataColor;
        var P = AllCharsColumns;

        // Median-of-medians for dmg%
        double allDmgPct = StatsAggregator.MedianOf(metrics.Select(m => m.DmgPctMedian).ToList());

        // Median-of-p25s and median-of-p75s for IQR
        var p25s = metrics.Where(m => m.IqrP25.HasValue).Select(m => m.IqrP25!.Value).ToList();
        var p75s = metrics.Where(m => m.IqrP75.HasValue).Select(m => m.IqrP75!.Value).ToList();
        (double p25, double p75)? allIqr = p25s.Count > 0 && p75s.Count > 0
            ? (StatsAggregator.MedianOf(p25s), StatsAggregator.MedianOf(p75s))
            : null;

        // Mean-of-means for turns, pots, death rate (character-weighted, matching
        // the baseline used to color per-character Deaths cells).
        double allTurns     = metrics.Average(m => m.AvgTurns);
        double allPots      = metrics.Average(m => m.AvgPots);
        double allDeathRate = metrics.Average(m => m.DeathRate);

        // Median-of-per-character-IQRCs (matches the unified Spread aggregation
        // used by StatsAggregator.AggregateMetricsFromEvents). Composes cleanly:
        // each character's displayed Spread is their own IQRC; medianing across
        // characters gives "typical character's Spread for this encounter".
        var perCharIqrcs = metrics.Where(m => m.Iqrc.HasValue).Select(m => m.Iqrc!.Value).ToList();
        double? allIqrc = perCharIqrcs.Count > 0 ? StatsAggregator.MedianOf(perCharIqrcs) : (double?)null;

        string fDmgPct  = $"{allDmgPct:F0}%";
        string fIqr     = allIqr.HasValue ? $"{allIqr.Value.p25:F0}-{allIqr.Value.p75:F0}%" : "-";
        string fSpread  = allIqrc.HasValue ? $"{allIqrc.Value * 100:F0}%" : "-";
        string fTurns   = $"{allTurns:F1}";
        string fPots    = $"{allPots:F1}";
        string fDeaths  = $"{allDeathRate:F0}%";

        sb.Append($"[cell {P.Fought}][right][color={color}]{totalFought}[/color][/right][/cell]");
        AppendSparkMarkerCell(sb, P);
        sb.Append($"[cell {P.Dmg}][right][color={color}]{fDmgPct}[/color][/right][/cell]");
        sb.Append($"[cell {P.Mid50}][right][color={color}]{fIqr}[/color][/right][/cell]");
        sb.Append($"[cell {P.Spread}][right][color={color}]{fSpread}[/color][/right][/cell]");
        sb.Append($"[cell {P.Turns}][right][color={color}]{fTurns}[/color][/right][/cell]");
        sb.Append($"[cell {P.Pots}][right][color={color}]{fPots}[/color][/right][/cell]");
        sb.Append($"[cell {P.Deaths}][right][color={color}]{fDeaths}[/color][/right][/cell]");
    }

    /// <summary>Baselines derived from the All row's character-weighted values.
    /// Used to color per-character rows by significance against the cross-character average.</summary>
    private struct AllRowBaselines
    {
        public double DmgPct;       // median-of-medians dmg%
        public double Iqrc;         // median of per-character IQRCs
        public double AvgTurns;     // mean-of-means turns
        public double AvgPots;      // mean-of-means potions
        public double DeathRate;    // mean-of-means death rate (0–100)
    }

    /// <summary>Computes the All row baseline values from per-character metrics.</summary>
    private static AllRowBaselines ComputeAllRowBaselines(List<AllCharsPerCharMetrics> metrics)
    {
        if (metrics.Count == 0)
            return new AllRowBaselines { DmgPct = 20, Iqrc = 1.0, AvgTurns = 3, AvgPots = 0.3, DeathRate = 10 };

        double dmgPct = StatsAggregator.MedianOf(metrics.Select(m => m.DmgPctMedian).ToList());

        var perCharIqrcs = metrics.Where(m => m.Iqrc.HasValue).Select(m => m.Iqrc!.Value).ToList();
        double iqrc = perCharIqrcs.Count > 0 ? StatsAggregator.MedianOf(perCharIqrcs) : 1.0;

        return new AllRowBaselines
        {
            DmgPct = dmgPct,
            Iqrc = iqrc,
            AvgTurns = metrics.Average(m => m.AvgTurns),
            AvgPots = metrics.Average(m => m.AvgPots),
            DeathRate = metrics.Average(m => m.DeathRate),
        };
    }

    /// <summary>Data cells for a per-character row in the all-characters table. Shows
    /// dmg% (damage / starting max HP) for the three damage columns. All columns are
    /// colored against the All row baselines (high dmg/spread/turns/pots/deaths = bad).</summary>
    private static void AppendAllCharsDataCells(StringBuilder sb, EncounterEvent stat, int startingHp,
        AllRowBaselines baselines)
    {
        int n = stat.Fought;
        var P = AllCharsColumns;
        if (n == 0 || startingHp <= 0)
        {
            AppendEmptyDataCells(sb);
            return;
        }

        double deathRate = 100.0 * stat.Died / n;
        double avgTurns  = (double)stat.TurnsTakenSum / n;
        double avgPots   = (double)stat.PotionsUsedSum / n;

        double? dmgPct = DmgPctMedian(stat, startingHp);
        var iqrPct = DmgPctIQR(stat, startingHp);

        // Fall back to mean dmg% if per-fight values not tracked
        double displayDmgPct = dmgPct ?? (stat.DmgPctSum / n * 100.0);

        string fDmgPct = $"{displayDmgPct:F0}%";
        string fIqr = iqrPct.HasValue ? $"{iqrPct.Value.p25:F0}-{iqrPct.Value.p75:F0}%" : "-";
        string fTurns = $"{avgTurns:F1}";
        string fPots  = $"{avgPots:F1}";

        var cN      = TooltipHelper.ColN($"{n}", n);
        var cDmg    = ColBad(fDmgPct, displayDmgPct, n, baselines.DmgPct);
        var cMid50  = $"[color={TooltipHelper.NeutralShade}]{fIqr}[/color]";
        var cSpread = FormatSpreadCell(StatsAggregator.Iqrc(iqrPct, displayDmgPct), n, baselines.Iqrc);
        var cTurns  = ColBadRelative(fTurns, avgTurns, baselines.AvgTurns, n);
        var cPots   = ColBadRelative(fPots, avgPots, baselines.AvgPots, n, PotionBaselineFloor, PotionKScale);
        var cDeaths = FormatDeathsCellInner(stat.Died, n, deathRate, baselines.DeathRate);

        sb.Append($"[cell {P.Fought}][right]{cN}[/right][/cell]");
        AppendSparkMarkerCell(sb, P);
        sb.Append($"[cell {P.Dmg}][right]{cDmg}[/right][/cell]");
        sb.Append($"[cell {P.Mid50}][right]{cMid50}[/right][/cell]");
        sb.Append($"[cell {P.Spread}][right]{cSpread}[/right][/cell]");
        sb.Append($"[cell {P.Turns}][right]{cTurns}[/right][/cell]");
        sb.Append($"[cell {P.Pots}][right]{cPots}[/right][/cell]");
        sb.Append($"[cell {P.Deaths}][right]{cDeaths}[/right][/cell]");
    }

    /// <summary>Appends a full row of empty "-" data cells for the [table=8] layout.
    /// Only called from the all-chars view (for characters with no data for this
    /// encounter), so hardcodes AllCharsColumns.</summary>
    private static void AppendEmptyDataCells(StringBuilder sb)
    {
        var P = AllCharsColumns;
        sb.Append(EmptyDataCell(P.Fought));
        // Marker emitted even for no-data rows so parts.Sparklines stays
        // 1:1 with marker slots in the BBCode. Caller pushes null for
        // these slots; the populate helper skips AddImage.
        AppendSparkMarkerCell(sb, P);
        sb.Append(EmptyDataCell(P.Dmg));
        sb.Append(EmptyDataCell(P.Mid50));
        sb.Append(EmptyDataCell(P.Spread));
        sb.Append(EmptyDataCell(P.Turns));
        sb.Append(EmptyDataCell(P.Pots));
        sb.Append(EmptyDataCell(P.Deaths));
    }

    /// <summary>Emits a Spark cell carrying the <see cref="SparklinePoc.SparklineMarker"/>
    /// token. The bestiary render path splits its BBCode on the marker and
    /// calls <c>RichTextLabel.AddImage</c> between the split segments. The
    /// <c>[right]</c> wrapper right-aligns the inline image within the cell so
    /// any leftover width (cell px - image px) sits on the left side, anchoring
    /// the curve flush to the next column's edge.</summary>
    private static void AppendSparkMarkerCell(StringBuilder sb, ColumnPaddings P)
    {
        sb.Append($"[cell {P.Spark}][right]{SparklinePoc.SparklineMarker}[/right][/cell]");
    }

    /// <summary>FormatDeathsCell variant for the new table layout — no padding,
    /// just the "died/fought" string with numerator colored by ColBad and
    /// denominator colored by ColN.</summary>
    private static string FormatDeathsCellInner(int died, int fought, double deathRate, double deathRateBaseline)
    {
        var cDied   = ColBad($"{died}", deathRate, fought, deathRateBaseline);
        var cFought = TooltipHelper.ColN($"{fought}", fought);
        return $"{cDied}[color={TooltipHelper.NeutralShade}]/[/color]{cFought}";
    }

    /// <summary>Pluralizes the lowercase category label used in row descriptors.
    /// Drops the "encounters" suffix entirely in favour of a shorter pluralized
    /// category: "normal encounters" → "normals", "elite encounters" → "elites",
    /// etc. Unknown/unexpected categories fall back to naive +"s".</summary>
    /// <summary>Localized plural form of a category (used in descriptor
    /// rows like "vs all elites"). Fallback on miss is English-naive "+s",
    /// which is noisy for non-English locales but translators can override
    /// every plural via the <c>category.*.plural</c> keys.</summary>
    private static string PluralizeCategory(string categoryLower)
    {
        var key = "category." + categoryLower + ".plural";
        var translated = L.T(key);
        if (translated != key) return translated;
        return categoryLower switch
        {
            "weak"   => "weaks",
            "normal" => "normals",
            "elite"  => "elites",
            "boss"   => "bosses",
            "event"  => "events",
            _        => categoryLower + "s",
        };
    }

    /// <summary>Returns just the inline character icon BBCode at a given size, without the
    /// trailing name. Returns empty string when the character has no recognised icon.</summary>
    internal static string CharacterIcon(string characterId, int sizePx)
    {
        var name = characterId.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
            ? characterId.Substring("CHARACTER.".Length)
            : characterId;
        var path = $"res://images/ui/top_panel/character_icon_{name.ToLowerInvariant()}.png";
        return Godot.ResourceLoader.Exists(path)
            ? $"[img={sizePx}x{sizePx}]{path}[/img] "
            : "";
    }

    // Cache the resolved Prismatic Gem icon path so we don't probe the resource loader
    // every time the all-chars row renders. Null = not yet probed; "" = no icon found.
    private static string? _allCharsIconPath;

    /// <summary>Returns inline BBCode for the Prismatic Gem relic icon — used as the
    /// "all characters" symbol on row 4. Tries the unpacked PNG path first, then the
    /// atlas .tres resource as a fallback. Result is cached for the session. Returns
    /// empty string if neither path resolves.</summary>
    internal static string AllCharsIcon(int sizePx)
    {
        if (_allCharsIconPath == null)
        {
            // Per RelicModel.BigIconPath: res://images/relics/{IconBaseName}.png
            // and PackedIconPath: res://images/atlases/relic_atlas.sprites/{IconBaseName}.tres
            // IconBaseName = Id.Entry.ToLowerInvariant() — and crucially,
            // Id.Entry comes from StringHelper.Slugify(typeof(T).Name) which inserts
            // underscores between camelCase boundaries before upper-casing. So
            // PrismaticGem → PRISMATIC_GEM → "prismatic_gem", NOT "prismaticgem".
            var candidates = new[]
            {
                "res://images/atlases/relic_atlas.sprites/prismatic_gem.tres",
                "res://images/relics/prismatic_gem.png",
                // Fallbacks for the old (buggy) naming, in case some build still uses it.
                "res://images/atlases/relic_atlas.sprites/prismaticgem.tres",
                "res://images/relics/prismaticgem.png",
            };
            _allCharsIconPath = "";
            foreach (var path in candidates)
            {
                if (Godot.ResourceLoader.Exists(path))
                {
                    _allCharsIconPath = path;
                    break;
                }
            }
            if (_allCharsIconPath.Length == 0)
                MainFile.Logger.Warn("[SlayTheStats] AllCharsIcon: failed to resolve PrismaticGem icon path; tried: "
                    + string.Join(", ", candidates));
        }

        return _allCharsIconPath.Length > 0
            ? $"[img={sizePx}x{sizePx}]{_allCharsIconPath}[/img] "
            : "";
    }

    /// <summary>
    /// Builds encounter stats for a focused single-character view. Section-based layout
    /// with descriptive sub-labels and color-coded sections:
    ///
    ///   Title: "Ironclad Encounter Stats"
    ///   [sub-label]         vs Turret Operator
    ///   [header]            N   Dmg   Mid 50% Turns  Pots  Deaths
    ///   [data — colored]    8    23     12-31   4.2   0.4     1/8
    ///   [sub-label warm]    vs All Hive Elite encounters (baseline)
    ///   [data — warm]      42    18      9-25   3.8   0.3    4/42
    ///   [sub-label cool]    vs All act 2 Elite encounters
    ///   [data — cool]      97    15      7-22   3.5   0.2   8/97
    ///   [separator]
    ///   [sub-label grey]    All characters vs Turret Operator
    ///   [data — grey]      24    19     10-28   3.9   0.3    3/24
    ///
    /// Biome is always the encounter's actual biome from metadata — not the selected tab.
    /// Row 3 (act pool) is skipped when it would be identical to row 2 (single-biome acts).
    /// </summary>
    internal static FocusedTableParts BuildEncounterStatsTextFocused(
        EncounterEvent encounterStat,
        PoolMetrics? poolAct,
        PoolMetrics poolAll,
        PoolMetrics allCharsMetrics,
        string encounterName,
        string characterId,
        string? actLabel,
        string categoryLabel,
        AggregationFilter filter,
        string? category = null,
        IReadOnlyList<int>? poolActDmgPct = null,
        IReadOnlyList<int>? poolAllDmgPct = null,
        IReadOnlyList<int>? allCharsDmgPct = null)
    {
        // Two-pass sparkline build: collect Dmg% values per row, then
        // materialize textures with a shared x-domain (see
        // BuildSparklinesFromValues).
        var focusedSparklineValues = new List<double[]?>();
        var sb = new StringBuilder();
        var charIcon = CharacterIcon(characterId, 30);
        // Canonical English id for category.*.plural key lookup — categoryLabel
        // is the localized display name (e.g. "Обычный") and lowercasing it
        // produces a Russian/CJK key that won't resolve.
        var catLower = (category ?? categoryLabel).ToLowerInvariant();
        var encColor = category != null ? EncounterIcons.CategoryColorHex(category) : null;
        var encNameRow1 = encColor != null
            ? $"{KreonBoldFontTag}[color={encColor}]{encounterName}[/color][/b]"
            : $"{KreonBoldFontTag}{encounterName}[/b]";
        var encNameRow4 = $"{KreonBoldFontTag}{encounterName}[/b]";

        var baselineSource = poolAct is { Fought: > 0 } ? poolAct.Value : poolAll;
        var (medianBase, iqrcBase, deathRateBase, turnsBase, potsBase) = DeriveBaselines(baselineSource);

        sb.Append("[table=9]");
        AppendSizingRow(sb);

        // Header row — empty descriptor cell + 7 column headers. Use FocusedColumns
        // paddings (which carry expand=N) to match the sizing row + data rows; passing
        // bare cell-padding constants would omit expand and could reset the column's
        // expand to Godot's default of 1, destabilizing column widths.
        var FP = FocusedColumns;
        AppendDescriptorCell(sb, " ", isHeader: true);
        AppendHeaderCell(sb, L.T("tooltip.col.fights"),    FP.Fought);
        AppendHeaderCell(sb, L.T("tooltip.col.dist"),    FP.Spark);
        AppendHeaderCell(sb, L.T("tooltip.col.dmg"),     FP.Dmg);
        AppendHeaderCell(sb, L.T("tooltip.col.mid50"),   FP.Mid50);
        AppendHeaderCell(sb, L.T("tooltip.col.spread"),  FP.Spread);
        AppendHeaderCell(sb, L.T("tooltip.col.turns"),   FP.Turns);
        AppendHeaderCell(sb, L.T("tooltip.col.potions"), FP.Pots);
        AppendHeaderCell(sb, L.T("tooltip.col.deaths"),  FP.Deaths);

        // Row 1: this encounter, this character — data cells colored against act pool baseline
        AppendDescriptorCell(sb, $"{charIcon}{L.T("descriptor.vs_encounter", ("enc", encNameRow1))}");
        AppendRow1DataCells(sb, encounterStat, medianBase, iqrcBase, deathRateBase, turnsBase, potsBase);
        // AppendRow1DataCells always emits a Spark marker now, so the
        // sparkline list needs a matching entry — texture if the row
        // has data, null otherwise.
        {
            int startingHp = MainFile.Db.CharacterStartingHp.GetValueOrDefault(characterId, 0);
            focusedSparklineValues.Add(
                encounterStat.Fought > 0
                    ? GetDmgPctValuesFromRawValues(encounterStat.DamageValues, startingHp)
                    : null);
        }

        // Row 2: act + category pool baseline — plain text (no coloring on context rows)
        if (poolAct.HasValue && actLabel != null)
        {
            AppendDescriptorCell(sb, $"{charIcon}{L.T("descriptor.vs_act_cat_baseline", ("act", actLabel), ("cat_plural", PluralizeCategory(catLower)))}");
            AppendPoolDataCells(sb, poolAct.Value);
            focusedSparklineValues.Add(GetDmgPctValuesFromPctList(poolActDmgPct));
        }

        // Row 3: all-acts category pool
        AppendDescriptorCell(sb, $"{charIcon}{L.T("descriptor.vs_all_cat", ("cat_plural", PluralizeCategory(catLower)))}");
        AppendPoolDataCells(sb, poolAll);
        focusedSparklineValues.Add(GetDmgPctValuesFromPctList(poolAllDmgPct));

        // Row 4: all chars vs this encounter — character-weighted aggregation
        var allCharsIcon = AllCharsIcon(22);
        AppendDescriptorCell(sb, $"{allCharsIcon}{L.T("descriptor.all_vs_encounter", ("enc", encNameRow4))}");
        AppendPoolDataCells(sb, allCharsMetrics);
        focusedSparklineValues.Add(GetDmgPctValuesFromPctList(allCharsDmgPct));

        sb.Append("[/table]");

        // Footer — character omitted (each row shows its character context via icon).
        var filterCtx = CardHoverShowPatch.BuildFilterContext(L.T("filter.all_characters"), filter, includeCharacter: false);
        sb.Append(TooltipHelper.FormatFooter(filterCtx));

        return new FocusedTableParts { Text = sb.ToString(), Sparklines = BuildSparklinesFromValues(focusedSparklineValues) };
    }

    /// <summary>
    /// Builds category-level stats for a focused single-character view. Same section
    /// layout as encounter view but without the encounter row; bottom row is all-chars
    /// for the category pool instead of a specific encounter.
    /// </summary>
    internal static FocusedTableParts BuildCategoryStatsTextFocused(
        PoolMetrics poolBiome,
        PoolMetrics poolAll,
        PoolMetrics allCharsPool,
        string characterId,
        string? biomeLabel,
        string categoryLabel,
        AggregationFilter filter,
        string? category = null,
        IReadOnlyList<int>? poolBiomeDmgPct = null,
        IReadOnlyList<int>? poolAllDmgPct = null,
        IReadOnlyList<int>? allCharsDmgPct = null)
    {
        // Two-pass sparkline build (see BuildSparklinesFromValues).
        var sparklineValues = new List<double[]?>();
        var sb = new StringBuilder();
        var charIcon = CharacterIcon(characterId, 30);
        // Canonical English id for category.*.plural key lookup — see note in
        // BuildEncounterStatsTextFocused.
        var catLower = (category ?? categoryLabel).ToLowerInvariant();
        var catColor = category != null ? EncounterIcons.CategoryColorHex(category) : null;

        sb.Append("[table=9]");
        AppendSizingRow(sb);

        // Header row (see comment in BuildEncounterStatsTextFocused — use FocusedColumns
        // paddings so expand attributes match the sizing + data rows).
        var FP = FocusedColumns;
        AppendDescriptorCell(sb, " ", isHeader: true);
        AppendHeaderCell(sb, L.T("tooltip.col.fights"),    FP.Fought);
        AppendHeaderCell(sb, L.T("tooltip.col.dist"),    FP.Spark);
        AppendHeaderCell(sb, L.T("tooltip.col.dmg"),     FP.Dmg);
        AppendHeaderCell(sb, L.T("tooltip.col.mid50"),   FP.Mid50);
        AppendHeaderCell(sb, L.T("tooltip.col.spread"),  FP.Spread);
        AppendHeaderCell(sb, L.T("tooltip.col.turns"),   FP.Turns);
        AppendHeaderCell(sb, L.T("tooltip.col.potions"), FP.Pots);
        AppendHeaderCell(sb, L.T("tooltip.col.deaths"),  FP.Deaths);

        var allCharsIcon = AllCharsIcon(22);
        var catPlural = PluralizeCategory(catLower);

        if (biomeLabel != null)
        {
            var biomeColored = catColor != null
                ? $"{KreonBoldFontTag}[color={catColor}]{biomeLabel}[/color][/b]"
                : $"{KreonBoldFontTag}{biomeLabel}[/b]";
            var catPluralColored = catColor != null
                ? $"{KreonBoldFontTag}[color={catColor}]{catPlural}[/color][/b]"
                : catPlural;
            AppendDescriptorCell(sb, $"{charIcon}{L.T("descriptor.vs_biome_cat", ("biome", biomeColored), ("cat_plural", catPluralColored))}");
            AppendRow1PoolDataCells(sb, poolBiome, poolAll);
            sparklineValues.Add(GetDmgPctValuesFromPctList(poolBiomeDmgPct));

            // Row 2 (neutral baseline): this char, all acts
            AppendDescriptorCell(sb, $"{charIcon}{L.T("descriptor.vs_all_cat_baseline", ("cat_plural", catPlural))}");
            AppendPoolDataCells(sb, poolAll);
            sparklineValues.Add(GetDmgPctValuesFromPctList(poolAllDmgPct));

            // Row 3 (neutral): all characters, scoped to same biome/act
            AppendDescriptorCell(sb, $"{allCharsIcon}{L.T("descriptor.all_vs_biome_cat", ("biome", biomeLabel), ("cat_plural", catPlural))}");
            AppendPoolDataCells(sb, allCharsPool);
            sparklineValues.Add(GetDmgPctValuesFromPctList(allCharsDmgPct));
        }
        else
        {
            // All acts: 2 rows.
            // Row 1 (colored): this char, all acts — colored vs all-chars pool
            AppendDescriptorCell(sb, $"{charIcon}{L.T("descriptor.vs_all_cat", ("cat_plural", catPlural))}");
            AppendRow1PoolDataCells(sb, poolBiome, allCharsPool);
            sparklineValues.Add(GetDmgPctValuesFromPctList(poolBiomeDmgPct));

            // Row 2 (neutral baseline): all characters, all acts
            AppendDescriptorCell(sb, $"{allCharsIcon}{L.T("descriptor.all_vs_all_cat_baseline", ("cat_plural", catPlural))}");
            AppendPoolDataCells(sb, allCharsPool);
            sparklineValues.Add(GetDmgPctValuesFromPctList(allCharsDmgPct));
        }

        sb.Append("[/table]");

        // Footer — character omitted (each row shows its character context inline).
        var filterCtx = CardHoverShowPatch.BuildFilterContext(L.T("filter.all_characters"), filter, includeCharacter: false);
        sb.Append(TooltipHelper.FormatFooter(filterCtx));

        return new FocusedTableParts { Text = sb.ToString(), Sparklines = BuildSparklinesFromValues(sparklineValues) };
    }

    /// <summary>
    /// Derives coloration baselines from an encounter-weighted PoolMetrics. Used as
    /// the reference for coloring the focused encounter row. The median baseline is
    /// the median of per-encounter medians; the IQRC baseline is the median of
    /// per-encounter IQRCs (see <c>GetEncounterIqrcBaseline</c>, which
    /// <see cref="StatsAggregator.AggregateMetricsFromEvents"/> pre-computes into
    /// <c>m.Iqrc</c>). Using <c>m.Iqrc</c> here keeps the focused-view Spread cell
    /// aligned with the encounter-list per-row Spread coloring (both ratio the
    /// encounter's own IQRC against the same pool baseline) and stays robust to
    /// outlier encounters where the per-encounter median is 0/floored. The other
    /// baselines stay mean-aggregated. See slay-the-stats-discuss.2026-04-25.txt
    /// for the rationale on switching from mean to median.
    /// </summary>
    private static (double medianBase, double iqrcBase, double deathRateBase, double turnsBase, double potsBase) DeriveBaselines(PoolMetrics m)
    {
        if (m.Fought == 0) return (0, 1.0, 10.0, 0, 0);
        return (m.Median, m.Iqrc, m.DeathRate, m.AvgTurns, m.AvgPots);
    }

    /// <summary>
    /// ColBad variant for metrics whose absolute scale is much smaller than 0–100
    /// (turns, potions, raw median damage). Expresses the value as a percentage of the
    /// baseline (100 = matches baseline, &gt;100 = above) so the existing significance
    /// thresholds in <see cref="TooltipHelper.ColWR"/> trigger meaningfully on small
    /// absolute differences. Falls back to neutral grey when the baseline is zero.
    /// </summary>
    /// <param name="baselineFloor">Minimum baseline value. When the actual baseline
    /// is below this, the floor is used instead — preventing extreme color ratios
    /// from tiny absolute differences (e.g. 0.2 vs 0.5 potions).</param>
    /// <param name="kScale">Multiplier applied to the deviation from 100% before
    /// handing to ColBad. Since sig = tanh(k × n × |pct − baseline|), scaling the
    /// deviation is equivalent to scaling the effective k. Values &lt; 1 dampen the
    /// color (higher deviations needed for the same shade); values &gt; 1 amplify.
    /// Default 1.0 (no change). Used for metrics like potions where normal k is
    /// too sensitive on the 0–200% scale that relative metrics produce.</param>
    private static string ColBadRelative(string text, double value, double baseline, int n,
        double baselineFloor = 0, double kScale = 1.0)
    {
        // Exact match → no deviation to express. Guards against the baselineFloor
        // synthesizing a ratio (e.g. value=0/baseline=0 with floor=0.1 computes
        // pctOfBaseline=0 which reads as "30% below baseline" after damping).
        if (value == baseline)
            return $"[color={TooltipHelper.NeutralShade}]{text}[/color]";
        if (baseline <= 0 && baselineFloor <= 0)
            return $"[color={TooltipHelper.NeutralShade}]{text}[/color]";
        // Direction comes from the actual baseline; magnitude uses the (possibly
        // floored) denominator. Otherwise a sub-floor baseline flips direction —
        // e.g. baseline=0.02, floor=0.1, value=0.05 computes value/floor = 50%
        // ("below baseline"), but value > baseline semantically means "above".
        double effectiveBaseline = Math.Max(baseline, baselineFloor);
        double sign = value > baseline ? 1.0 : -1.0;
        double magnitude = Math.Abs(value - baseline) / effectiveBaseline * 100.0;
        double pctOfBaseline = 100.0 + sign * magnitude;
        double dampenedPct = 100.0 + (pctOfBaseline - 100.0) * kScale;
        return ColBad(text, dampenedPct, n, 100.0);
    }

    /// <summary>
    /// Log-space variant of ColBad for ratio metrics where "4× baseline" and
    /// "baseline / 4" should color with equal magnitude in opposite directions.
    /// Converts the multiplicative ratio to an additive log deviation, then feeds
    /// the existing ColBad tanh pipeline. log(value/baseline) is symmetric around
    /// zero: log(4) ≈ +1.39, log(0.25) ≈ −1.39.
    /// </summary>
    /// <param name="scale">Multiplier applied to the log ratio before significance.
    /// Log-space deviations are much smaller in magnitude than percentage deviations
    /// (log(2) ≈ 0.69, log(4) ≈ 1.39), so scale compensates to land in the ColBad
    /// tanh sweet spot. Calibrated for per-character Fought counts (typically n≈10–50):
    /// with scale=20, ratio 2× at n=20 ≈ level 1, ratio 4× at n=20 ≈ level 2,
    /// ratio 1.2× at n=20 stays faint. Small-n cases need higher scale to overcome
    /// the k × n product being tiny; pool-aggregated contexts with n=100+ will
    /// saturate quickly on any notable ratio — acceptable since those are high-
    /// confidence signals worth emphasizing.</param>
    private static string ColBadLog(string text, double value, double baseline, int n, double scale = 20.0)
    {
        if (baseline <= 0)
            return $"[color={TooltipHelper.NeutralShade}]{text}[/color]";
        // Clamp to a tiny minimum so log stays finite when value is 0.
        double ratio = Math.Max(value, 1e-6) / baseline;
        double logDev = Math.Log(ratio);
        return ColBad(text, 100.0 + logDev * scale, n, 100.0);
    }

    /// <summary>
    /// Builds encounter stats text with a single row (no character breakdown).
    /// Used by the in-combat tooltip where the current character is already known.
    /// Trimmed to 4 columns (drops Pots vs the bestiary table) and drops the per-row
    /// character label since the in-combat tooltip is always scoped to the current
    /// run's character. The character flows into the footer instead, rendered as the
    /// top-panel head sprite + name (matching the compendium tooltip footers).
    /// </summary>
    /// <param name="dmgBaseline">Absolute average damage taken across this category
    /// (not %-of-max-HP) — used in the footer because the in-combat tooltip is scoped
    /// to a single character with constant max HP, so absolute damage is more
    /// meaningful than the % variant.</param>
    /// <param name="character">Run character ID (e.g. "CHARACTER.DEFECT") used to look
    /// up the head sprite for the footer. Null falls back to a plain "All" label.</param>
    /// <param name="filter">Filter that produced these aggregates — required so the
    /// footer can render the full <see cref="CardHoverShowPatch.BuildFilterContext"/>
    /// line, matching the card / relic tooltip footer format. Every active filter
    /// (asc range, character, version range, profile) is visible even at default
    /// values like A0-20.</param>
    // Column padding for the in-combat encounter [table=5]. expand=1 distributes
    // leftover width so columns span the full tooltip panel.
    private const string CombatCellPadding = "expand=1 padding=4,0,4,0";

    internal static string BuildEncounterStatsTextSingleRow(
        EncounterEvent stat,
        PoolMetrics poolBaseline,
        double dmgBaseline,
        string? character,
        string categoryLabel,
        AggregationFilter filter)
    {
        var sb = new StringBuilder();

        sb.Append("[table=5]");
        // Header row
        sb.Append($"[cell {CombatCellPadding}][right][color={HeaderColor}]{L.T("tooltip.col.fights")}[/color][/right][/cell]");
        sb.Append($"[cell {CombatCellPadding}][right][color={HeaderColor}]{L.T("tooltip.col.dmg")}[/color][/right][/cell]");
        sb.Append($"[cell {CombatCellPadding}][right][color={HeaderColor}]{L.T("tooltip.col.mid50")}[/color][/right][/cell]");
        sb.Append($"[cell {CombatCellPadding}][right][color={HeaderColor}]{L.T("tooltip.col.spread")}[/color][/right][/cell]");
        sb.Append($"[cell {CombatCellPadding}][right][color={HeaderColor}]{L.T("tooltip.col.turns")}[/color][/right][/cell]");

        double spreadBase = 0;
        if (stat.Fought > 0)
        {
            // Pool baseline provides spread + turns; Dmg uses the explicit
            // `dmgBaseline` (fight-weighted mean) so the coloration matches what
            // the footer line displays. Mixing poolBaseline.Median for coloration
            // and dmgBaseline for display caused a "4 vs 4.8 but colored red" bug
            // when the two numbers disagreed (encounter-weighted median of medians
            // is not the same as fight-weighted mean damage).
            var (_, iqrcBase, _, turnsBase, _) = DeriveBaselines(poolBaseline);
            AppendCombatDataCells(sb, stat, dmgBaseline, iqrcBase, turnsBase);
            spreadBase = iqrcBase;
        }
        else
        {
            for (int i = 0; i < 5; i++)
                sb.Append($"[cell {CombatCellPadding}][right][color={TooltipHelper.NeutralShade}]-[/color][/right][/cell]");
        }
        sb.Append("[/table]");

        // Footer — baseline + filter context. Spread baseline is IQRC (ratio);
        // render as a percentage to match the Spread column.
        var baselineDmgAbs = $"{dmgBaseline:F1}";
        var charLabel = character != null
            ? CardHoverShowPatch.GetCharacterDisplay(character)
            : L.T("filter.all_characters");
        var filterCtx = CardHoverShowPatch.BuildFilterContext(charLabel, filter);
        var categoryLower = categoryLabel.ToLowerInvariant();
        var baselineLine = stat.Fought > 0
            ? L.T("tooltip.baseline.encounter",        ("category", categoryLower), ("dmg", baselineDmgAbs), ("spread", $"{spreadBase * 100:F0}"))
            : L.T("tooltip.baseline.encounter_nodata", ("category", categoryLower), ("dmg", baselineDmgAbs));
        sb.Append(TooltipHelper.FormatBaselineLine(baselineLine));
        sb.Append(TooltipHelper.FormatFooter(filterCtx));

        return sb.ToString();
    }

    /// <summary>Appends 5 data cells for the in-combat encounter table: Runs | Dmg | Mid 50% | Spread | Turns.
    /// Coloration: Dmg via ColBadRelative against <paramref name="dmgBaseline"/> (the
    /// same value shown in the footer, so color and display agree); Mid 50% neutral;
    /// Spread via ColBadLog against <paramref name="iqrcBaseline"/>; Turns via
    /// ColBadRelative against <paramref name="turnsBaseline"/>.</summary>
    private static void AppendCombatDataCells(StringBuilder sb, EncounterEvent stat,
        double dmgBaseline, double iqrcBaseline, double turnsBaseline)
    {
        int n = stat.Fought;
        double avgTurns  = (double)stat.TurnsTakenSum / n;
        double dmgMedian = stat.DamageMedian() ?? (double)stat.DamageTakenSum / n;
        var iqr = stat.DamageIQR();
        string fIqr = iqr.HasValue ? $"{iqr.Value.p25:F0}-{iqr.Value.p75:F0}" : "-";

        var cN      = TooltipHelper.ColN($"{n}", n);
        var cDmg    = ColBadRelative($"{dmgMedian:F0}", dmgMedian, dmgBaseline, n);
        var cMid50  = $"[color={TooltipHelper.NeutralShade}]{fIqr}[/color]";
        var cSpread = FormatSpreadCell(StatsAggregator.Iqrc(iqr, dmgMedian), n, iqrcBaseline);
        var cTurns  = ColBadRelative($"{avgTurns:F1}", avgTurns, turnsBaseline, n);

        sb.Append($"[cell {CombatCellPadding}][right]{cN}[/right][/cell]");
        sb.Append($"[cell {CombatCellPadding}][right]{cDmg}[/right][/cell]");
        sb.Append($"[cell {CombatCellPadding}][right]{cMid50}[/right][/cell]");
        sb.Append($"[cell {CombatCellPadding}][right]{cSpread}[/right][/cell]");
        sb.Append($"[cell {CombatCellPadding}][right]{cTurns}[/right][/cell]");
    }



    internal static string NoDataText(string? characterLabel, int? ascensionMin, int? ascensionMax)
    {
        var ascPrefix = FormatAscensionPrefix(ascensionMin, ascensionMax);
        var label = characterLabel ?? L.T("filter.all_characters");
        return $"[color={TooltipHelper.NeutralShade}]{L.T("tooltip.no_encounter_data")}\n[font_size=16]{ascPrefix}{label}[/font_size][/color]";
    }

    /// <summary>
    /// Formats a character ID we don't have a hardcoded label for.
    /// "CHARACTER.MOD_ROGUE" -> "Mod Rogue", truncated to fit the column width.
    /// </summary>
    private static string FormatUnknownCharLabel(string characterId)
    {
        var dotIdx = characterId.IndexOf('.');
        var name = dotIdx >= 0 ? characterId[(dotIdx + 1)..] : characterId;
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpper(words[i][0]) + words[i][1..].ToLower();
        }
        var label = string.Join(' ', words);
        return label.Length > 11 ? label[..11] : label;
    }

    private static string FormatEmptyRow(string label)
    {
        var dash = $"[color={TooltipHelper.NeutralShade}]";
        // Match the column widths used in FormatRow:
        //   N(4) Dmg(5) Mid50%(9) Turns(5) Pots(5) Deaths(7)
        return $"{label} {dash}{"-",4}[/color] {dash}{"-",5}[/color] {dash}{"-",9}[/color] {dash}{"-",5}[/color] {dash}{"-",5}[/color] {dash}{"-",7}[/color]";
    }

    private static string ColBad(string text, double pct, int n, double baseline)
    {
        // Encounter surfaces skip the ≤3-runs filter so low-sample rows still
        // color (needed for monotonic coloring in the bestiary's sorted list;
        // see TooltipHelper.ColWR).
        return TooltipHelper.ColWR(text, baseline + (baseline - pct), n, baseline,
            skipSmallSampleFilter: true);
    }

    internal static void Accumulate(EncounterEvent total, EncounterEvent stat)
    {
        total.Fought           += stat.Fought;
        total.Died             += stat.Died;
        total.WonRun           += stat.WonRun;
        total.TurnsTakenSum    += stat.TurnsTakenSum;
        total.DamageTakenSum   += stat.DamageTakenSum;
        total.DamageTakenSqSum += stat.DamageTakenSqSum;
        total.HpEnteringSum    += stat.HpEnteringSum;
        total.MaxHpSum         += stat.MaxHpSum;
        total.PotionsUsedSum   += stat.PotionsUsedSum;
        total.DmgPctSum        += stat.DmgPctSum;
        total.DmgPctSqSum      += stat.DmgPctSqSum;
        if (stat.DamageValues is { Count: > 0 })
            (total.DamageValues ??= new List<int>()).AddRange(stat.DamageValues);
        if (stat.TurnsValues is { Count: > 0 })
            (total.TurnsValues ??= new List<int>()).AddRange(stat.TurnsValues);
        if (stat.PotionsValues is { Count: > 0 })
            (total.PotionsValues ??= new List<int>()).AddRange(stat.PotionsValues);
    }

    private static string FormatAscensionPrefix(int? ascMin, int? ascMax)
    {
        if (ascMin == null && ascMax == null) return "";
        int min = ascMin ?? 0;
        int max = ascMax ?? 20;
        if (min == max) return $"A{min} ";
        if (min == 0 && max == 20) return "";
        return $"A{min}-{max} ";
    }
}
