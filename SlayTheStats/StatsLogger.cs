using System.Text;

namespace SlayTheStats;

public static class StatsLogger
{
    /// <summary>
    /// Logs a per-act stats table for every card that has been picked at least once.
    /// Aggregates across all characters and ascensions.
    /// Columns: Act | Runs Offered | Runs Picked | Win Rate | Pick Rate
    /// </summary>
    public static void LogAllCards(StatsDb db)
    {
        foreach (var (cardId, contextMap) in db.Cards)
        {
            var byAct = StatsAggregator.AggregateByAct(contextMap);
            if (byAct.Values.All(s => s.RunsPicked == 0))
                continue;

            MainFile.Logger.Info(FormatTable(cardId, byAct));
        }
    }

    private static string FormatTable(string cardId, Dictionary<int, CardStat> byAct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(cardId);
        sb.AppendLine($"  {"Act",-5} {"Offered",8} {"Picked",7} {"WR%",7} {"PR%",7}");
        sb.AppendLine($"  {new string('-', 36)}");

        foreach (var act in byAct.Keys.Order())
        {
            var s = byAct[act];
            var wr = s.RunsPicked > 0 ? (double)s.RunsWon    / s.RunsPicked  * 100 : 0;
            var pr = s.RunsOffered > 0 ? (double)s.RunsPicked / s.RunsOffered * 100 : 0;
            sb.AppendLine($"  {act,-5} {s.RunsOffered,8} {s.RunsPicked,7} {wr,6:F1}% {pr,6:F1}%");
        }

        return sb.ToString().TrimEnd();
    }
}
