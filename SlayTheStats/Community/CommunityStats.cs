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
                MainFile.DebugLog($"Community cache origin '{cache.SourceBaseUrl}' != current '{BaseUrl}' — discarding.");
                cache = new CommunityCache { Backoff = cache.Backoff };
            }
            _current = cache;
            MainFile.DebugLog($"Community cache loaded (fetched={cache.FetchedUtc?.ToString("o") ?? "never"}, runs={cache.TotalRuns}).");
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

    private static async Task RefreshAsync()
    {
        // Single-flight: never two refreshes in flight at once.
        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            _client ??= new SpireCodexClient(BaseUrl, UserAgent);

            var built = new CommunityCache { SourceBaseUrl = BaseUrl };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            foreach (var cohort in Cohorts)
            {
                var metrics = new CohortMetrics();
                foreach (var entity in EntityTypes)
                {
                    var r = await _client.GetMetricsAsync(entity, cohort, cts.Token).ConfigureAwait(false);
                    if (!r.IsOk || r.Value == null)
                    {
                        FailAndPersist(now, $"metrics/{entity}?cohort={cohort}: {r.Error}", r.RetryAfter);
                        return;
                    }
                    switch (entity)
                    {
                        case "cards":   metrics.Cards = r.Value; break;
                        case "relics":  metrics.Relics = r.Value; break;
                        case "potions": metrics.Potions = r.Value; break;
                    }
                }
                built.Cohorts[cohort] = metrics;
            }

            var enc = await _client.GetEncounterStatsAsync(cts.Token).ConfigureAwait(false);
            if (!enc.IsOk || enc.Value == null)
            {
                FailAndPersist(now, $"encounter-stats: {enc.Error}", enc.RetryAfter);
                return;
            }
            built.Encounters = enc.Value;

            var ver = await _client.GetVersionsAsync(cts.Token).ConfigureAwait(false);
            if (ver.IsOk && ver.Value != null) built.Versions = ver.Value.Versions;

            built.FetchedUtc = DateTimeOffset.UtcNow;
            built.TotalRuns = built.Cohorts.TryGetValue("all", out var all) ? (all.Cards?.TotalRuns ?? 0) : 0;
            built.RecordSuccess();
            built.Save(CachePath, msg => MainFile.Logger.Warn(msg));

            _current = built; // atomic publish
            MainFile.Logger.Info($"Community stats refreshed: {built.TotalRuns} runs, {built.Encounters.Count} encounters, base={BaseUrl}.");
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

    /// <summary>Records a refresh failure on the live snapshot, persists the advanced
    /// backoff, and logs. Keeps the previously-loaded data intact (degraded, not wiped).</summary>
    private static void FailAndPersist(DateTimeOffset now, string reason, TimeSpan? retryAfter)
    {
        _current.RecordFailure(now, retryAfter);
        _current.Save(CachePath, msg => MainFile.Logger.Warn(msg));
        MainFile.DebugLog($"Community refresh failed ({reason}); next attempt after {_current.Backoff.NextEligibleUtc:o}.");
    }
}
