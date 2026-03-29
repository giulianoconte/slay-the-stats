using System.Text.Json.Serialization;

namespace SlayTheStats;

/// <summary>
/// Stats for a single relic: how many runs it was present in the final build, and how many were won.
/// </summary>
public class RelicStat
{
    [JsonPropertyName("runs_present")] public int RunsPresent { get; set; }
    [JsonPropertyName("runs_won")]    public int RunsWon     { get; set; }
}
