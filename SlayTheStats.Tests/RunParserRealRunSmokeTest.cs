using Xunit;
using SlayTheStats;

namespace SlayTheStats.Tests;

/// <summary>
/// Smoke test against the reference run cited in slay-the-stats.md
/// (1775364591.run). Skips when the fixture isn't present so CI stays
/// green without external data.
/// </summary>
public class RunParserRealRunSmokeTest
{
    private const string ReferenceRun =
        "/home/giuliano/dev/sts2/sts2-docs/slay-the-stats/1775364591.run";

    [Fact]
    public void Parses_ExpectedEvents()
    {
        if (!File.Exists(ReferenceRun)) return; // fixture not checked in — skip locally

        var db = new StatsDb();
        RunParser.ProcessRun(ReferenceRun, "1775364591", "default", db);

        // Ancient events (Neow) must be skipped; non-ancient events captured.
        Assert.False(db.EventVisits.ContainsKey("EVENT.NEOW"));
        Assert.True(db.EventVisits.ContainsKey("EVENT.DROWNING_BEACON"));
        Assert.True(db.EventVisits.ContainsKey("EVENT.CRYSTAL_SPHERE"));
        Assert.True(db.EventVisits.ContainsKey("EVENT.THE_LANTERN_KEY"));

        // Lantern Key spawns a combat — SpawnedEncounterId must be wired.
        var lantern = db.EventVisits["EVENT.THE_LANTERN_KEY"][0];
        Assert.Equal("ENCOUNTER.MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER", lantern.SpawnedEncounterId);
        Assert.Equal(2, lantern.OptionPath.Count);
    }
}
