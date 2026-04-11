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

    /// <summary>
    /// Builds encounter stats for a focused single-character view. Section-based layout
    /// with descriptive sub-labels on their own lines between data rows:
    ///
    ///   [header]            N   Dmg   Mid 50% Turns  Pots  Deaths
    ///   [data — colored]    8    23     12-31   4.2   0.4     1/8
    ///   [sub-label]         (baseline) Hive Elite
    ///   [data — neutral]   42    18      9-25   3.8   0.3    4/42
    ///   [sub-label]         All Elite
    ///   [data — neutral]   97    15      7-22   3.5   0.2   8/97
    ///   [separator]
    ///   [sub-label]         All chars vs Soul Nexus
    ///   [data — neutral]   24    19     10-28   3.9   0.3    3/24
    ///
    /// Title (set by caller via SetStatsTitle): "Defect vs Soul Nexus".
    /// Row 1 colored against row 2 (biome+category pool). Biome is always the encounter's
    /// actual biome from metadata — not the selected biome tab.
    /// </summary>
    internal static string BuildEncounterStatsTextFocused(
        EncounterEvent encounterStat,
        EncounterEvent? poolBiomeStat,
        EncounterEvent poolAllStat,
        EncounterEvent allCharsStat,
        string encounterName,
        string? biomePoolLabel,
        string allPoolLabel,
        string categoryLabel,
        AggregationFilter filter)
    {
        var sb = new StringBuilder();

        // Column header — no label column, just a small indent.
        sb.Append($"  {"N",4} {"Dmg",5} {"Mid 50%",9} {"Turns",5} {"Pots",5} {"Deaths",7}\n");

        // Derive baselines for row 1 coloration from the pool row (biome pool if present, else all pool).
        var baselineSource = poolBiomeStat is { Fought: > 0 } ? poolBiomeStat : poolAllStat;
        var (dmgPctBase, deathRateBase, iqrcBase) = DeriveBaselines(baselineSource);

        // Row 1: this encounter, this character — colored
        if (encounterStat.Fought > 0)
            sb.Append(FormatDataRow(encounterStat, deathRateBase, dmgPctBase, iqrcBase));
        else
            sb.Append(FormatEmptyDataRow());
        sb.Append('\n');

        // Row 2: biome+category pool (skipped when encounter has no biome)
        if (poolBiomeStat != null && biomePoolLabel != null)
        {
            sb.Append(SubLabel($"(baseline) {biomePoolLabel} {categoryLabel}"));
            sb.Append('\n');
            if (poolBiomeStat.Fought > 0)
                sb.Append(FormatNeutralDataRow(poolBiomeStat));
            else
                sb.Append(FormatEmptyDataRow());
            sb.Append('\n');
        }

        // Row 3: category pool all biomes
        sb.Append(SubLabel($"All {categoryLabel}"));
        sb.Append('\n');
        if (poolAllStat.Fought > 0)
            sb.Append(FormatNeutralDataRow(poolAllStat));
        else
            sb.Append(FormatEmptyDataRow());

        // Separator + Row 4: all chars for this encounter
        sb.Append("\n\n");
        sb.Append(SubLabel($"All chars vs {encounterName}"));
        sb.Append('\n');
        if (allCharsStat.Fought > 0)
            sb.Append(FormatNeutralDataRow(allCharsStat));
        else
            sb.Append(FormatEmptyDataRow());

        // Footer — filter context only (baselines are visible as rows).
        var filterCtx = CardHoverShowPatch.BuildFilterContext("All chars", filter);
        if (filterCtx.Length > 0)
            sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{filterCtx}[/color][/font_size][/font]");

        return sb.ToString();
    }

    /// <summary>
    /// Builds category-level stats for a focused single-character view. Same section-based
    /// layout as the encounter view but without the top encounter row and with the bottom
    /// row showing all-characters for the category instead of a specific encounter:
    ///
    ///   [header]            N   Dmg   Mid 50% Turns  Pots  Deaths
    ///   [data — neutral]   42    18      9-25   3.8   0.3    4/42
    ///   [sub-label]         All Elite
    ///   [data — neutral]   97    15      7-22   3.5   0.2   8/97
    ///   [separator]
    ///   [sub-label]         All chars — Hive Elite
    ///   [data — neutral]   84    17     10-24   3.7   0.3    6/84
    ///
    /// Title (set by caller): "Defect — Hive Elite".
    /// </summary>
    internal static string BuildCategoryStatsTextFocused(
        EncounterEvent poolBiomeStat,
        EncounterEvent poolAllStat,
        EncounterEvent allCharsPoolStat,
        string? biomePoolLabel,
        string categoryLabel,
        AggregationFilter filter)
    {
        var sb = new StringBuilder();

        sb.Append($"  {"N",4} {"Dmg",5} {"Mid 50%",9} {"Turns",5} {"Pots",5} {"Deaths",7}\n");

        // Row 1: biome+category pool, this character
        if (poolBiomeStat.Fought > 0)
            sb.Append(FormatNeutralDataRow(poolBiomeStat));
        else
            sb.Append(FormatEmptyDataRow());
        sb.Append('\n');

        // Row 2: all-biome category pool, this character
        sb.Append(SubLabel($"All {categoryLabel}"));
        sb.Append('\n');
        if (poolAllStat.Fought > 0)
            sb.Append(FormatNeutralDataRow(poolAllStat));
        else
            sb.Append(FormatEmptyDataRow());

        // Separator + Row 3: all chars for this category+biome
        var allCharsLabel = biomePoolLabel != null
            ? $"All chars — {biomePoolLabel} {categoryLabel}"
            : $"All chars — {categoryLabel}";
        sb.Append("\n\n");
        sb.Append(SubLabel(allCharsLabel));
        sb.Append('\n');
        if (allCharsPoolStat.Fought > 0)
            sb.Append(FormatNeutralDataRow(allCharsPoolStat));
        else
            sb.Append(FormatEmptyDataRow());

        var filterCtx = CardHoverShowPatch.BuildFilterContext("All chars", filter);
        if (filterCtx.Length > 0)
            sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{filterCtx}[/color][/font_size][/font]");

        return sb.ToString();
    }

    /// <summary>
    /// Derives coloration baselines from an EncounterEvent (used as the reference pool
    /// row for coloring the focused encounter row).
    /// </summary>
    private static (double dmgPctBaseline, double deathRateBaseline, double iqrcBaseline) DeriveBaselines(EncounterEvent stat)
    {
        if (stat.Fought == 0) return (20.0, 10.0, 1.0);
        double dmgPct = stat.DmgPctSum / stat.Fought * 100.0;
        double deathRate = 100.0 * stat.Died / stat.Fought;
        double iqrc = 1.0;
        var median = stat.DamageMedian();
        var iqr = stat.DamageIQR();
        if (median != null && iqr != null && median.Value > 0)
            iqrc = (iqr.Value.p75 - iqr.Value.p25) / median.Value;
        return (dmgPct, deathRate, iqrc);
    }

    /// <summary>Sub-label in smaller, dimmer text — displayed on its own line between data rows.</summary>
    private static string SubLabel(string text)
        => $"[font_size=14][color=#787878]{text}[/color][/font_size]";

    /// <summary>Data row with coloration (no label column). Used for the focused encounter row.</summary>
    private static string FormatDataRow(EncounterEvent stat, double deathRateBaseline, double dmgPctBaseline, double iqrcBaseline)
    {
        int n = stat.Fought;
        double deathRate = 100.0 * stat.Died / n;
        double avgTurns  = (double)stat.TurnsTakenSum / n;
        double avgPots   = (double)stat.PotionsUsedSum / n;
        double avgDmgPct = stat.DmgPctSum / n * 100.0;
        double dmgMedian = stat.DamageMedian() ?? (double)stat.DamageTakenSum / n;
        var iqr = stat.DamageIQR();
        string fIqr = iqr.HasValue ? $"{iqr.Value.p25:F0}-{iqr.Value.p75:F0}" : "-";

        var fDmg   = $"{dmgMedian:F0}";
        var fTurns = $"{avgTurns:F1}";
        var fPots  = $"{avgPots:F1}";

        var cN      = TooltipHelper.ColN($"{n,4}", n);
        var cDmg    = ColBad($"{fDmg,5}", avgDmgPct, n, dmgPctBaseline);
        var cIqr    = FormatIqrCell(fIqr, iqr, dmgMedian, n, iqrcBaseline);
        var cTurns  = $"[color={TooltipHelper.NeutralShade}]{fTurns,5}[/color]";
        var cPots   = $"[color={TooltipHelper.NeutralShade}]{fPots,5}[/color]";
        var cDeaths = FormatDeathsCell(stat.Died, n, deathRate, deathRateBaseline);

        return $"  {cN} {cDmg} {cIqr} {cTurns} {cPots} {cDeaths}";
    }

    /// <summary>Data row with all neutral grey (no label column). Used for context rows.</summary>
    private static string FormatNeutralDataRow(EncounterEvent stat)
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

        var c = $"[color={TooltipHelper.NeutralShade}]";
        return $"  {c}{n,4}[/color] {c}{fDmg,5}[/color] {c}{fIqr,9}[/color] {c}{fTurns,5}[/color] {c}{fPots,5}[/color] {c}{pad}{fDeaths}[/color]";
    }

    /// <summary>Empty data row with dashes (no label column).</summary>
    private static string FormatEmptyDataRow()
    {
        var c = $"[color={TooltipHelper.NeutralShade}]";
        return $"  {c}{"-",4}[/color] {c}{"-",5}[/color] {c}{"-",9}[/color] {c}{"-",5}[/color] {c}{"-",5}[/color] {c}{"-",7}[/color]";
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
    private static string FormatIqrCell(string formattedIqr, (double p25, double p75)? iqr, double? median, int n, double iqrcBaseline)
    {
        if (!iqr.HasValue || median == null || median.Value <= 0)
            return $"[color={TooltipHelper.NeutralShade}]{formattedIqr,9}[/color]";

        double iqrc = (iqr.Value.p75 - iqr.Value.p25) / median.Value;
        // Scale IQRC to percentage-like range for ColBad (which uses tanh significance).
        // Multiply by 100 so the deviation from baseline has similar magnitude to dmg%/death%.
        return ColBad($"{formattedIqr,9}", iqrc * 100, n, iqrcBaseline * 100);
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
