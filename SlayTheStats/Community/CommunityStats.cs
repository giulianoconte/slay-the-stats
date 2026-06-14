using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace SlayTheStats.Community;

/// <summary>
/// Lifecycle owner for the community stats snapshot: loads the cache at boot and
/// runs a throttled, off-main-thread refresh against the Spire Codex stats API.
/// The current snapshot is published as a single volatile reference so main-thread
/// readers (the Phase 2 adapter) always see a consistent, fully-built cache.
///
/// This is the Godot-facing glue; the cache, backoff, DTOs, and client are all
/// engine-free and unit-tested separately.
/// </summary>
public static class CommunityStats
{
    /// <summary>The production origin. The build-time override (dev only) can point
    /// elsewhere, but a Release build is hard-pinned here (see <see cref="BaseUrl"/>).</summary>
    public const string ProdBaseUrl = "https://spire-codex.com";

    private const string CacheFileName = "slay-the-stats-community-cache.json";

    // The cohorts and entity types we mirror (Area 1: coarse "everyone" baseline).
    private static readonly string[] Cohorts = { "all", "a10" };
    private static readonly string[] EntityTypes = { "cards", "relics", "potions" };

    private static volatile CommunityCache _current = new();
    /// <summary>The live snapshot. Always non-null; empty until the first successful load/refresh.</summary>
    public static CommunityCache Current => _current;

    private static readonly SemaphoreSlim _refreshLock = new(1, 1);
    private static bool _attemptedThisLaunch;
    private static SpireCodexClient? _client;

    public static string CachePath => System.IO.Path.Combine(OS.GetUserDataDir(), CacheFileName);

    /// <summary>
    /// Resolved API origin. Defaults to the build-time <c>BuildInfo.SpireCodexBaseUrl</c>
    /// (prod unless a dev <c>deploy --spire-url=…</c> overrode it), but a Release build
    /// can NEVER use a non-prod URL — belt-and-suspenders against a localhost URL ever
    /// shipping. The deploy script already refuses <c>--spire-url</c> on a release build;
    /// this is the second layer.
    /// </summary>
    public static string BaseUrl
    {
        get
        {
            var configured = BuildInfo.SpireCodexBaseUrl;
            if (BuildInfo.IsRelease && configured != ProdBaseUrl)
            {
                MainFile.Logger.Warn($"Release build with non-prod Spire Codex URL '{configured}' — forcing prod '{ProdBaseUrl}'.");
                return ProdBaseUrl;
            }
            return string.IsNullOrWhiteSpace(configured) ? ProdBaseUrl : configured;
        }
    }

    private static string UserAgent =>
        $"SlayTheStats/{MainFile.ModVersion} (+https://github.com/giulianoconte/slay-the-stats)";

    /// <summary>Synchronous cache read into memory (no network). Called from MainFile.Initialize.</summary>
    public static void LoadAtBoot()
    {
        try
        {
            var cache = CommunityCache.Load(CachePath, msg => MainFile.Logger.Warn(msg));
            // Drop a snapshot fetched from a different origin (e.g. a dev localhost cache
            // left behind in a prod build) so we never serve cross-origin data.
            if (cache.HasData && !string.IsNullOrEmpty(cache.SourceBaseUrl) && cache.SourceBaseUrl != BaseUrl)
            {
                MainFile.Logger.Info($"Community cache origin '{cache.SourceBaseUrl}' != current '{BaseUrl}' — discarding.");
                cache = new CommunityCache { Backoff = cache.Backoff };
            }
            _current = cache;
            // One Info line per launch summarizing what we start with — including an active
            // backoff, so a silently-suppressed auto-refresh (the failure path advances the
            // backoff window) is visible without DebugMode.
            var b = cache.Backoff;
            string backoffNote = b.ConsecutiveFailures > 0
                ? $", backoff until {b.NextEligibleUtc:u} after {b.ConsecutiveFailures} failure(s)"
                : "";
            MainFile.Logger.Info($"Community cache loaded (fetched={cache.FetchedUtc?.ToString("o") ?? "never"}, runs={cache.TotalRuns}{backoffNote}).");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Community cache load failed: {e.Message}");
        }
    }

    /// <summary>
    /// Kicks an async refresh if warranted. Safe to call repeatedly (it's invoked
    /// from NMainMenu._Ready, which fires on every return to the menu) — it no-ops
    /// unless this is the first attempt this launch AND the cache says a refresh is due.
    /// </summary>
    public static void MaybeRefresh()
    {
        if (SlayTheStatsConfig.Community == SlayTheStatsConfig.CommunityMode.Off) return;
        if (_attemptedThisLaunch) return;
        if (!_current.ShouldRefresh(DateTimeOffset.UtcNow)) return;

        _attemptedThisLaunch = true;
        _ = Task.Run(RefreshAsync);
    }

    /// <summary>
    /// Manual "refresh now" (settings button, #34). Forces an attempt regardless of
    /// the staleness gate and the once-per-launch guard that <see cref="MaybeRefresh"/>
    /// honors — the user explicitly asked. The one thing it won't override is a server
    /// <c>Retry-After</c> window: we never hammer a server that asked us to wait. Single-
    /// flight is still enforced inside <see cref="RefreshAsync"/>, so a double-click is safe.
    /// </summary>
    public static void ForceRefresh()
    {
        if (SlayTheStatsConfig.Community == SlayTheStatsConfig.CommunityMode.Off) return;
        var now = DateTimeOffset.UtcNow;
        if (_current.InRetryAfter(now))
        {
            MainFile.Logger.Info(
                $"Manual community refresh skipped — server asked to retry after {_current.Backoff.RetryAfterUntilUtc:u}.");
            return;
        }
        // Logged synchronously (before the off-thread launch) so a button press is
        // visible immediately — distinguishes "button fired" from a refresh that
        // never concluded (network hang / game closed mid-fetch).
        MainFile.Logger.Info($"Manual community refresh requested (base={BaseUrl}).");
        _attemptedThisLaunch = true;
        _ = Task.Run(RefreshAsync);
    }

    /// <summary>Opens the Spire Codex site in the player's browser — the attribution /
    /// data-source link (settings button, #34). Always the public site, never a dev override.</summary>
    public static void OpenWebsite() => OS.ShellOpen(ProdBaseUrl);

    private static async Task RefreshAsync()
    {
        // Single-flight: never two refreshes in flight at once.
        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false))
        {
            MainFile.Logger.Info("Community refresh already in progress — skipping duplicate request.");
            return;
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            _client ??= new SpireCodexClient(BaseUrl, UserAgent);
            MainFile.Logger.Info($"Community refresh started (base={BaseUrl}).");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            // Partial publish: start from the live snapshot and overwrite only the units
            // (per cohort×entity metrics, encounters, versions) that succeed this round, so
            // one dead endpoint can't discard the data that fetched fine. Each unit's failure
            // is isolated; we keep its last-known-good value and retry the gaps next round.
            var built = _current.CloneForRefresh();
            built.SourceBaseUrl = BaseUrl;

            int ok = 0;
            var failures = new System.Collections.Generic.List<string>();
            TimeSpan? retryAfter = null;

            foreach (var cohort in Cohorts)
            {
                if (!built.Cohorts.TryGetValue(cohort, out var metrics))
                    built.Cohorts[cohort] = metrics = new CohortMetrics();
                foreach (var entity in EntityTypes)
                {
                    var r = await _client.GetMetricsAsync(entity, cohort, cts.Token).ConfigureAwait(false);
                    if (r.IsOk && r.Value != null)
                    {
                        switch (entity)
                        {
                            case "cards":   metrics.Cards = r.Value; break;
                            case "relics":  metrics.Relics = r.Value; break;
                            case "potions": metrics.Potions = r.Value; break;
                        }
                        ok++;
                    }
                    else
                    {
                        failures.Add($"metrics/{entity}?cohort={cohort}: {r.Error}");
                        retryAfter = MaxSpan(retryAfter, r.RetryAfter);
                    }
                }
            }

            var enc = await _client.GetEncounterStatsAsync(cts.Token).ConfigureAwait(false);
            if (enc.IsOk && enc.Value != null) { built.Encounters = enc.Value; ok++; }
            else { failures.Add($"encounter-stats: {enc.Error}"); retryAfter = MaxSpan(retryAfter, enc.RetryAfter); }

            var ver = await _client.GetVersionsAsync(cts.Token).ConfigureAwait(false);
            if (ver.IsOk && ver.Value != null) { built.Versions = ver.Value.Versions; ok++; }
            else { failures.Add($"versions: {ver.Error}"); retryAfter = MaxSpan(retryAfter, ver.RetryAfter); }

            // Nothing landed at all → keep the prior snapshot, advance backoff, publish nothing.
            if (ok == 0)
            {
                FailAndPersist(now, string.Join("; ", failures), retryAfter);
                return;
            }

            built.TotalRuns = built.Cohorts.TryGetValue("all", out var all) ? (all.Cards?.TotalRuns ?? 0) : 0;
            built.FetchedUtc = DateTimeOffset.UtcNow;
            built.Complete = failures.Count == 0;

            if (built.Complete)
                built.RecordSuccess();
            else
                // Partial: publish what we have, but schedule a retry to fill the gaps. The
                // backoff advances (so we don't hammer) while Complete=false keeps ShouldRefresh
                // true once the window elapses, even inside the 24h freshness window.
                built.RecordFailure(now, retryAfter);

            built.Save(CachePath, msg => MainFile.Logger.Warn(msg));
            _current = built; // atomic publish

            if (built.Complete)
                MainFile.Logger.Info($"Community stats refreshed: {built.TotalRuns} runs, {built.Encounters.Count} encounters, base={BaseUrl}.");
            else
                MainFile.Logger.Warn(
                    $"Community stats refreshed partially ({ok} of {ok + failures.Count} units ok): {string.Join("; ", failures)}. Retry after {built.Backoff.NextEligibleUtc:o}.");
        }
        catch (OperationCanceledException)
        {
            FailAndPersist(DateTimeOffset.UtcNow, "refresh timed out", null);
        }
        catch (Exception e)
        {
            FailAndPersist(DateTimeOffset.UtcNow, e.Message, null);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>The longer of two optional spans (null = absent), so a partial refresh
    /// honors the most conservative server-requested <c>Retry-After</c> across its units.</summary>
    private static TimeSpan? MaxSpan(TimeSpan? a, TimeSpan? b)
        => a is null ? b : b is null ? a : (a.Value >= b.Value ? a : b);

    /// <summary>Records a refresh failure on the live snapshot, persists the advanced
    /// backoff, and logs. Keeps the previously-loaded data intact (degraded, not wiped).</summary>
    private static void FailAndPersist(DateTimeOffset now, string reason, TimeSpan? retryAfter)
    {
        _current.RecordFailure(now, retryAfter);
        _current.Save(CachePath, msg => MainFile.Logger.Warn(msg));
        MainFile.Logger.Warn($"Community refresh failed ({reason}); next attempt after {_current.Backoff.NextEligibleUtc:o}.");
    }
}
