using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlayTheStats.Community;

/// <summary>
/// Local record of which runs we have pushed to Spire Codex (#36) — keyed by the
/// same runId (history filename, no extension) the stats parser uses. Two jobs:
///   1. <b>"log what we pushed"</b> — each entry carries the server-returned
///      <c>run_hash</c>, the submit timestamp, whether it was a duplicate, whether the
///      owner tag was attached, and whether a round-trip verify confirmed it landed.
///   2. <b>"don't re-push"</b> — <see cref="Contains"/> is the skip set so a launch only
///      submits runs not already in the ledger (the server dedups anyway, but this
///      avoids needless traffic and makes the once-per-run intent explicit).
///
/// Engine-free and unit-tested offline; the Godot-facing <see cref="RunSubmitter"/> owns
/// the path (under <c>OS.GetUserDataDir()</c>) and the network. Atomic save mirrors
/// <see cref="CommunityCache"/>: write a sibling .tmp, then move-overwrite.
/// </summary>
public sealed class SubmissionLedger
{
    /// <summary>Bumped if the on-disk shape changes incompatibly; a mismatch discards
    /// the file (we just re-derive it by re-submitting — idempotent, so harmless).</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>runId → submission record. runId is the .run filename without extension,
    /// matching <c>StatsDb.ProcessedRuns</c>.</summary>
    [JsonPropertyName("submitted")]
    public Dictionary<string, SubmissionEntry> Submitted { get; set; } = new();

    public bool Contains(string runId) => Submitted.ContainsKey(runId);

    /// <summary>Records (or overwrites) the outcome of submitting <paramref name="runId"/>.</summary>
    public void Record(string runId, SubmissionEntry entry) => Submitted[runId] = entry;

    private static readonly JsonSerializerOptions SerOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static SubmissionLedger Load(string path, Action<string>? warn = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var ledger = JsonSerializer.Deserialize<SubmissionLedger>(File.ReadAllText(path));
                if (ledger == null) return new SubmissionLedger();
                if (ledger.SchemaVersion != CurrentSchemaVersion)
                {
                    warn?.Invoke($"Submission ledger schema changed (file {ledger.SchemaVersion}, current {CurrentSchemaVersion}). Discarding.");
                    return new SubmissionLedger();
                }
                ledger.Submitted ??= new();
                return ledger;
            }
        }
        catch (Exception e)
        {
            warn?.Invoke($"Failed to load submission ledger: {e.Message}");
        }
        return new SubmissionLedger();
    }

    /// <summary>Atomic publish: write a sibling .tmp then move-overwrite, so a crash
    /// mid-write never leaves a half-written ledger.</summary>
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
            warn?.Invoke($"Failed to save submission ledger: {e.Message}");
        }
    }
}

/// <summary>One run's submission outcome (#36).</summary>
public sealed class SubmissionEntry
{
    /// <summary>Server-returned canonical hash (sha256[:16]); the key for round-trip verify.</summary>
    [JsonPropertyName("run_hash")]   public string? RunHash { get; set; }
    [JsonPropertyName("submitted_utc")] public DateTimeOffset SubmittedUtc { get; set; }
    /// <summary>The server already had this run (idempotent re-submit).</summary>
    [JsonPropertyName("duplicate")]  public bool Duplicate { get; set; }
    /// <summary>An <c>?steam_id=</c> owner tag was attached (player on Steam).</summary>
    [JsonPropertyName("owner_tagged")] public bool OwnerTagged { get; set; }
    /// <summary>A GET /shared/{run_hash} round-trip confirmed the corpus holds it.</summary>
    [JsonPropertyName("verified")]   public bool Verified { get; set; }
}
