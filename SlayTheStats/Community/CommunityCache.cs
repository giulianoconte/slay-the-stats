using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlayTheStats.Community;

/// <summary>
/// On-disk snapshot of the community stats, plus the refresh bookkeeping
/// (staleness + failure backoff). Deliberately Godot-free — load/save take an
/// explicit path (the manager supplies <c>OS.GetUserDataDir()</c>) so the whole
/// type, including the backoff math, is unit-testable like <see cref="StatsDb"/>.
/// The read adapter (Phase 2) maps these stored API payloads to tooltip rows.
/// </summary>
public sealed class CommunityCache
{
    /// <summary>Bumped when the cache structure changes; a mismatch discards the file.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>How long a fetched snapshot is considered fresh.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Failure backoff schedule (clamped to the last entry on repeated failures).</summary>
    public static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
        TimeSpan.FromDays(7),
    };

    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonPropertyName("fetched_utc")]    public DateTimeOffset? FetchedUtc { get; set; }
    /// <summary>Origin the snapshot was fetched from — lets the manager discard a
    /// dev (localhost) cache when the build's base URL differs.</summary>
    [JsonPropertyName("source_base_url")] public string SourceBaseUrl { get; set; } = "";
    [JsonPropertyName("total_runs")]     public long TotalRuns { get; set; }

    /// <summary>Per cohort ("all"/"a10") → the three entity metrics payloads.</summary>
    [JsonPropertyName("cohorts")]    public Dictionary<string, CohortMetrics> Cohorts { get; set; } = new();
    /// <summary>All encounter rows across acts; the adapter filters by <see cref="EncounterRow.Act"/>.</summary>
    [JsonPropertyName("encounters")] public List<EncounterRow> Encounters { get; set; } = new();
    [JsonPropertyName("versions")]   public List<string> Versions { get; set; } = new();

    [JsonPropertyName("backoff")]    public BackoffState Backoff { get; set; } = new();

    [JsonIgnore] public bool HasData => FetchedUtc.HasValue;

    /// <summary>True once the snapshot is older than <see cref="MaxAge"/> (or was never fetched).</summary>
    public bool IsStale(DateTimeOffset now) => FetchedUtc is not { } f || now - f >= MaxAge;

    /// <summary>True while a prior failure's backoff window is still open.</summary>
    public bool InBackoff(DateTimeOffset now) => Backoff.NextEligibleUtc is { } next && now < next;

    /// <summary>A refresh should be attempted iff data is stale and we're not in a backoff window.</summary>
    public bool ShouldRefresh(DateTimeOffset now) => !InBackoff(now) && IsStale(now);

    /// <summary>Records a failed refresh: advances the backoff step and sets the next-eligible time.
    /// A server <c>Retry-After</c> wins if it's longer than the scheduled step.</summary>
    public void RecordFailure(DateTimeOffset now, TimeSpan? retryAfter = null)
    {
        Backoff.ConsecutiveFailures++;
        int idx = Math.Min(Backoff.ConsecutiveFailures - 1, BackoffSchedule.Length - 1);
        var delay = BackoffSchedule[idx];
        if (retryAfter is { } ra && ra > delay) delay = ra;
        Backoff.NextEligibleUtc = now + delay;
    }

    /// <summary>Clears backoff after a successful refresh.</summary>
    public void RecordSuccess()
    {
        Backoff.ConsecutiveFailures = 0;
        Backoff.NextEligibleUtc = null;
    }

    private static readonly JsonSerializerOptions SerOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CommunityCache Load(string path, Action<string>? warn = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var cache = JsonSerializer.Deserialize<CommunityCache>(File.ReadAllText(path));
                if (cache == null) return new CommunityCache();
                if (cache.SchemaVersion != CurrentSchemaVersion)
                {
                    warn?.Invoke($"Community cache schema changed (file {cache.SchemaVersion}, current {CurrentSchemaVersion}). Discarding.");
                    return new CommunityCache();
                }
                return cache;
            }
        }
        catch (Exception e)
        {
            warn?.Invoke($"Failed to load community cache: {e.Message}");
        }
        return new CommunityCache();
    }

    /// <summary>Atomic publish: write a sibling .tmp then move-overwrite, so a reader
    /// (or a crash mid-write) never sees a half-written cache.</summary>
    public void Save(string path, Action<string>? warn = null)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, SerOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e)
        {
            warn?.Invoke($"Failed to save community cache: {e.Message}");
        }
    }
}

/// <summary>The three entity metrics payloads for one cohort.</summary>
public sealed class CohortMetrics
{
    [JsonPropertyName("cards")]   public MetricsResponse? Cards { get; set; }
    [JsonPropertyName("relics")]  public MetricsResponse? Relics { get; set; }
    [JsonPropertyName("potions")] public MetricsResponse? Potions { get; set; }
}

/// <summary>Persisted failure-backoff state, so backoff survives restarts.</summary>
public sealed class BackoffState
{
    [JsonPropertyName("consecutive_failures")] public int ConsecutiveFailures { get; set; }
    [JsonPropertyName("next_eligible_utc")]    public DateTimeOffset? NextEligibleUtc { get; set; }
}
