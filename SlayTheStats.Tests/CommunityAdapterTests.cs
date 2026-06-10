using SlayTheStats.Community;
using Xunit;

namespace SlayTheStats.Tests;

public class CommunityAdapterTests
{
    private static CommunityCache CacheWithCards(params MetricRow[] rows)
    {
        var cache = new CommunityCache();
        cache.Cohorts["all"] = new CohortMetrics
        {
            Cards = new MetricsResponse { Cohort = "all", EntityType = "cards", Rows = new(rows) },
        };
        return cache;
    }

    [Theory]
    [InlineData("STRIKE_R", true)]
    [InlineData("DEFEND_SILENT", true)]
    [InlineData("AKABEKO", true)]
    [InlineData("", false)]
    [InlineData("modded:thing", false)]
    [InlineData("Lowercase", false)]
    public void IsOfficialId_AcceptsGameIds(string id, bool expected)
        => Assert.Equal(expected, CommunityAdapter.IsOfficialId(id));

    [Theory]
    [InlineData("CARD.GLASSWORK", "GLASSWORK")]
    [InlineData("RELIC.AKABEKO", "AKABEKO")]
    [InlineData("GLASSWORK", "GLASSWORK")]
    [InlineData("glasswork", "GLASSWORK")]
    public void NormalizeId_StripsPrefixAndUppercases(string input, string expected)
        => Assert.Equal(expected, CommunityAdapter.NormalizeId(input));

    [Fact]
    public void GetEntityMetric_FindsRow_BarePrefixedOrCased()
    {
        var cache = CacheWithCards(new MetricRow { Id = "GLASSWORK", Upgraded = false, WinRate = 33.5, PickRate = 25.3, Picks = 5903, Wins = 1975 });

        foreach (var id in new[] { "GLASSWORK", "CARD.GLASSWORK", "glasswork" })
        {
            var m = CommunityAdapter.GetEntityMetric(cache, "cards", id, upgraded: false, "all");
            Assert.NotNull(m);
            Assert.Equal(33.5, m!.Value.WinRate);
            Assert.Equal(25.3, m.Value.PickRate);
        }
    }

    [Fact]
    public void GetEntityMetric_PrefersExactUpgradeVariant_FallsBackToOther()
    {
        var cache = CacheWithCards(
            new MetricRow { Id = "STRIKE_R", Upgraded = false, WinRate = 40 },
            new MetricRow { Id = "STRIKE_R", Upgraded = true, WinRate = 55 });

        Assert.Equal(55, CommunityAdapter.GetEntityMetric(cache, "cards", "STRIKE_R", upgraded: true, "all")!.Value.WinRate);
        Assert.Equal(40, CommunityAdapter.GetEntityMetric(cache, "cards", "STRIKE_R", upgraded: false, "all")!.Value.WinRate);

        // Only unupgraded present → upgraded request falls back to it.
        var oneSided = CacheWithCards(new MetricRow { Id = "BASH", Upgraded = false, WinRate = 50 });
        Assert.Equal(50, CommunityAdapter.GetEntityMetric(oneSided, "cards", "BASH", upgraded: true, "all")!.Value.WinRate);
    }

    [Fact]
    public void GetEntityMetric_NullWhenMissingOrNoCohortOrNonOfficial()
    {
        var cache = CacheWithCards(new MetricRow { Id = "GLASSWORK", WinRate = 33.5 });
        Assert.Null(CommunityAdapter.GetEntityMetric(cache, "cards", "NONEXISTENT", false, "all"));
        Assert.Null(CommunityAdapter.GetEntityMetric(cache, "cards", "GLASSWORK", false, "a10")); // cohort absent
        Assert.Null(CommunityAdapter.GetEntityMetric(cache, "cards", "modded:x", false, "all"));   // non-official
        Assert.Null(CommunityAdapter.GetEntityMetric(new CommunityCache(), "cards", "GLASSWORK", false, "all")); // empty cache
    }

    [Fact]
    public void GetEncounterMetric_SumsAcrossActs_WeightsAverages()
    {
        var cache = new CommunityCache();
        cache.Encounters.Add(new EncounterRow { EncounterId = "SLIMES_WEAK", Act = 1, Total = 100, Fatal = 10, AvgDamage = 5.0, AvgTurns = 4.0 });
        cache.Encounters.Add(new EncounterRow { EncounterId = "SLIMES_WEAK", Act = 2, Total = 300, Fatal = 6,  AvgDamage = 9.0, AvgTurns = 8.0 });

        var m = CommunityAdapter.GetEncounterMetric(cache, "SLIMES_WEAK");
        Assert.NotNull(m);
        Assert.Equal(400, m!.Value.Total);
        Assert.Equal(16, m.Value.Fatal);
        Assert.Equal(16.0 / 400, m.Value.DeathRate);
        // run-count-weighted: (5*100 + 9*300)/400 = 8.0
        Assert.Equal(8.0, m.Value.AvgDamage);
        Assert.Equal(7.0, m.Value.AvgTurns); // (4*100 + 8*300)/400
    }

    [Fact]
    public void GetEncounterMetric_NullWhenAbsent()
        => Assert.Null(CommunityAdapter.GetEncounterMetric(new CommunityCache(), "WHATEVER"));
}
