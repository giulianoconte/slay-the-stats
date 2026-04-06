using System.Text;

namespace SlayTheStats;

/// <summary>
/// Generates encounter stats text with rows per character.
/// Columns: Deaths (died/fought)  Death%  Turns  Pots  Dmg  Dmg%  Var  Var%
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

    /// <summary>
    /// Builds encounter stats text with one row per character + an "All" total row.
    /// </summary>
    internal static string BuildEncounterStatsText(
        Dictionary<string, EncounterEvent> charStats,
        double deathRateBaseline,
        double dmgPctBaseline,
        int? ascensionMin,
        int? ascensionMax,
        string categoryLabel)
    {
        var sb = new StringBuilder();

        // Header
        //           Label(11)  Deaths(7)  Dth%(5)  Turns(5)  Pots(4)  Dmg(4)  Dmg%(5)  Var(4)  Var%(5)
        sb.Append("            Deaths Dth% Turns Pots  Dmg Dmg%  Var Var%\n");

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

        // Footer
        var ascPrefix = FormatAscensionPrefix(ascensionMin, ascensionMax);
        var baselineDmg = $"{Math.Round(dmgPctBaseline):F0}%";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{ascPrefix}{categoryLabel} Avg Dmg%: {baselineDmg}[/color][/font_size][/font]");

        return sb.ToString();
    }

    /// <summary>
    /// Builds encounter stats text with a single row (no character breakdown).
    /// Used by the in-combat tooltip where the current character is already known.
    /// </summary>
    internal static string BuildEncounterStatsTextSingleRow(
        EncounterEvent stat,
        double deathRateBaseline,
        double dmgPctBaseline,
        string characterLabel,
        int? ascensionMin,
        int? ascensionMax,
        string categoryLabel)
    {
        var sb = new StringBuilder();

        sb.Append("       Deaths Dth% Turns Pots  Dmg Dmg%  Var Var%\n");

        if (stat.Fought > 0)
            sb.Append(FormatRow($"{characterLabel,-7}", stat, deathRateBaseline, dmgPctBaseline));
        else
            sb.Append($"[color={TooltipHelper.NeutralShade}]No data[/color]");

        var ascPrefix = FormatAscensionPrefix(ascensionMin, ascensionMax);
        var baselineDmg = $"{Math.Round(dmgPctBaseline):F0}%";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{ascPrefix}{categoryLabel} Avg Dmg%: {baselineDmg}[/color][/font_size][/font]");

        return sb.ToString();
    }

    internal static string NoDataText(string? characterLabel, int? ascensionMin, int? ascensionMax)
    {
        var ascPrefix = FormatAscensionPrefix(ascensionMin, ascensionMax);
        var label = characterLabel ?? "All chars";
        return $"[color={TooltipHelper.NeutralShade}]No encounter data\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16]{ascPrefix}{label}[/font_size][/font][/color]";
    }

    private static string FormatRow(string label, EncounterEvent stat, double deathRateBaseline, double dmgPctBaseline)
    {
        int n = stat.Fought;

        double deathRate = 100.0 * stat.Died / n;
        double avgTurns  = (double)stat.TurnsTakenSum / n;
        double avgPots   = (double)stat.PotionsUsedSum / n;
        double avgDmg    = (double)stat.DamageTakenSum / n;
        double avgDmgPct = stat.DmgPctSum / n * 100.0;

        double varDmg    = (double)stat.DamageTakenSqSum / n - avgDmg * avgDmg;
        double varDmgPct = (stat.DmgPctSqSum / n - (stat.DmgPctSum / n) * (stat.DmgPctSum / n)) * 100.0 * 100.0;

        // Deaths column: died/fought (e.g. "2/15")
        var diedStr = $"{stat.Died}";
        var foughtStr = $"{n}";
        // Pad the combined string to 6 chars width
        var deathsFrac = $"{diedStr}/{foughtStr}";
        var cDeaths = $"{new string(' ', Math.Max(0, 6 - deathsFrac.Length))}"
                    + $"{TooltipHelper.ColN(diedStr, n)}"
                    + $"[color={TooltipHelper.NeutralShade}]/{foughtStr}[/color]";

        var fDeathPct = $"{Math.Round(deathRate)}%";
        var fTurns   = $"{avgTurns:F1}";
        var fPots    = $"{avgPots:F1}";
        var fDmg     = $"{Math.Round(avgDmg)}";
        var fDmgPct  = $"{Math.Round(avgDmgPct)}%";
        var fVar     = $"{Math.Round(Math.Max(0, varDmg))}";
        var fVarPct  = $"{Math.Round(Math.Max(0, varDmgPct))}%";

        var cDeathPct = ColBad($"{fDeathPct,4}", deathRate, n, deathRateBaseline);
        var cTurns  = $"[color={TooltipHelper.NeutralShade}]{fTurns,5}[/color]";
        var cPots   = $"[color={TooltipHelper.NeutralShade}]{fPots,4}[/color]";
        var cDmg    = $"[color={TooltipHelper.NeutralShade}]{fDmg,4}[/color]";
        var cDmgPct = ColBad($"{fDmgPct,4}", avgDmgPct, n, dmgPctBaseline);
        var cVar    = $"[color={TooltipHelper.NeutralShade}]{fVar,4}[/color]";
        var cVarPct = $"[color={TooltipHelper.NeutralShade}]{fVarPct,4}[/color]";

        return $"{label} {cDeaths} {cDeathPct} {cTurns} {cPots} {cDmg} {cDmgPct} {cVar} {cVarPct}";
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
        // Match the column widths used in FormatRow: Deaths(6) Dth%(4) Turns(5) Pots(4) Dmg(4) Dmg%(4) Var(4) Var%(4)
        return $"{label} {dash}{"-",6}[/color] {dash}{"-",4}[/color] {dash}{"-",5}[/color] {dash}{"-",4}[/color] {dash}{"-",4}[/color] {dash}{"-",4}[/color] {dash}{"-",4}[/color] {dash}{"-",4}[/color]";
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
