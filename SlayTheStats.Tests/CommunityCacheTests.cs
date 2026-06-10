using System;
using System.IO;
using SlayTheStats.Community;
using Xunit;

namespace SlayTheStats.Tests;

public class CommunityCacheTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"sts-community-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_MissingFile_ReturnsEmptyCache()
    {
        var cache = CommunityCache.Load(TempPath());
        Assert.False(cache.HasData);
        Assert.Empty(cache.Cohorts);
        Assert.Equal(CommunityCache.CurrentSchemaVersion, cache.SchemaVersion);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = TempPath();
        try
        {
            var fetched = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
            var src = new CommunityCache
            {
                FetchedUtc = fetched,
                SourceBaseUrl = "https://spire-codex.com",
                TotalRuns = 217212,
                Versions = { "v0.107.0", "v0.106.1" },
            };
            src.Cohorts["all"] = new CohortMetrics
            {
                Cards = new MetricsResponse
                {
                    Cohort = "all",
                    EntityType = "cards",
                    TotalRuns = 217212,
                    BaselineWinRate = 42.5,
                    Rows = { new MetricRow { Id = "GLASSWORK", WinRate = 33.5, PickRate = 25.3, Picks = 5903, Wins = 1975 } },
                },
            };
            src.Encounters.Add(new EncounterRow { EncounterId = "SLIMES_WEAK", Act = 1, RoomType = "monster", Total = 75841, Fatal = 485, AvgDamage = 5.8 });
            src.Save(path);

            var loaded = CommunityCache.Load(path);
            Assert.True(loaded.HasData);
            Assert.Equal(fetched, loaded.FetchedUtc);
            Assert.Equal("https://spire-codex.com", loaded.SourceBaseUrl);
            Assert.Equal(217212, loaded.TotalRuns);
            Assert.Equal(42.5, loaded.Cohorts["all"].Cards!.BaselineWinRate);
            Assert.Equal("GLASSWORK", loaded.Cohorts["all"].Cards!.Rows[0].Id);
            Assert.Single(loaded.Encounters);
            Assert.Equal("SLIMES_WEAK", loaded.Encounters[0].EncounterId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_IsAtomic_NoTmpLeftBehind()
    {
        var path = TempPath();
        try
        {
            new CommunityCache { FetchedUtc = DateTimeOffset.UtcNow }.Save(path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_SchemaMismatch_Discards()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schema_version\":999,\"total_runs\":5,\"fetched_utc\":\"2026-06-09T00:00:00+00:00\"}");
            string? warned = null;
            var cache = CommunityCache.Load(path, w => warned = w);
            Assert.False(cache.HasData);
            Assert.Equal(0, cache.TotalRuns);
            Assert.NotNull(warned);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IsStale_FreshFalse_OldTrue_NeverTrue()
    {
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.True(new CommunityCache().IsStale(now)); // never fetched
        Assert.False(new CommunityCache { FetchedUtc = now - TimeSpan.FromHours(1) }.IsStale(now));
        Assert.True(new CommunityCache { FetchedUtc = now - TimeSpan.FromHours(25) }.IsStale(now));
    }

    [Fact]
    public void ShouldRefresh_StaleAndNotInBackoff_True()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new CommunityCache(); // never fetched → stale
        Assert.True(cache.ShouldRefresh(now));
    }

    [Fact]
    public void ShouldRefresh_InBackoff_False()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new CommunityCache();
        cache.RecordFailure(now); // sets a 1h backoff window
        Assert.False(cache.ShouldRefresh(now));
        Assert.True(cache.InBackoff(now));
        Assert.False(cache.InBackoff(now + TimeSpan.FromHours(2)));
    }

    [Fact]
    public void RecordFailure_AdvancesSchedule_AndClamps()
    {
        var now = new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero);
        var cache = new CommunityCache();

        cache.RecordFailure(now);
        Assert.Equal(now + TimeSpan.FromHours(1), cache.Backoff.NextEligibleUtc);
        cache.RecordFailure(now);
        Assert.Equal(now + TimeSpan.FromHours(6), cache.Backoff.NextEligibleUtc);
        cache.RecordFailure(now);
        Assert.Equal(now + TimeSpan.FromHours(24), cache.Backoff.NextEligibleUtc);
        cache.RecordFailure(now);
        Assert.Equal(now + TimeSpan.FromDays(7), cache.Backoff.NextEligibleUtc);
        cache.RecordFailure(now); // clamps at the last step
        Assert.Equal(now + TimeSpan.FromDays(7), cache.Backoff.NextEligibleUtc);
        Assert.Equal(5, cache.Backoff.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFailure_HonorsRetryAfterWhenLonger()
    {
        var now = new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero);
        var cache = new CommunityCache();
        // First step would be 1h; a 3h Retry-After should win.
        cache.RecordFailure(now, TimeSpan.FromHours(3));
        Assert.Equal(now + TimeSpan.FromHours(3), cache.Backoff.NextEligibleUtc);

        // A shorter Retry-After than the scheduled step does not shorten it.
        var c2 = new CommunityCache();
        c2.RecordFailure(now, TimeSpan.FromMinutes(5));
        Assert.Equal(now + TimeSpan.FromHours(1), c2.Backoff.NextEligibleUtc);
    }

    [Fact]
    public void RecordSuccess_ClearsBackoff()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new CommunityCache();
        cache.RecordFailure(now);
        cache.RecordFailure(now);
        cache.RecordSuccess();
        Assert.Equal(0, cache.Backoff.ConsecutiveFailures);
        Assert.Null(cache.Backoff.NextEligibleUtc);
        Assert.False(cache.InBackoff(now));
    }
}
