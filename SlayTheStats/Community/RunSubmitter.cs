using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Platform;

namespace SlayTheStats.Community;

/// <summary>
/// The community write path (#36): best-effort, idempotent, non-blocking submission of
/// completed local runs to the Spire Codex corpus. Fires only in
/// <see cref="SlayTheStatsConfig.CommunityMode.ReadShare"/>, off the main thread, at most
/// once per launch — invoked from <c>MainMenuReadyPatch</c> right after the stats parser
/// has caught up (boot + every return to the menu, so the just-finished run is on disk).
///
/// Flow per run not already in the <see cref="SubmissionLedger"/>:
///   POST /api/runs (with <c>?steam_id=</c> when on Steam) → record the returned run_hash
///   in the ledger → round-trip GET /shared/{run_hash} to confirm it landed → mark
///   verified. The server dedups on run_hash, so a re-submit is always safe; the ledger
///   exists to log what we pushed and to avoid needless repeat traffic, not for
///   correctness. A write failure never touches the read path.
///
/// Bounded: at most <see cref="MaxSubmitsPerLaunch"/> network submits per launch (a
/// first-time opt-in with a big backlog paces itself across launches, well under the
/// server's 3000/hr), and it stops early on a server <c>Retry-After</c> or a run of
/// consecutive failures (network/server down).
/// </summary>
internal static class RunSubmitter
{
    /// <summary>Network submits per launch. A heavy first-install backlog drains over
    /// several launches rather than firing hundreds of requests at once.</summary>
    private const int MaxSubmitsPerLaunch = 200;

    /// <summary>Give up for this launch after this many consecutive hard failures — the
    /// server/network is likely down; retry next launch.</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>The <c>_sts</c> provenance stamp ("slay-the-stats/&lt;version&gt;") is injected
    /// into the OUTBOUND run copy only (never the local .run), to attribute runs as
    /// slay-the-stats-origin in the public export. It rides in the run JSON body, which the
    /// server stores in the shareable <c>{run_hash}.json</c> and ships in the export, but does
    /// NOT affect the dedup hash (fixed-field-based) or the Mongo doc (narrow projection).
    /// Inertness/roundtrip being verified (#46 follow-up).</summary>
    private const bool StampProvenance = true;

    private const string LedgerFileName = "slay-the-stats-submission-ledger.json";

    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static bool _attemptedThisLaunch;
    private static SpireCodexClient? _client;

    internal static string LedgerPath => System.IO.Path.Combine(OS.GetUserDataDir(), LedgerFileName);

    /// <summary>True when a write to the live prod corpus must be refused. The automatic
    /// per-launch / on-enable pass passes <paramref name="allowDevOptIn"/>=false: it NEVER
    /// writes to prod from a dev build, so enabling Read+Share can't auto-dump the backlog.
    /// Only the deliberate one-off button passes true, additionally honoring the
    /// <c>--spire-prod-write</c> opt-in (<see cref="BuildInfo.AllowDevProdWrite"/>). Release
    /// builds always write.</summary>
    private static bool ProdWriteBlocked(bool allowDevOptIn)
    {
        if (BuildInfo.IsRelease) return false;
        if (CommunityStats.BaseUrl != CommunityStats.ProdBaseUrl) return false;
        return !(allowDevOptIn && BuildInfo.AllowDevProdWrite);
    }

    /// <summary>
    /// Kick a submission pass if warranted. Safe to call repeatedly (invoked on every
    /// menu-ready): no-ops unless this is the first attempt this launch AND the mode is
    /// ReadShare. Captures the Steam owner id on the main thread, then hands off to a
    /// background task.
    /// </summary>
    internal static void MaybeSubmit()
    {
        if (SlayTheStatsConfig.Community != SlayTheStatsConfig.CommunityMode.ReadShare) return;

        // Dev safety: a non-release build must never write to the prod corpus (irreversible —
        // anonymous runs are undeletable, the _sts stamp ships in the public export). The
        // write path is normally exercised against a local instance (`deploy --spire-url=`,
        // sts2-docs#136); a deliberate one-off prod push is opted into with
        // `deploy --spire-prod-write` (#46). Release builds always target prod and submit
        // normally — this guard is the inverse of the release-hard-pin on the read URL.
        // The automatic pass never writes to prod from a dev build — even with
        // --spire-prod-write. That opt-in only empowers the explicit one-off button
        // (SubmitOneForDev); otherwise enabling Read+Share here would auto-dump the whole
        // pending backlog to the undeletable public corpus.
        if (ProdWriteBlocked(allowDevOptIn: false))
        {
            MainFile.Logger.Info("Run submit: dev build pointed at prod — automatic submission skipped (use --spire-url for a local instance; the deliberate prod one-off goes through the DEV 'Push one run' button on a --spire-prod-write build).");
            return;
        }

        if (_attemptedThisLaunch) return;
        _attemptedThisLaunch = true;

        // Owner tag: only when Steam is the live platform. Every non-Steam case (editor /
        // -fastmp / any future storefront) falls back to anonymous — GetLocalPlayerId would
        // otherwise return a fake constant or a non-Steam id with nowhere correct to go (#32).
        ulong? steamId = null;
        try
        {
            if (PlatformUtil.PrimaryPlatform == PlatformType.Steam)
                steamId = PlatformUtil.GetLocalPlayerId(PlatformType.Steam);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Run submit: Steam id lookup failed ({e.Message}); submitting anonymously.");
        }

        var ledgerPath = LedgerPath;
        _ = Task.Run(() => SubmitPendingAsync(steamId, ledgerPath));
    }

    private static async Task SubmitPendingAsync(ulong? steamId, string ledgerPath, int maxSubmits = MaxSubmitsPerLaunch)
    {
        // Single-flight: never two passes at once.
        if (!await _lock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var ledger = SubmissionLedger.Load(ledgerPath, m => MainFile.Logger.Warn(m));

            // Enumerate every .run on disk (across profiles); keep those not yet handled.
            var pending = new List<(string runId, string path)>();
            foreach (var (historyDir, _) in RunParser.GetHistoryDirectories())
            {
                foreach (var p in Directory.GetFiles(historyDir, "*.run"))
                {
                    var runId = System.IO.Path.GetFileNameWithoutExtension(p);
                    if (!ledger.Contains(runId)) pending.Add((runId, p));
                }
            }

            if (pending.Count == 0)
            {
                MainFile.Logger.Info("Run submit: nothing new to push.");
                return;
            }

            _client ??= new SpireCodexClient(CommunityStats.BaseUrl, CommunityStats.UserAgent);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            int submitted = 0, duplicates = 0, verified = 0, failed = 0, skipped = 0, consecutiveFailures = 0;
            MainFile.Logger.Info(
                $"Run submit: {pending.Count} run(s) pending; pushing up to {maxSubmits} this launch " +
                $"(owner tag={(steamId is { } id ? id.ToString() : "anonymous")}, base={CommunityStats.BaseUrl}).");

            bool ledgerDirty = false;
            for (int i = 0; i < pending.Count; i++)
            {
                if (submitted >= maxSubmits)
                {
                    MainFile.Logger.Info($"Run submit: cap ({maxSubmits}) reached; {pending.Count - i} run(s) deferred to next launch.");
                    break;
                }

                var (runId, path) = pending[i];

                string json;
                try { json = File.ReadAllText(path); }
                catch (Exception e) { MainFile.Logger.Warn($"Run submit: couldn't read {runId} ({e.Message}); skipping."); continue; }

                // Skip abandoned runs (matches the stats parser; the corpus gains nothing from
                // partial junk). Ledger a hashless marker so we don't re-read it every launch.
                if (IsAbandoned(json))
                {
                    ledger.Record(runId, new SubmissionEntry { SubmittedUtc = DateTimeOffset.UtcNow });
                    ledgerDirty = true;
                    skipped++;
                    continue;
                }

                var r = await _client.SubmitRunAsync(MaybeStamp(json), steamId, cts.Token).ConfigureAwait(false);
                if (!r.IsOk || r.Value == null)
                {
                    failed++;
                    consecutiveFailures++;
                    MainFile.Logger.Warn($"Run submit: {runId} failed ({r.Error}).");
                    if (r.RetryAfter is { } ra)
                    {
                        MainFile.Logger.Info($"Run submit: server asked to retry after {ra}; stopping this launch.");
                        break;
                    }
                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        MainFile.Logger.Warn($"Run submit: {consecutiveFailures} consecutive failures; stopping this launch.");
                        break;
                    }
                    continue;
                }

                consecutiveFailures = 0;
                submitted++;
                if (r.Value.Duplicate) duplicates++;

                var entry = new SubmissionEntry
                {
                    RunHash      = r.Value.RunHash,
                    SubmittedUtc = DateTimeOffset.UtcNow,
                    Duplicate    = r.Value.Duplicate,
                    OwnerTagged  = steamId.HasValue,
                };

                // Round-trip verify: confirm the corpus actually holds the run.
                if (!string.IsNullOrEmpty(r.Value.RunHash))
                {
                    var v = await _client.VerifyRunAsync(r.Value.RunHash!, cts.Token).ConfigureAwait(false);
                    if (v.IsOk && v.Value) { entry.Verified = true; verified++; }
                    else if (v.IsOk)
                        MainFile.Logger.Warn($"Run submit: {runId} pushed (hash {r.Value.RunHash}) but round-trip reports it not present yet.");
                    // verify transport error → leave unverified; not fatal, re-checkable later.
                }
                else
                {
                    MainFile.Logger.Warn($"Run submit: {runId} accepted but no run_hash returned — cannot verify.");
                }

                ledger.Record(runId, entry);
                ledgerDirty = true;

                // Checkpoint periodically so a crash mid-batch keeps prior progress.
                if (submitted % 25 == 0) { ledger.Save(ledgerPath, m => MainFile.Logger.Warn(m)); ledgerDirty = false; }
            }

            if (ledgerDirty) ledger.Save(ledgerPath, m => MainFile.Logger.Warn(m));

            MainFile.Logger.Info(
                $"Run submit done: {submitted} pushed ({duplicates} already-present, {verified} round-trip-verified), " +
                $"{skipped} abandoned-skipped, {failed} failed.");
        }
        catch (OperationCanceledException)
        {
            MainFile.Logger.Warn("Run submit: batch timed out (2 min); remaining runs deferred to next launch.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Run submit: unexpected error ({e.Message}).");
        }
        finally
        {
            _lock.Release();
        }
    }

#if DEV_BUILD
    /// <summary>DEV reset: delete the submission ledger and clear the once-per-launch guard,
    /// so the next ReadShare pass re-submits the whole history from scratch. Part of the
    /// "reset Spire Codex state" dev button (<c>SlayTheStatsConfig.CommunityResetState</c>).</summary>
    internal static void ResetLedgerAndState()
    {
        try { if (File.Exists(LedgerPath)) File.Delete(LedgerPath); }
        catch (Exception e) { MainFile.Logger.Warn($"Spire Codex reset: ledger delete failed: {e.Message}"); }
        _attemptedThisLaunch = false;
    }

    /// <summary>DEV one-off: push exactly ONE pending run, ignoring the once-per-launch and
    /// ReadShare-mode gates but still honoring the prod-write guard (so it reaches prod only
    /// on a <c>--spire-prod-write</c> build). Backs the "Push one run to corpus (dev)" config
    /// button — the controlled single-run probe for the #46 public-corpus verification, so the
    /// whole local backlog isn't dumped to an undeletable public corpus.</summary>
    internal static void SubmitOneForDev()
    {
        if (ProdWriteBlocked(allowDevOptIn: true))
        {
            MainFile.Logger.Warn("Run submit (dev one-off): build points at prod without --spire-prod-write; refusing. Rebuild with `deploy --spire-prod-write` to push to the public corpus.");
            return;
        }

        ulong? steamId = null;
        try
        {
            if (PlatformUtil.PrimaryPlatform == PlatformType.Steam)
                steamId = PlatformUtil.GetLocalPlayerId(PlatformType.Steam);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Run submit (dev one-off): Steam id lookup failed ({e.Message}); submitting anonymously.");
        }

        var ledgerPath = LedgerPath;
        MainFile.Logger.Info($"Run submit (dev one-off): pushing exactly 1 pending run to {CommunityStats.BaseUrl}.");
        _ = Task.Run(() => SubmitPendingAsync(steamId, ledgerPath, maxSubmits: 1));
    }
#endif

    private static bool IsAbandoned(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("was_abandoned", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>Inject the <c>_sts</c> provenance stamp into the OUTBOUND copy only (never
    /// written back to the local .run). No-op until <see cref="StampProvenance"/> is cleared.
    /// The stamp doesn't touch the server's dedup hash (canonical-field-based).</summary>
    private static string MaybeStamp(string json)
    {
        if (!StampProvenance) return json;
        try
        {
            if (JsonNode.Parse(json) is JsonObject obj)
            {
                obj["_sts"] = $"slay-the-stats/{MainFile.ModVersion}";
                return obj.ToJsonString();
            }
        }
        catch { /* fall through to the raw body */ }
        return json;
    }
}
