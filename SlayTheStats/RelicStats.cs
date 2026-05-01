using System.Text.Json.Serialization;

namespace SlayTheStats;

/// <summary>
/// Stats for a single relic: how many runs it was present in the final build, and how many were won.
/// </summary>
public class RelicStat
{
    [JsonPropertyName("runs_present")] public int RunsPresent { get; set; }
    [JsonPropertyName("runs_won")]    public int RunsWon     { get; set; }

    // Per-run counters (Shop% — shop appearances and purchases)
    [JsonPropertyName("runs_shop_seen")]   public int RunsShopSeen   { get; set; }
    [JsonPropertyName("runs_shop_bought")] public int RunsShopBought { get; set; }

    // Experimental insights — per-event counters for within-offer (#1) and
    // skip-as-control (#2) techniques. Symmetric to CardStat. Populated from
    // relic_choices on non-shop floors (boss/elite relic offers, event relic
    // choices). See sts2-docs/slay-the-stats/insights.md.
    [JsonPropertyName("offered")]              public int Offered             { get; set; }
    [JsonPropertyName("picked")]               public int Picked              { get; set; }
    [JsonPropertyName("offered_won")]          public int OfferedWon          { get; set; }
    [JsonPropertyName("picked_won")]           public int PickedWon           { get; set; }
    [JsonPropertyName("offered_skipped")]      public int OfferedSkipped      { get; set; }
    [JsonPropertyName("offered_skipped_won")]  public int OfferedSkippedWon   { get; set; }

    [JsonPropertyName("runs_ever_present")]    public int RunsEverPresent     { get; set; }
    [JsonPropertyName("runs_ever_won")]        public int RunsEverWon         { get; set; }
}
