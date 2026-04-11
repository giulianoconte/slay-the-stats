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

        // All row — always shown
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

    // Section colors for focused-character view — subtle warm/cool tints on neutral grey.
    // Slightly nudged toward orange (baseline) and teal (pool) so the sub-label and its data
    // row are visually grouped without dominating the table.
    private const string BaselineSectionColor = "#a89888";  // subtle warm grey — biome pool baseline
    private const string PoolSectionColor     = "#8898a8";  // subtle cool grey — all-acts category pool
    private const string AllCharsSectionColor = "#989898";  // pure neutral grey — cross-character
    // Highlight colour for the focused encounter row's sub-label — gold/amber matching
    // the bestiary title and other in-game header text.
    private const string EncounterRowLabelColor = "#eabe51";

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
        var sb = new StringBuilder();
        sb.Append($"          {"median",6}   {"middle 50%",12}\n");
        sb.Append($"  {"Fought",6}  {"damage",6}  {"damage range",12}  {"turns",5}  {"potions",7}  {"deaths",7}\n");
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
            // IconBaseName = Id.Entry.ToLowerInvariant() = "prismaticgem".
            var candidates = new[]
            {
                "res://images/relics/prismaticgem.png",
                "res://images/packed/atlases/relic_atlas.sprites/prismaticgem.tres",
                "res://images/atlases/relic_atlas.sprites/prismaticgem.tres",
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
        AggregationFilter filter)
    {
        var sb = new StringBuilder();
        var charIcon = CharacterIcon(characterId, 22);
        var catLower = categoryLabel.ToLowerInvariant();

        // 2-line column header + separator
        sb.Append(FocusedColumnHeader());
        AppendSectionSeparator(sb);

        // Derive coloration baselines from the encounter-weighted act pool (preferred)
        // or fall back to the all-pool when there's no act data.
        var baselineSource = poolAct is { Fought: > 0 } ? poolAct.Value : poolAll;
        var (medianBase, iqrcBase, deathRateBase, turnsBase, potsBase) = DeriveBaselines(baselineSource);

        // Row 1: this encounter, this character — sub-label highlighted in gold,
        // data row colored against the encounter-weighted baseline
        sb.Append(SubLabel($"{charIcon}vs {encounterName}", EncounterRowLabelColor));
        sb.Append('\n');
        if (encounterStat.Fought > 0)
            sb.Append(FormatDataRow(encounterStat, medianBase, iqrcBase, deathRateBase, turnsBase, potsBase));
        else
            sb.Append(FormatEmptyDataRow());
        sb.Append('\n');

        // Row 2: act+category pool (baseline) — encounter-weighted across all encounters
        // in the same category within the encounter's act (so Act 1 includes both
        // Overgrowth and Underdocks for example). Neutral data cells.
        if (poolAct.HasValue && actLabel != null)
        {
            AppendSectionSeparator(sb);
            sb.Append(SubLabel($"{charIcon}vs {actLabel} {catLower} encounters (baseline)", BaselineSectionColor));
            sb.Append('\n');
            sb.Append(poolAct.Value.Fought > 0
                ? FormatPoolDataRow(poolAct.Value, TooltipHelper.NeutralShade)
                : FormatEmptySectionDataRow(TooltipHelper.NeutralShade));
            sb.Append('\n');
        }

        // Row 3: all-acts category pool — encounter-weighted. Always shown.
        AppendSectionSeparator(sb);
        sb.Append(SubLabel($"{charIcon}vs all {catLower} encounters", PoolSectionColor));
        sb.Append('\n');
        sb.Append(poolAll.Fought > 0
            ? FormatPoolDataRow(poolAll, TooltipHelper.NeutralShade)
            : FormatEmptySectionDataRow(TooltipHelper.NeutralShade));
        sb.Append('\n');

        // Row 4: all chars vs this encounter — character-weighted (each character is one
        // observation, median taken across characters for the percentile metrics). The
        // Prismatic Gem icon represents "all characters" (the relic that gives access
        // to every color in STS).
        var allCharsIcon = AllCharsIcon(22);
        AppendSectionSeparatorWithExtraSpace(sb);
        sb.Append(SubLabel($"{allCharsIcon}All vs {encounterName}", AllCharsSectionColor));
        sb.Append('\n');
        sb.Append(allCharsMetrics.Fought > 0
            ? FormatPoolDataRow(allCharsMetrics, AllCharsSectionColor)
            : FormatEmptySectionDataRow(AllCharsSectionColor));

        // Footer — character is omitted (each row already shows its character context).
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

    /// <summary>Appends a section separator with one extra blank line above for slightly
    /// more visual breathing room. Used before the all-chars section since it's a different
    /// reference axis (not part of the zoom-out progression).</summary>
    private static void AppendSectionSeparatorWithExtraSpace(StringBuilder sb)
    {
        sb.Append('\n');                  // extra blank line above
        sb.Append(SectionSeparator);
        sb.Append('\n');
    }

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
        var charIcon = CharacterIcon(characterId, 22);
        var catLower = categoryLabel.ToLowerInvariant();

        // 2-line column header + separator
        sb.Append(FocusedColumnHeader());
        AppendSectionSeparator(sb);

        // Row 1: biome+category pool, this character — encounter-weighted, neutral data
        if (biomeLabel != null)
        {
            sb.Append(SubLabel($"{charIcon}vs {biomeLabel} {catLower} encounters (baseline)", BaselineSectionColor));
            sb.Append('\n');
        }
        sb.Append(poolBiome.Fought > 0
            ? FormatPoolDataRow(poolBiome, TooltipHelper.NeutralShade)
            : FormatEmptySectionDataRow(TooltipHelper.NeutralShade));
        sb.Append('\n');

        // Row 2: all-acts category pool, this character — encounter-weighted, neutral data
        AppendSectionSeparator(sb);
        sb.Append(SubLabel($"{charIcon}vs all {catLower} encounters", PoolSectionColor));
        sb.Append('\n');
        sb.Append(poolAll.Fought > 0
            ? FormatPoolDataRow(poolAll, TooltipHelper.NeutralShade)
            : FormatEmptySectionDataRow(TooltipHelper.NeutralShade));
        sb.Append('\n');

        // Row 3: all chars for this category+biome — encounter-weighted, separated with extra space
        var allCharsIcon = AllCharsIcon(22);
        var allCharsLabel = biomeLabel != null
            ? $"{allCharsIcon}All vs {biomeLabel} {catLower} encounters"
            : $"{allCharsIcon}All vs {catLower} encounters";
        AppendSectionSeparatorWithExtraSpace(sb);
        sb.Append(SubLabel(allCharsLabel, AllCharsSectionColor));
        sb.Append('\n');
        sb.Append(allCharsPool.Fought > 0
            ? FormatPoolDataRow(allCharsPool, AllCharsSectionColor)
            : FormatEmptySectionDataRow(AllCharsSectionColor));

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

        return $"  [color={color}]{n,6}  {fDmg,6}  {fIqr,12}  {fTurns,5}  {fPots,7}  {pad}{fDeaths}[/color]";
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
        var cIqr    = FormatIqrCell(fIqr, iqr, dmgMedian, n, iqrcBase, width: 12);
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

        return $"  [color={color}]{n,6}  {fDmg,6}  {fIqr,12}  {fTurns,5}  {fPots,7}  {pad}{fDeaths}[/color]";
    }

    /// <summary>Empty data row with dashes in a section color.</summary>
    private static string FormatEmptySectionDataRow(string color)
        => $"  [color={color}]{"-",6}  {"-",6}  {"-",12}  {"-",5}  {"-",7}  {"-",7}[/color]";

    /// <summary>Empty data row with dashes (no label column, default neutral).</summary>
    private static string FormatEmptyDataRow()
        => FormatEmptySectionDataRow(TooltipHelper.NeutralShade);

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
    private static string FormatIqrCell(string formattedIqr, (double p25, double p75)? iqr, double? median, int n, double iqrcBaseline, int width = 9)
    {
        if (!iqr.HasValue || median == null || median.Value <= 0)
            return $"[color={TooltipHelper.NeutralShade}]{formattedIqr.PadLeft(width)}[/color]";

        double iqrc = (iqr.Value.p75 - iqr.Value.p25) / median.Value;
        // Scale IQRC to percentage-like range for ColBad (which uses tanh significance).
        // Multiply by 100 so the deviation from baseline has similar magnitude to dmg%/death%.
        return ColBad(formattedIqr.PadLeft(width), iqrc * 100, n, iqrcBaseline * 100);
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
