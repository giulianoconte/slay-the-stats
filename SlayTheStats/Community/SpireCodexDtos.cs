using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SlayTheStats.Community;

// DTOs for the Spire Codex stats API (https://spire-codex.com/api/runs/…).
// Only the fields slay-the-stats consumes are modelled; score/tier/elo are
// deliberately omitted (Area 4: methodology-specific, we layer our own
// coloration on the objective counts). All rate/avg fields are nullable —
// the API returns null for entities where a metric is undefined (e.g. potions
// have no pick_rate, encounters can have null avg_damage). Shapes verified
// live 2026-06-09; see spire-codex-corpus-measurements.md.

/// <summary>
/// Response of GET /api/runs/metrics/{cards|relics|potions}?cohort={all|a10}.
/// </summary>
public sealed class MetricsResponse
{
    [JsonPropertyName("cohort")]            public string Cohort { get; set; } = "";
    [JsonPropertyName("entity_type")]       public string EntityType { get; set; } = "";
    [JsonPropertyName("total_runs")]        public long TotalRuns { get; set; }
    [JsonPropertyName("baseline_win_rate")] public double? BaselineWinRate { get; set; }
    [JsonPropertyName("rows")]              public List<MetricRow> Rows { get; set; } = new();
}

/// <summary>One entity (card/relic/potion) row inside a <see cref="MetricsResponse"/>.</summary>
public sealed class MetricRow
{
    [JsonPropertyName("id")]               public string Id { get; set; } = "";
    [JsonPropertyName("upgraded")]         public bool Upgraded { get; set; }
    [JsonPropertyName("win_rate")]         public double? WinRate { get; set; }
    [JsonPropertyName("pick_rate")]        public double? PickRate { get; set; }
    [JsonPropertyName("picks")]            public int Picks { get; set; }
    [JsonPropertyName("wins")]             public int Wins { get; set; }
    [JsonPropertyName("losses")]           public int Losses { get; set; }
    [JsonPropertyName("offered")]          public int Offered { get; set; }
    [JsonPropertyName("picked")]           public int Picked { get; set; }
    [JsonPropertyName("pick_rate_by_act")] public List<double?> PickRateByAct { get; set; } = new();
}

/// <summary>
/// One page of GET /api/runs/encounter-stats. The endpoint paginates
/// (default 50/page) and 500s if given an <c>?act=</c> filter, so the client
/// pages through with no act filter and groups by the inline <see cref="EncounterRow.Act"/>.
/// </summary>
public sealed class EncounterStatsResponse
{
    [JsonPropertyName("encounters")] public List<EncounterRow> Encounters { get; set; } = new();
    [JsonPropertyName("total")]      public int Total { get; set; }
    [JsonPropertyName("limit")]      public int Limit { get; set; }
    [JsonPropertyName("page")]       public int Page { get; set; }
    [JsonPropertyName("has_next")]   public bool HasNext { get; set; }
}

/// <summary>One encounter row (all-character aggregate + per-character breakdown).</summary>
public sealed class EncounterRow
{
    [JsonPropertyName("encounter_id")] public string EncounterId { get; set; } = "";
    [JsonPropertyName("act")]          public int Act { get; set; }
    [JsonPropertyName("room_type")]    public string RoomType { get; set; } = "";
    [JsonPropertyName("total")]        public int Total { get; set; }
    [JsonPropertyName("fatal")]        public int Fatal { get; set; }
    [JsonPropertyName("avg_damage")]   public double? AvgDamage { get; set; }
    [JsonPropertyName("avg_turns")]    public double? AvgTurns { get; set; }
    [JsonPropertyName("characters")]   public List<EncounterCharacterRow> Characters { get; set; } = new();
}

/// <summary>Per-character slice of an <see cref="EncounterRow"/>.</summary>
public sealed class EncounterCharacterRow
{
    [JsonPropertyName("character")]  public string Character { get; set; } = "";
    [JsonPropertyName("total")]      public int Total { get; set; }
    [JsonPropertyName("fatal")]      public int Fatal { get; set; }
    [JsonPropertyName("avg_damage")] public double? AvgDamage { get; set; }
    [JsonPropertyName("avg_turns")]  public double? AvgTurns { get; set; }
}

/// <summary>Response of GET /api/runs/versions — the build_ids present in the corpus.</summary>
public sealed class VersionsResponse
{
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = new();
}
