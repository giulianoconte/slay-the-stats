using System.Text;

namespace SlayTheStats;

/// <summary>
/// Generates encounter stats tooltip text matching the card/relic tooltip pattern.
/// Columns: Fought  Died  Turns  Pots  Dmg  Dmg%  Var  Var%
/// </summary>
public static class EncounterTooltipHelper
{
    internal static string BuildEncounterStatsText(
        Dictionary<int, EncounterEvent> actStats,
        double deathRateBaseline,
        double dmgPctBaseline,
        string characterLabel,
        int? ascensionMin,
        int? ascensionMax,
        string categoryLabel)
    {
        var sb = new StringBuilder();

        // Header — compact column labels
        sb.Append(" Act  Fght  Died Turns  Pots  Dmg Dmg%  Var Var%\n");

        // Accumulate totals for the "All" row
        var total = new EncounterEvent();

        for (int act = 1; act <= 3; act++)
        {
            if (actStats.TryGetValue(act, out var stat) && stat.Fought > 0)
            {
                Accumulate(total, stat);
                sb.Append(FormatRow($"{act,4}", stat, deathRateBaseline, dmgPctBaseline));
                sb.Append('\n');
            }
            else
            {
                sb.Append($"{act,4}  [color={TooltipHelper.NeutralShade}]{"-",4}  {"-",4} {"-",5}  {"-",4}  {"-",3} {"-",4}  {"-",3} {"-",4}[/color]\n");
            }
        }

        // Total row
        if (total.Fought > 0)
        {
            sb.Append(FormatRow(" All", total, deathRateBaseline, dmgPctBaseline));
        }

        // Footer
        var ascPrefix = FormatAscensionPrefix(ascensionMin, ascensionMax);
        var baselineDmg = $"{Math.Round(dmgPctBaseline):F0}%";
        sb.Append($"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color=#686868]{ascPrefix}{characterLabel} {categoryLabel} Avg Dmg%: {baselineDmg}[/color][/font_size][/font]");

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

        // Variance: E[X^2] - E[X]^2
        double varDmg    = (double)stat.DamageTakenSqSum / n - avgDmg * avgDmg;
        double varDmgPct = (stat.DmgPctSqSum / n - (stat.DmgPctSum / n) * (stat.DmgPctSum / n)) * 100.0 * 100.0;

        // Format values
        var fFought  = $"{n}";
        var fDied    = $"{stat.Died}";
        var fTurns   = $"{avgTurns:F1}";
        var fPots    = $"{avgPots:F1}";
        var fDmg     = $"{Math.Round(avgDmg)}";
        var fDmgPct  = $"{Math.Round(avgDmgPct)}%";
        var fVar     = $"{Math.Round(Math.Max(0, varDmg))}";
        var fVarPct  = $"{Math.Round(Math.Max(0, varDmgPct))}%";

        // Color coding:
        // - Fought: sample size coloring (ColN)
        // - Died: higher = worse (inverted ColWR)
        // - Dmg%: higher = worse (inverted)
        // - Turns, Pots, Dmg, Var, Var%: neutral (no baseline comparison)
        var cFought = TooltipHelper.ColN($"{fFought,4}", n);
        var cDied   = ColBad($"{fDied,4}", deathRate, n, deathRateBaseline);
        var cTurns  = $"[color={TooltipHelper.NeutralShade}]{fTurns,5}[/color]";
        var cPots   = $"[color={TooltipHelper.NeutralShade}]{fPots,4}[/color]";
        var cDmg    = $"[color={TooltipHelper.NeutralShade}]{fDmg,3}[/color]";
        var cDmgPct = ColBad($"{fDmgPct,4}", avgDmgPct, n, dmgPctBaseline);
        var cVar    = $"[color={TooltipHelper.NeutralShade}]{fVar,3}[/color]";
        var cVarPct = $"[color={TooltipHelper.NeutralShade}]{fVarPct,4}[/color]";

        return $"{label}  {cFought}  {cDied} {cTurns}  {cPots}  {cDmg} {cDmgPct}  {cVar} {cVarPct}";
    }

    /// <summary>
    /// Colors text where higher values are BAD (e.g. damage, death rate).
    /// Inverts the direction relative to ColWR: above baseline = BadShades, below = GoodShades.
    /// </summary>
    private static string ColBad(string text, double pct, int n, double baseline)
    {
        // Reuse ColWR with inverted baseline: swap pct/baseline so above-baseline goes red
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
