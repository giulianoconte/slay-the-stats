using System.Text;

namespace SlayTheStats;

/// <summary>
/// Generates encounter stats text with rows per character.
/// Columns (bestiary multi-row): N | Dmg | Mid 50% | Turns | Pots | Deaths
/// Columns (in-combat single-row): N | Dmg | Mid 50% | Turns | Deaths   ← drops Pots so the
/// table fits within the normal hover-tip width (the in-combat tooltip uses
/// TooltipHelper.TooltipWidth, not EncounterTooltipWidth, after this trim).
///
/// Coloration:
///   Dmg      — ColBad(avgDmgPct, dmgPctBaseline, n=Fought) so high-damage encounters
///              tint orange/red and low-damage ones tint teal/green.
///   Mid 50%  — ColBad(iqrc, iqrcBaseline, n=Fought) so swingy encounters (high IQRC)
///              tint orange/red and consistent ones tint teal/green. IQRC = IQR / median.
///   Deaths   — Numerator (died) is colored by ColBad(deathRate, deathRateBaseline, n)
///              so the cell visualises both the rate AND the confidence (denominator).
///   N, Turns, Pots — neutral (no good/bad direction; just informational).
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
    internal static string BuildEncounterStatsText(
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
        var sb = new StringBuilder();

        // Header — column widths align with FormatRow.
        //   Label(11)  N(4)  Dmg(5)  Mid50%(9)  Turns(5)  Pots(5)  Deaths(7)
        // "Dmg" = median damage. "Mid 50%" = IQR (p25-p75).
        sb.Append("                 N   Dmg   Mid 50% Turns  Pots  Deaths\n");

        var total = new EncounterEvent();

        // Render known characters in canonical order
        var rendered = new HashSet<string>();
        foreach (var (charId, label) in CharacterOrder)
        {
            rendered.Add(charId);
            if (charStats.TryGetValue(charId, out var stat) && stat.Fought > 0)
            {
                Accumulate(total, stat);
                sb.Append(FormatRow($"{label,-11}", stat, deathRateBaseline, dmgPctBaseline, iqrcBaseline));
            }
            else
            {
                sb.Append(FormatEmptyRow($"{label,-11}"));
            }
            sb.Append('\n');
        }

        // Append any unknown characters with stats (modded or new in-game characters)
        // sorted alphabetically by ID for stable ordering
        foreach (var (charId, stat) in charStats.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (rendered.Contains(charId)) continue;
            if (stat.Fought == 0) continue;
            Accumulate(total, stat);
            var label = FormatUnknownCharLabel(charId);
            sb.Append(FormatRow($"{label,-11}", stat, deathRateBaseline, dmgPctBaseline, iqrcBaseline));
            sb.Append('\n');
        }

        // All row — always shown.
        if (total.Fought > 0)
            sb.Append(FormatRow($"{"All",-11}", total, deathRateBaseline, dmgPctBaseline, iqrcBaseline));
        else
            sb.Append(FormatEmptyRow($"{"All",-11}"));

        // Footer — line 1 is the category dmg% baseline; line 2 is the
        // filter context line (asc · char · version · profile) produced by
        // the shared CardHoverShowPatch.BuildFilterContext. The filter is
        // always provided by bestiary callers, so the context line always
        // renders; per-surface rules in BuildFilterContext collapse or
        // omit segments as appropriate.
        var baselineDmg = $"{Math.Round(dmgPctBaseline):F0}%";
        var filterCtx = filter != null
            ? CardHoverShowPatch.BuildFilterContext(characterLabel ?? "All chars", filter)
            : "";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]Baseline {categoryLabel.ToLowerInvariant()} pool dmg%: {baselineDmg}");
        if (filterCtx.Length > 0)
            sb.Append($"\n{filterCtx}");
        sb.Append("[/color][/font_size][/font]");

        return sb.ToString();
    }

    // Section colors for focused-character view. Row descriptors use a dull grey
    // that contrasts less with the background so the eye travels to the data
    // cells first. Context-row data cells (rows 2, 3, 4) use an off-white beige
    // that's bright enough to scan cleanly but stays distinct from row 1's
    // significance-colored white numbers (which get the default label color).
    private const string BaselineSectionColor = "#6a6a6a";  // dull grey — biome pool descriptor
    private const string PoolSectionColor     = "#6a6a6a";  // dull grey — all-acts category descriptor
    private const string AllCharsSectionColor = "#6a6a6a";  // dull grey — cross-character descriptor
    // Dull neutral grey for the data cells in rows 2, 3, 4. Intentionally
    // muted so row 1's emphasized white / colored numbers stay the visual
    // focus of the table.
    private const string ContextRowDataColor  = "#808080";

    // BBCode constants used across the focused-view table cells.
    // Descriptor font is slightly bigger than the data cells so it reads as the
    // label for the row; data stays at the base mono font size (18 from the
    // RichTextLabel theme override).
    private const string KreonRegFontTag  = "[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=18]";
    private const string KreonRegClose    = "[/font_size][/font]";
    private const string KreonBoldFontTag = "[font=res://themes/kreon_bold_glyph_space_one.tres]";
    private const string HeaderColor      = "#8e8676";  // dim warm grey for column headers
    // Per-cell padding controls inter-column gaps. Godot [cell padding] format
    // is `left,top,right,bottom`. Adjacent cells' right + left pad sum to the
    // visual gap. Three tiers of tightness:
    //   • Tight: columns within a logical triple (Dmg/Mid50/Spread and
    //     Turns/Potions/Deaths) sit close together as a group.
    //   • Normal: gap between Fought↔Dmg and Spread↔Turns — separates groups.
    //   • Descriptor: gap between the descriptor column and Fought.
    private const string TightCellPadding      = "padding=3,0,3,0";   // within a triple
    private const string NormalCellPadding      = "padding=8,0,8,0";   // between groups
    private const string DescriptorCellPadding  = "padding=0,0,8,0";   // descriptor → Fought

    /// <summary>Per-column padding assignments so each cell-emitter picks the
    /// right tier without hard-coding padding strings inline.</summary>
    private struct ColumnPaddings
    {
        public readonly string Fought = NormalCellPadding;
        public readonly string Dmg    = TightCellPadding;
        public readonly string Mid50  = TightCellPadding;
        public readonly string Spread = NormalCellPadding;
        public readonly string Turns  = TightCellPadding;
        public readonly string Pots   = TightCellPadding;
        public readonly string Deaths = NormalCellPadding;
        public ColumnPaddings() {}
    }

    private static string EmptyDataCell(string padding, string? color = null)
    {
        var c = color ?? TooltipHelper.NeutralShade;
        return $"[cell {padding}][right][color={c}]-[/color][/right][/cell]";
    }

    /// <summary>Descriptor cell in the focused-view [table=8] — always the first
    /// cell of each row. `expand=3` weights the descriptor column so it soaks up
    /// leftover horizontal space, wrapping long sub-labels across multiple lines
    /// instead of forcing the data columns to shrink. Kreon proportional font.</summary>
    private static void AppendDescriptorCell(StringBuilder sb, string text, bool isHeader = false)
    {
        var color = isHeader ? HeaderColor : BaselineSectionColor;
        sb.Append($"[cell expand=3 {DescriptorCellPadding}]{KreonRegFontTag}[color={color}]{text}[/color]{KreonRegClose}[/cell]");
    }

    /// <summary>Column header cell. Kreon, muted, right-aligned within the cell
    /// so the header sits over right-aligned numbers in the same column.</summary>
    private static void AppendHeaderCell(StringBuilder sb, string name, string padding = NormalCellPadding)
    {
        sb.Append($"[cell {padding}][right]{KreonRegFontTag}[color={HeaderColor}]{name}[/color]{KreonRegClose}[/right][/cell]");
    }

    /// <summary>Row 1 (subject encounter) data cells. Colored against the
    /// encounter-weighted act-pool baseline using the existing ColBad/
    /// ColBadRelative helpers so the familiar significance gradient still
    /// applies: high=bad (orange/red), low=good (teal/green).</summary>
    private static void AppendRow1DataCells(StringBuilder sb, EncounterEvent stat,
        double medianBase, double iqrcBase, double deathRateBase, double turnsBase, double potsBase)
    {
        int n = stat.Fought;
        var P = new ColumnPaddings();
        if (n == 0)
        {
            sb.Append(EmptyDataCell(P.Fought));
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
        var cSpread  = FormatSpreadCell(iqr, dmgMedian, n, iqrcBase);
        var cTurns   = ColBadRelative($"{avgTurns:F1}", avgTurns, turnsBase, n);
        var cPots    = ColBadRelative($"{avgPots:F1}", avgPots, potsBase, n);
        var cDeaths  = FormatDeathsCellInner(stat.Died, n, deathRate, deathRateBase);

        sb.Append($"[cell {P.Fought}][right]{cN}[/right][/cell]");
       
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
        var P = new ColumnPaddings();
        if (n == 0)
        {
            sb.Append(EmptyDataCell(P.Fought));
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
        var cSpread  = FormatSpreadCell(subject.IQR, subject.Median, n, iqrcBase);
        var cTurns   = ColBadRelative($"{subject.AvgTurns:F1}", subject.AvgTurns, turnsBase, n);
        var cPots    = ColBadRelative($"{subject.AvgPots:F1}", subject.AvgPots, potsBase, n);
        var cDeaths  = FormatDeathsCellInner(subject.Died, n, subject.DeathRate, deathRateBase);

        sb.Append($"[cell {P.Fought}][right]{cN}[/right][/cell]");
       
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
        var P = new ColumnPaddings();
        if (n == 0)
        {
            sb.Append(EmptyDataCell(P.Fought, color));
            sb.Append(EmptyDataCell(P.Dmg, color));
            sb.Append(EmptyDataCell(P.Mid50, color));  sb.Append(EmptyDataCell(P.Spread, color));
           
            sb.Append(EmptyDataCell(P.Turns, color));  sb.Append(EmptyDataCell(P.Pots, color));
            sb.Append(EmptyDataCell(P.Deaths, color));
            return;
        }

        string fIqr    = m.IQR.HasValue ? $"{m.IQR.Value.p25:F0}-{m.IQR.Value.p75:F0}" : "-";
        string fSpread = FormatSpreadValue(m.IQR, m.Median);
        var fN         = $"{n}";
        var fDmg       = $"{m.Median:F0}";
        var fTurns     = $"{m.AvgTurns:F1}";
        var fPots      = $"{m.AvgPots:F1}";
        var fDeaths    = $"{m.Died}/{n}";

        sb.Append($"[cell {P.Fought}][right][color={color}]{fN}[/color][/right][/cell]");
       
        sb.Append($"[cell {P.Dmg}][right][color={color}]{fDmg}[/color][/right][/cell]");
        sb.Append($"[cell {P.Mid50}][right][color={color}]{fIqr}[/color][/right][/cell]");
        sb.Append($"[cell {P.Spread}][right][color={color}]{fSpread}[/color][/right][/cell]");
       
        sb.Append($"[cell {P.Turns}][right][color={color}]{fTurns}[/color][/right][/cell]");
        sb.Append($"[cell {P.Pots}][right][color={color}]{fPots}[/color][/right][/cell]");
        sb.Append($"[cell {P.Deaths}][right][color={color}]{fDeaths}[/color][/right][/cell]");
    }

    /// <summary>Formats the IQRC as a percentage string ("82%"). Returns "-" when
    /// there's insufficient data for an IQR or the median is zero.</summary>
    private static string FormatSpreadValue((double p25, double p75)? iqr, double median)
    {
        if (!iqr.HasValue || median <= 0) return "-";
        double iqrc = (iqr.Value.p75 - iqr.Value.p25) / median;
        return $"{iqrc * 100:F0}%";
    }

    /// <summary>Formats the Spread cell for colored row-1 use. IQRC displayed as
    /// percent ("82%"), colored by ColBad against the IQRC baseline (high = swingy
    /// = bad → orange, low = consistent = good → teal). Returns neutral when
    /// there's no data.</summary>
    private static string FormatSpreadCell((double p25, double p75)? iqr, double? median, int n, double iqrcBaseline)
    {
        if (!iqr.HasValue || median == null || median.Value <= 0)
            return $"[color={TooltipHelper.NeutralShade}]-[/color]";

        double iqrc = (iqr.Value.p75 - iqr.Value.p25) / median.Value;
        string text = $"{iqrc * 100:F0}%";
        return ColBad(text, iqrc * 100, n, iqrcBaseline * 100);
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
    private static string PluralizeCategory(string categoryLower) => categoryLower switch
    {
        "weak"   => "weaks",
        "normal" => "normals",
        "elite"  => "elites",
        "boss"   => "bosses",
        "event"  => "events",
        _        => categoryLower + "s",
    };
    // Horizontal separator between sections in focused mode. 51 box-drawing chars span the
    // data row content width (with the new wider columns). Dim colour so it doesn't compete.
    private static readonly string SectionSeparator =
        $"  [color=#505050]{new string('─', 51)}[/color]";

    // Two-line column header for focused-character views with descriptive labels.
    //   Line 1:          median   middle 50%
    //   Line 2:  Fought  damage   damage range  turns  potions  deaths
    // Column widths: Fought(6) | damage(6) | range(12) | turns(5) | potions(7) | deaths(7)
    // Two-space separators between columns for breathing room.
    private static string FocusedColumnHeader()
    {
        // Render the two header lines in Kreon (proportional) rather than the
        // table's monospace font. Proportional Kreon words are considerably
        // narrower than the old mono padding, which shrinks the overall header
        // strip — the data columns underneath still align with each other in
        // monospace, and the header words are positioned roughly above their
        // columns via a small amount of manual leading whitespace.
        const string Open  = "[font=res://themes/kreon_regular_glyph_space_one.tres][color=#9a9484]";
        const string Close = "[/color][/font]";
        var sb = new StringBuilder();
        sb.Append($"{Open}                median     middle 50%{Close}\n");
        sb.Append($"{Open}       Fought   damage    damage range    turns   potions   deaths{Close}\n");
        return sb.ToString();
    }

    /// <summary>Returns just the inline character icon BBCode at a given size, without the
    /// trailing name. Returns empty string when the character has no recognised icon.</summary>
    private static string CharacterIcon(string characterId, int sizePx)
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
    private static string AllCharsIcon(int sizePx)
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
    internal static string BuildEncounterStatsTextFocused(
        EncounterEvent encounterStat,
        PoolMetrics? poolAct,
        PoolMetrics poolAll,
        PoolMetrics allCharsMetrics,
        string encounterName,
        string characterId,
        string? actLabel,
        string categoryLabel,
        AggregationFilter filter,
        string? category = null)
    {
        var sb = new StringBuilder();
        var charIcon = CharacterIcon(characterId, 30);
        var catLower = categoryLabel.ToLowerInvariant();
        var encColor = category != null ? EncounterIcons.CategoryColorHex(category) : null;
        var encNameRow1 = encColor != null
            ? $"{KreonBoldFontTag}[color={encColor}]{encounterName}[/color][/font]"
            : $"{KreonBoldFontTag}{encounterName}[/font]";
        var encNameRow4 = $"{KreonBoldFontTag}{encounterName}[/font]";

        var baselineSource = poolAct is { Fought: > 0 } ? poolAct.Value : poolAll;
        var (medianBase, iqrcBase, deathRateBase, turnsBase, potsBase) = DeriveBaselines(baselineSource);

        sb.Append("[table=8]");

        // Header row — empty descriptor cell + 6 column headers
        AppendDescriptorCell(sb, " ", isHeader: true);
        AppendHeaderCell(sb, "Fought", NormalCellPadding);
        AppendHeaderCell(sb, "Dmg",     TightCellPadding);
        AppendHeaderCell(sb, "Mid 50%", TightCellPadding);
        AppendHeaderCell(sb, "Spread",  NormalCellPadding);
        AppendHeaderCell(sb, "Turns",   TightCellPadding);
        AppendHeaderCell(sb, "Potions", TightCellPadding);
        AppendHeaderCell(sb, "Deaths",  TightCellPadding);

        // Row 1: this encounter, this character — data cells colored against act pool baseline
        AppendDescriptorCell(sb, $"{charIcon}vs {encNameRow1}");
        AppendRow1DataCells(sb, encounterStat, medianBase, iqrcBase, deathRateBase, turnsBase, potsBase);

        // Row 2: act + category pool baseline
        if (poolAct.HasValue && actLabel != null)
        {
            AppendDescriptorCell(sb, $"{charIcon}vs {actLabel} {PluralizeCategory(catLower)} (baseline)");
            AppendPoolDataCells(sb, poolAct.Value);
        }

        // Row 3: all-acts category pool
        AppendDescriptorCell(sb, $"{charIcon}vs all {PluralizeCategory(catLower)}");
        AppendPoolDataCells(sb, poolAll);

        // Row 4: all chars vs this encounter — character-weighted aggregation
        var allCharsIcon = AllCharsIcon(22);
        AppendDescriptorCell(sb, $"{allCharsIcon}All vs {encNameRow4}");
        AppendPoolDataCells(sb, allCharsMetrics);

        sb.Append("[/table]");

        // Footer — character omitted (each row shows its character context via icon).
        var filterCtx = CardHoverShowPatch.BuildFilterContext("All chars", filter, includeCharacter: false);
        if (filterCtx.Length > 0)
            sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{filterCtx}[/color][/font_size][/font]");

        return sb.ToString();
    }

    /// <summary>Appends a horizontal section separator on its own line, no extra blank
    /// lines (the line height of the separator itself provides the visual breathing room).</summary>
    private static void AppendSectionSeparator(StringBuilder sb)
    {
        sb.Append(SectionSeparator);
        sb.Append('\n');
    }

    /// <summary>Previously added a blank line above the all-chars separator; row 4 now
    /// sits flush with the rest of the table (no extra vertical gap) per the tighter
    /// spacing pass. Kept as a thin alias for the call sites.</summary>
    private static void AppendSectionSeparatorWithExtraSpace(StringBuilder sb)
        => AppendSectionSeparator(sb);

    /// <summary>
    /// Builds category-level stats for a focused single-character view. Same section
    /// layout as encounter view but without the encounter row; bottom row is all-chars
    /// for the category pool instead of a specific encounter.
    /// </summary>
    internal static string BuildCategoryStatsTextFocused(
        PoolMetrics poolBiome,
        PoolMetrics poolAll,
        PoolMetrics allCharsPool,
        string characterId,
        string? biomeLabel,
        string categoryLabel,
        AggregationFilter filter)
    {
        var sb = new StringBuilder();
        var charIcon = CharacterIcon(characterId, 30);
        var catLower = categoryLabel.ToLowerInvariant();

        sb.Append("[table=8]");

        // Header row
        AppendDescriptorCell(sb, " ", isHeader: true);
        AppendHeaderCell(sb, "Fought", NormalCellPadding);
       
        AppendHeaderCell(sb, "Dmg",     TightCellPadding);
        AppendHeaderCell(sb, "Mid 50%", TightCellPadding);
        AppendHeaderCell(sb, "Spread",  NormalCellPadding);
       
        AppendHeaderCell(sb, "Turns",   TightCellPadding);
        AppendHeaderCell(sb, "Potions", TightCellPadding);
        AppendHeaderCell(sb, "Deaths",  NormalCellPadding);

        var allCharsIcon = AllCharsIcon(22);
        var catPlural = PluralizeCategory(catLower);

        if (biomeLabel != null)
        {
            // Specific biome or act: 3 rows.
            // Row 1 (colored): this char, scoped to biome/act — colored vs char's all-acts pool.
            // The biome/act name is bold + highlighted so the scope stands out in the descriptor.
            var boldBiome = $"{KreonBoldFontTag}[color=#d4c9a8]{biomeLabel}[/color][/font]";
            AppendDescriptorCell(sb, $"{charIcon}vs {boldBiome} {catPlural}");
            AppendRow1PoolDataCells(sb, poolBiome, poolAll);

            // Row 2 (neutral baseline): this char, all acts
            AppendDescriptorCell(sb, $"{charIcon}vs all {catPlural} (baseline)");
            AppendPoolDataCells(sb, poolAll);

            // Row 3 (neutral): all characters, scoped to same biome/act
            AppendDescriptorCell(sb, $"{allCharsIcon}All vs {biomeLabel} {catPlural}");
            AppendPoolDataCells(sb, allCharsPool);
        }
        else
        {
            // All acts: 2 rows.
            // Row 1 (colored): this char, all acts — colored vs all-chars pool
            AppendDescriptorCell(sb, $"{charIcon}vs all {catPlural}");
            AppendRow1PoolDataCells(sb, poolBiome, allCharsPool);

            // Row 2 (neutral baseline): all characters, all acts
            AppendDescriptorCell(sb, $"{allCharsIcon}All vs all {catPlural} (baseline)");
            AppendPoolDataCells(sb, allCharsPool);
        }

        sb.Append("[/table]");

        // Footer — character omitted (each row shows its character context inline).
        var filterCtx = CardHoverShowPatch.BuildFilterContext("All chars", filter, includeCharacter: false);
        if (filterCtx.Length > 0)
            sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{filterCtx}[/color][/font_size][/font]");

        return sb.ToString();
    }

    /// <summary>
    /// Derives coloration baselines from an encounter-weighted PoolMetrics. Used as
    /// the reference for coloring the focused encounter row. The median and IQRC
    /// baselines are derived from the median-aggregated pool values (so a row showing
    /// "median 23" is colored against the median of per-encounter medians, not the mean
    /// of per-encounter avg dmg%). The other baselines stay mean-aggregated.
    /// </summary>
    private static (double medianBase, double iqrcBase, double deathRateBase, double turnsBase, double potsBase) DeriveBaselines(PoolMetrics m)
    {
        if (m.Fought == 0) return (0, 1.0, 10.0, 0, 0);
        double iqrcBase = 1.0;
        if (m.IQR.HasValue && m.Median > 0)
            iqrcBase = (m.IQR.Value.p75 - m.IQR.Value.p25) / m.Median;
        return (m.Median, iqrcBase, m.DeathRate, m.AvgTurns, m.AvgPots);
    }

    /// <summary>
    /// ColBad variant for metrics whose absolute scale is much smaller than 0–100
    /// (turns, potions, raw median damage). Expresses the value as a percentage of the
    /// baseline (100 = matches baseline, &gt;100 = above) so the existing significance
    /// thresholds in <see cref="TooltipHelper.ColWR"/> trigger meaningfully on small
    /// absolute differences. Falls back to neutral grey when the baseline is zero.
    /// </summary>
    private static string ColBadRelative(string text, double value, double baseline, int n)
    {
        if (baseline <= 0) return $"[color={TooltipHelper.NeutralShade}]{text}[/color]";
        double pctOfBaseline = value / baseline * 100.0;
        return ColBad(text, pctOfBaseline, n, 100.0);
    }

    /// <summary>Data row from an encounter-weighted PoolMetrics in a section color.
    /// Each metric is already an average across per-encounter values.</summary>
    private static string FormatPoolDataRow(PoolMetrics m, string color)
    {
        int n = m.Fought;
        string fIqr = m.IQR.HasValue
            ? $"{m.IQR.Value.p25:F0}-{m.IQR.Value.p75:F0}"
            : "-";
        var fDmg    = $"{m.Median:F0}";
        var fTurns  = $"{m.AvgTurns:F1}";
        var fPots   = $"{m.AvgPots:F1}";
        var fDeaths = $"{m.Died}/{n}";
        var pad     = new string(' ', Math.Max(0, 7 - fDeaths.Length));

        return $"  [color={color}]{n,6}  {fDmg,6}  {fIqr,-12}  {fTurns,5}  {fPots,7}  {pad}{fDeaths}[/color]";
    }

    /// <summary>Sub-label in Kreon font with optional section color. Null color uses default dim grey.
    /// Proportional font (not monospace) since the sub-labels don't need to align with column data.</summary>
    private static string SubLabel(string text, string? color)
        => $"[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=15][color={color ?? "#787878"}]{text}[/color][/font_size][/font]";

    /// <summary>Data row with coloration (no label column). Used for the focused encounter row.
    /// Column widths: Fought(6) | dmg(6) | IQR(12) | Turns(5) | Pots(7) | Deaths(7).
    /// Two-space separators between columns. All metric cells are colored against the
    /// pool baselines via ColBadRelative (high = bad, low = good).</summary>
    private static string FormatDataRow(EncounterEvent stat,
        double medianBase, double iqrcBase, double deathRateBase, double turnsBase, double potsBase)
    {
        int n = stat.Fought;
        double deathRate = 100.0 * stat.Died / n;
        double avgTurns  = (double)stat.TurnsTakenSum / n;
        double avgPots   = (double)stat.PotionsUsedSum / n;
        double dmgMedian = stat.DamageMedian() ?? (double)stat.DamageTakenSum / n;
        var iqr = stat.DamageIQR();
        string fIqr = iqr.HasValue ? $"{iqr.Value.p25:F0}-{iqr.Value.p75:F0}" : "-";

        var fDmg   = $"{dmgMedian:F0}";
        var fTurns = $"{avgTurns:F1}";
        var fPots  = $"{avgPots:F1}";

        var cN      = TooltipHelper.ColN($"{n,6}", n);
        var cDmg    = ColBadRelative($"{fDmg,6}", dmgMedian, medianBase, n);
        var cIqr    = FormatIqrCell(fIqr, iqr, dmgMedian, n, iqrcBase, width: 12, leftAlign: true);
        var cTurns  = ColBadRelative($"{fTurns,5}", avgTurns, turnsBase, n);
        var cPots   = ColBadRelative($"{fPots,7}", avgPots, potsBase, n);
        var cDeaths = FormatDeathsCell(stat.Died, n, deathRate, deathRateBase);

        return $"  {cN}  {cDmg}  {cIqr}  {cTurns}  {cPots}  {cDeaths}";
    }

    /// <summary>Data row in a section color (no label column). Used for context/pool rows.
    /// Column widths: Fought(6) | dmg(6) | IQR(12) | Turns(5) | Pots(7) | Deaths(7).</summary>
    private static string FormatSectionDataRow(EncounterEvent stat, string color)
    {
        int n = stat.Fought;
        double avgTurns = (double)stat.TurnsTakenSum / n;
        double avgPots  = (double)stat.PotionsUsedSum / n;
        double dmgMedian = stat.DamageMedian() ?? (double)stat.DamageTakenSum / n;
        var iqr = stat.DamageIQR();
        string fIqr = iqr.HasValue ? $"{iqr.Value.p25:F0}-{iqr.Value.p75:F0}" : "-";
        var fDmg    = $"{dmgMedian:F0}";
        var fTurns  = $"{avgTurns:F1}";
        var fPots   = $"{avgPots:F1}";
        var fDeaths = $"{stat.Died}/{n}";
        var pad     = new string(' ', Math.Max(0, 7 - fDeaths.Length));

        return $"  [color={color}]{n,6}  {fDmg,6}  {fIqr,-12}  {fTurns,5}  {fPots,7}  {pad}{fDeaths}[/color]";
    }

    /// <summary>Empty data row with dashes in a section color.</summary>
    private static string FormatEmptySectionDataRow(string color)
        => $"  [color={color}]{"-",6}  {"-",6}  {"-",-12}  {"-",5}  {"-",7}  {"-",7}[/color]";

    /// <summary>Empty data row with dashes (no label column, default neutral).</summary>
    private static string FormatEmptyDataRow()
        => FormatEmptySectionDataRow(ContextRowDataColor);

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
    internal static string BuildEncounterStatsTextSingleRow(
        EncounterEvent stat,
        double deathRateBaseline,
        double dmgPctBaseline,
        double iqrcBaseline,
        double dmgBaseline,
        string? character,
        string categoryLabel,
        AggregationFilter filter)
    {
        var sb = new StringBuilder();

        // Header — column widths align with FormatRowCombat:
        //   N(4)  Dmg(5)  Mid50%(9)  Turns(5)  Deaths(7)
        sb.Append("   N   Dmg   Mid 50% Turns  Deaths\n");

        if (stat.Fought > 0)
            sb.Append(FormatRowCombat(stat, deathRateBaseline, dmgPctBaseline, iqrcBaseline));
        else
            sb.Append($"[color={TooltipHelper.NeutralShade}]No data[/color]");

        // Footer — line 1 is the absolute-damage baseline
        // ("Baseline {category} pool dmg: X.X"). Line 2 is the filter
        // context — asc · char · version · profile, separator is the
        // interpunct (U+00B7). Segments collapse/disappear per the rules
        // in CardHoverShowPatch.BuildFilterContext.
        var baselineDmgAbs = $"{dmgBaseline:F1}";
        var charLabel = character != null
            ? CardHoverShowPatch.GetCharacterDisplay(character)
            : "All chars";
        var filterCtx = CardHoverShowPatch.BuildFilterContext(charLabel, filter);
        var categoryLower = categoryLabel.ToLowerInvariant();
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]Baseline {categoryLower} pool dmg: {baselineDmgAbs}");
        if (filterCtx.Length > 0)
            sb.Append($"\n{filterCtx}");
        sb.Append("[/color][/font_size][/font]");

        return sb.ToString();
    }

    internal static string NoDataText(string? characterLabel, int? ascensionMin, int? ascensionMax)
    {
        var ascPrefix = FormatAscensionPrefix(ascensionMin, ascensionMax);
        var label = characterLabel ?? "All chars";
        return $"[color={TooltipHelper.NeutralShade}]No encounter data\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16]{ascPrefix}{label}[/font_size][/font][/color]";
    }

    /// <summary>
    /// Bestiary multi-row formatter — 6 columns:
    ///   N | Dmg (median) | Mid 50% (p25-p75) | Turns | Pots | Deaths
    ///
    /// Dmg uses ColBad against the dmg-pct baseline. Mid 50% uses ColBad against the
    /// IQRC baseline (high IQRC = swingy = bad). Deaths colors the *numerator* (died)
    /// by ColBad against death-rate baseline; *denominator* (fought) by ColN for
    /// confidence.
    ///
    /// When DamageValues is null (old db that predates per-fight tracking), falls back
    /// to the mean (DamageTakenSum / Fought) so nothing breaks on upgrade.
    /// </summary>
    private static string FormatRow(string label, EncounterEvent stat, double deathRateBaseline, double dmgPctBaseline, double iqrcBaseline)
    {
        int n = stat.Fought;

        double deathRate = 100.0 * stat.Died / n;
        double avgTurns  = (double)stat.TurnsTakenSum / n;
        double avgPots   = (double)stat.PotionsUsedSum / n;
        double avgDmgPct = stat.DmgPctSum / n * 100.0;

        // Median damage. Fall back to mean if per-fight values not yet tracked.
        double dmgMedian = stat.DamageMedian() ?? (double)stat.DamageTakenSum / n;

        // IQR: p25-p75. Empty string if not enough data for a meaningful IQR.
        var iqr = stat.DamageIQR();
        string fIqr = iqr.HasValue
            ? $"{iqr.Value.p25:F0}-{iqr.Value.p75:F0}"
            : "-";

        var fDmg   = $"{dmgMedian:F0}";
        var fTurns = $"{avgTurns:F1}";
        var fPots  = $"{avgPots:F1}";

        var cN      = TooltipHelper.ColN($"{n,4}", n);
        var cDmg    = ColBad($"{fDmg,5}", avgDmgPct, n, dmgPctBaseline);
        var cIqr    = FormatIqrCell(fIqr, iqr, dmgMedian, n, iqrcBaseline);
        var cTurns  = $"[color={TooltipHelper.NeutralShade}]{fTurns,5}[/color]";
        var cPots   = $"[color={TooltipHelper.NeutralShade}]{fPots,5}[/color]";
        var cDeaths = FormatDeathsCell(stat.Died, n, deathRate, deathRateBaseline);

        return $"{label} {cN} {cDmg} {cIqr} {cTurns} {cPots} {cDeaths}";
    }

    /// <summary>
    /// In-combat single-row formatter — 5 columns: N | Dmg (median) | Mid 50% (IQR) |
    /// Turns | Deaths (drops Pots vs FormatRow, plus the per-character label since
    /// the in-combat tooltip is always for the current run's character). Tighter
    /// formatting so the table fits inside the standard hover-tip width.
    /// </summary>
    private static string FormatRowCombat(EncounterEvent stat, double deathRateBaseline, double dmgPctBaseline, double iqrcBaseline)
    {
        int n = stat.Fought;

        double deathRate = 100.0 * stat.Died / n;
        double avgTurns  = (double)stat.TurnsTakenSum / n;
        double avgDmgPct = stat.DmgPctSum / n * 100.0;

        double dmgMedian = stat.DamageMedian() ?? (double)stat.DamageTakenSum / n;
        var iqr = stat.DamageIQR();
        string fIqr = iqr.HasValue ? $"{iqr.Value.p25:F0}-{iqr.Value.p75:F0}" : "-";

        var fDmg   = $"{dmgMedian:F0}";
        var fTurns = $"{avgTurns:F1}";

        var cN      = TooltipHelper.ColN($"{n,4}", n);
        var cDmg    = ColBad($"{fDmg,5}", avgDmgPct, n, dmgPctBaseline);
        var cIqr    = FormatIqrCell(fIqr, iqr, dmgMedian, n, iqrcBaseline);
        var cTurns  = $"[color={TooltipHelper.NeutralShade}]{fTurns,5}[/color]";
        var cDeaths = FormatDeathsCell(stat.Died, n, deathRate, deathRateBaseline);

        return $"{cN} {cDmg} {cIqr} {cTurns} {cDeaths}";
    }

    /// <summary>
    /// Formats the Mid 50% (IQR) cell with IQRC-based coloration. IQRC = IQR / median.
    /// High IQRC (swingy encounter) → orange/red. Low IQRC (consistent) → teal/green.
    /// Falls back to neutral grey when there's not enough data for IQR or when median is zero.
    /// </summary>
    /// <summary>
    /// Formats the IQR (Mid 50%) cell. The focused-view tables use a wide column (12) and
    /// left-align the data so it sits flush with the median dmg column on its left rather
    /// than drifting to the right edge. The legacy bestiary all-characters table still
    /// uses the narrower 9-wide right-aligned default. <paramref name="leftAlign"/>
    /// switches between the two.
    /// </summary>
    private static string FormatIqrCell(string formattedIqr, (double p25, double p75)? iqr, double? median, int n, double iqrcBaseline, int width = 9, bool leftAlign = false)
    {
        var padded = leftAlign ? formattedIqr.PadRight(width) : formattedIqr.PadLeft(width);
        if (!iqr.HasValue || median == null || median.Value <= 0)
            return $"[color={TooltipHelper.NeutralShade}]{padded}[/color]";

        double iqrc = (iqr.Value.p75 - iqr.Value.p25) / median.Value;
        // Scale IQRC to percentage-like range for ColBad (which uses tanh significance).
        // Multiply by 100 so the deviation from baseline has similar magnitude to dmg%/death%.
        return ColBad(padded, iqrc * 100, n, iqrcBaseline * 100);
    }

    /// <summary>
    /// Builds the Deaths cell as "[colored]died[/colored]/[colored]fought[/colored]"
    /// right-aligned in a 7-char visual field. Two-axis coloration:
    ///   • Numerator (died) — ColBad against death-rate baseline. Encodes severity:
    ///     low rate = teal/good, high rate = orange/bad.
    ///   • Denominator (fought, "times seen") — ColN by sample size. Encodes
    ///     confidence: more samples → brighter/bolder grey.
    /// The two complement each other: a faint grey "1/2" reads as "low confidence",
    /// while a bright teal "0" over a bold "30" reads as "confidently safe".
    /// </summary>
    private static string FormatDeathsCell(int died, int fought, double deathRate, double deathRateBaseline)
    {
        var diedStr   = $"{died}";
        var foughtStr = $"{fought}";
        var combined  = $"{diedStr}/{foughtStr}";
        var pad       = new string(' ', Math.Max(0, 7 - combined.Length));
        var cDied     = ColBad(diedStr, deathRate, fought, deathRateBaseline);
        var cFought   = TooltipHelper.ColN(foughtStr, fought);
        return $"{pad}{cDied}[color={TooltipHelper.NeutralShade}]/[/color]{cFought}";
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
        return TooltipHelper.ColWR(text, baseline + (baseline - pct), n, baseline);
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
        // Merge per-fight damage lists for median/IQR on the "All" row.
        if (stat.DamageValues != null && stat.DamageValues.Count > 0)
        {
            total.DamageValues ??= new List<int>();
            total.DamageValues.AddRange(stat.DamageValues);
        }
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
