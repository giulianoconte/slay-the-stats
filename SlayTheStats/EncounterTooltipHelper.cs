using System.Text;

namespace SlayTheStats;

/// <summary>
/// Generates encounter stats text with rows per character.
/// Columns (bestiary multi-row): Dmg | Var | Turns | Pots | Deaths
/// Columns (in-combat single-row): Dmg | Var | Turns | Deaths   ← drops Pots so the
/// table fits within the normal hover-tip width (the in-combat tooltip uses
/// TooltipHelper.TooltipWidth, not EncounterTooltipWidth, after this trim).
///
/// Coloration:
///   Dmg     — ColBad(avgDmg, dmgBaseline, n=Fought) so high-damage encounters tint
///             orange/red and low-damage ones tint teal/green. Significance grows
///             with sample size (n).
///   Deaths  — Numerator (died) is colored by ColBad(deathRate, deathRateBaseline, n)
///             so the cell visualises both the rate AND the confidence (denominator).
///             Replaces the prior ColN-by-sample-size shading which conveyed
///             confidence but not severity.
///   Var, Turns, Pots — neutral (no good/bad direction; just informational).
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
        int? ascensionMin,
        int? ascensionMax,
        string categoryLabel,
        AggregationFilter? filter = null,
        string? characterLabel = null)
    {
        var sb = new StringBuilder();

        // Header — column widths align with FormatRow.
        //   Label(11)  Dmg(5)  Mid50%(9)  Turns(5)  Pots(5)  Deaths(7)  Dth%(5)
        // "Dmg" = median damage. "Mid 50%" = IQR (p25-p75).
        sb.Append("              Dmg   Mid 50% Turns  Pots  Deaths  Dth%\n");

        var total = new EncounterEvent();

        // Render known characters in canonical order
        var rendered = new HashSet<string>();
        foreach (var (charId, label) in CharacterOrder)
        {
            rendered.Add(charId);
            if (charStats.TryGetValue(charId, out var stat) && stat.Fought > 0)
            {
                Accumulate(total, stat);
                sb.Append(FormatRow($"{label,-11}", stat, deathRateBaseline, dmgPctBaseline));
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
            sb.Append(FormatRow($"{label,-11}", stat, deathRateBaseline, dmgPctBaseline));
            sb.Append('\n');
        }

        // All row — always shown
        if (total.Fought > 0)
            sb.Append(FormatRow($"{"All",-11}", total, deathRateBaseline, dmgPctBaseline));
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
        double dmgBaseline,
        string? character,
        string categoryLabel,
        AggregationFilter filter)
    {
        var sb = new StringBuilder();

        // Header — column widths align with FormatRowCombat:
        //   Dmg(5)  Mid50%(9)  Turns(5)  Deaths(7)
        sb.Append("  Dmg   Mid 50% Turns  Deaths\n");

        if (stat.Fought > 0)
            sb.Append(FormatRowCombat(stat, deathRateBaseline, dmgPctBaseline));
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
    ///   Dmg (median) | Mid 50% (p25-p75) | Turns | Pots | Deaths | Death%
    ///
    /// Dmg uses ColBad against the dmg-pct baseline. Death% uses ColBad against
    /// the death-rate baseline. Deaths colors the *denominator* (Fought) by sample size
    /// via ColN so the cell visualises confidence (more samples → brighter).
    ///
    /// When DamageValues is null (old db that predates per-fight tracking), falls back
    /// to the mean (DamageTakenSum / Fought) so nothing breaks on upgrade.
    /// </summary>
    private static string FormatRow(string label, EncounterEvent stat, double deathRateBaseline, double dmgPctBaseline)
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

        var fDmg      = $"{dmgMedian:F0}";
        var fTurns    = $"{avgTurns:F1}";
        var fPots     = $"{avgPots:F1}";
        var fDeathPct = $"{Math.Round(deathRate)}%";

        var cDmg      = ColBad($"{fDmg,5}", avgDmgPct, n, dmgPctBaseline);
        var cIqr      = $"[color={TooltipHelper.NeutralShade}]{fIqr,9}[/color]";
        var cTurns    = $"[color={TooltipHelper.NeutralShade}]{fTurns,5}[/color]";
        var cPots     = $"[color={TooltipHelper.NeutralShade}]{fPots,5}[/color]";
        var cDeaths   = FormatDeathsCell(stat.Died, n, deathRate, deathRateBaseline);
        var cDeathPct = ColBad($"{fDeathPct,5}", deathRate, n, deathRateBaseline);

        return $"{label} {cDmg} {cIqr} {cTurns} {cPots} {cDeaths} {cDeathPct}";
    }

    /// <summary>
    /// In-combat single-row formatter — 4 columns: Dmg (median) | Mid 50% (IQR) |
    /// Turns | Deaths (drops Pots vs FormatRow, plus the per-character label since
    /// the in-combat tooltip is always for the current run's character). Tighter
    /// formatting so the table fits inside the standard hover-tip width.
    /// </summary>
    private static string FormatRowCombat(EncounterEvent stat, double deathRateBaseline, double dmgPctBaseline)
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

        var cDmg    = ColBad($"{fDmg,5}", avgDmgPct, n, dmgPctBaseline);
        var cIqr    = $"[color={TooltipHelper.NeutralShade}]{fIqr,9}[/color]";
        var cTurns  = $"[color={TooltipHelper.NeutralShade}]{fTurns,5}[/color]";
        var cDeaths = FormatDeathsCell(stat.Died, n, deathRate, deathRateBaseline);

        return $"{cDmg} {cIqr} {cTurns} {cDeaths}";
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
        //   Dmg(5) Mid50%(9) Turns(5) Pots(5) Deaths(7) Dth%(5)
        return $"{label} {dash}{"-",5}[/color] {dash}{"-",9}[/color] {dash}{"-",5}[/color] {dash}{"-",5}[/color] {dash}{"-",7}[/color] {dash}{"-",5}[/color]";
    }

    private static string ColBad(string text, double pct, int n, double baseline)
    {
        return TooltipHelper.ColWR(text, baseline + (baseline - pct), n, baseline);
    }

    private static void Accumulate(EncounterEvent total, EncounterEvent stat)
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
