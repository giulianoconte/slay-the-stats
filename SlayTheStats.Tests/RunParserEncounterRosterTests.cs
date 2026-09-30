using Xunit;
using SlayTheStats;
using static SlayTheStats.Tests.RunFixture;

namespace SlayTheStats.Tests;

/// <summary>
/// Encounter rosters are one sampled observation, taken from the most recent run that
/// fought the encounter. Covers the recency rule and the two things it exists for:
/// stale rosters from older game builds, and the pet leakage of builds v0.106.1–v0.107.1.
/// </summary>
public class RunParserEncounterRosterTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string TempRun(string json)
    {
        var path = WriteTempFile(json);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    private string RunWith(long startTime, params string[] monsterIds) =>
        RunOn("v0.111.0", startTime, monsterIds);

    private string RunOn(string build, long startTime, params string[] monsterIds) =>
        TempRun(BuildWithEncounters(
            startTime: startTime,
            buildVersion: build,
            encounterActs:
            [[
                new EncounterFloor("ENCOUNTER.A", [.. monsterIds],
                    TurnsTaken: 3, DamageTaken: 5, CurrentHp: 70, MaxHp: 80)
            ]]));

    [Fact]
    public void NewerRun_ReplacesOlderRoster()
    {
        var db = new StatsDb();
        RunParser.ProcessRun(RunWith(1000, "MONSTER.A", "MONSTER.B"), "old", "default", db);
        RunParser.ProcessRun(RunWith(2000, "MONSTER.A", "MONSTER.C"), "new", "default", db);

        Assert.Equal(["MONSTER.A", "MONSTER.C"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);
        Assert.Equal(2000, db.EncounterMeta["ENCOUNTER.A"].SampledAt);
    }

    [Fact]
    public void OlderRun_DoesNotReplaceNewerRoster()
    {
        var db = new StatsDb();
        // Arrival order is filesystem order, so the older run routinely lands second.
        RunParser.ProcessRun(RunWith(2000, "MONSTER.A", "MONSTER.C"), "new", "default", db);
        RunParser.ProcessRun(RunWith(1000, "MONSTER.A", "MONSTER.B"), "old", "default", db);

        Assert.Equal(["MONSTER.A", "MONSTER.C"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);
        Assert.Equal(2000, db.EncounterMeta["ENCOUNTER.A"].SampledAt);
    }

    [Fact]
    public void RosterIsOneSample_NotTheUnionOfVariants()
    {
        var db = new StatsDb();
        RunParser.ProcessRun(RunWith(1000, "MONSTER.A", "MONSTER.B"), "r1", "default", db);
        RunParser.ProcessRun(RunWith(2000, "MONSTER.A", "MONSTER.C"), "r2", "default", db);

        // (mA,mB) and (mA,mC) are both real; (mA,mB,mC) is a lineup that never occurs.
        Assert.DoesNotContain("MONSTER.B", db.EncounterMeta["ENCOUNTER.A"].MonsterIds);
    }

    [Fact]
    public void PetFromLegacyBuild_IsDisplacedByCleanerRecentSample()
    {
        var db = new StatsDb();
        // Builds around v0.107 wrote the cumulative combatant list, so pets leaked in.
        RunParser.ProcessRun(RunOn("v0.107.0", 1000, "MONSTER.KNOWLEDGE_DEMON", "MONSTER.BYRDPIP"), "legacy", "default", db);
        // Builds since record the spawn set again.
        RunParser.ProcessRun(RunOn("v0.111.0", 2000, "MONSTER.KNOWLEDGE_DEMON"), "current", "default", db);

        Assert.Equal(["MONSTER.KNOWLEDGE_DEMON"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);
    }

    [Fact]
    public void OlderTrustedSample_BeatsNewerUntrustedOne()
    {
        var db = new StatsDb();
        // The whole point of the trust tier: recency alone would pick the v0.107.0 row.
        RunParser.ProcessRun(RunOn("v0.105.0", 1000, "MONSTER.KNOWLEDGE_DEMON"), "old-clean", "default", db);
        RunParser.ProcessRun(RunOn("v0.107.0", 9000, "MONSTER.KNOWLEDGE_DEMON", "MONSTER.BYRDPIP"), "new-dirty", "default", db);

        Assert.Equal(["MONSTER.KNOWLEDGE_DEMON"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);
        Assert.Equal(1000, db.EncounterMeta["ENCOUNTER.A"].SampledAt);
    }

    [Fact]
    public void UntrustedSample_IsUsedWhenItIsAllThereIs()
    {
        var db = new StatsDb();
        RunParser.ProcessRun(RunOn("v0.107.0", 1000, "MONSTER.KNOWLEDGE_DEMON", "MONSTER.BYRDPIP"), "only", "default", db);

        // Distrust must not mean discard — a wrong-ish roster beats no roster.
        Assert.Equal(["MONSTER.KNOWLEDGE_DEMON", "MONSTER.BYRDPIP"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);
    }

    [Fact]
    public void SummonsFromUntrustedBuild_AreDisplacedToo()
    {
        var db = new StatsDb();
        // The same corruption without a pet: Zapbot is a real enemy the Fabricator
        // summons mid-fight, so no pet-identification scheme would have caught it.
        RunParser.ProcessRun(RunOn("v0.107.0", 1000, "MONSTER.FABRICATOR", "MONSTER.ZAPBOT"), "dirty", "default", db);
        RunParser.ProcessRun(RunOn("v0.111.0", 2000, "MONSTER.FABRICATOR"), "clean", "default", db);

        Assert.Equal(["MONSTER.FABRICATOR"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);
    }

    [Theory]
    [InlineData("v0.105.0", true)]
    [InlineData("v0.106.0", false)]   // untested in practice, covered by the wider bound
    [InlineData("v0.106.1", false)]   // confirmed leaking
    [InlineData("v0.107.1", false)]   // confirmed leaking
    [InlineData("v0.110.9", false)]   // untested, covered
    [InlineData("v0.111.0", true)]    // confirmed clean
    [InlineData("v1.0.0", true)]
    [InlineData("UNKNOWN", true)]     // unparseable reads as trusted
    [InlineData("", true)]
    [InlineData(null, true)]
    public void BuildTrustWindow(string? build, bool trusted) =>
        Assert.Equal(trusted, EncounterMeta.IsRosterTrustedBuild(build));

    [Fact]
    public void NoPetAllowlist_ARealMonsterIsNeverStripped()
    {
        var db = new StatsDb();
        // Thieving Hopper reads Creature.PetOwner but is a genuine encounter monster;
        // an id-based pet filter would have erased it.
        RunParser.ProcessRun(RunWith(1000, "MONSTER.THIEVING_HOPPER"), "r1", "default", db);

        Assert.Equal(["MONSTER.THIEVING_HOPPER"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);
    }

    [Fact]
    public void RunWithoutStartTime_SeedsButNeverDisplacesADatedSample()
    {
        var db = new StatsDb();
        RunParser.ProcessRun(RunWith(0, "MONSTER.A"), "undated", "default", db);
        Assert.Equal(["MONSTER.A"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);

        RunParser.ProcessRun(RunWith(1000, "MONSTER.B"), "dated", "default", db);
        Assert.Equal(["MONSTER.B"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);

        RunParser.ProcessRun(RunWith(0, "MONSTER.A"), "undated2", "default", db);
        Assert.Equal(["MONSTER.B"], db.EncounterMeta["ENCOUNTER.A"].MonsterIds);
    }

    [Fact]
    public void SchemaBumpDiscardsOldDb_SoStoredRostersAreResampled()
    {
        // Rosters are only corrected by re-parsing, and runs are processed once. The
        // schema bump is what forces that reparse — without it a stale roster persists.
        var stale = new StatsDb { SchemaVersion = 9 };
        var path = Path.Combine(Path.GetTempPath(), $"sts-schema-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        stale.EncounterMeta["ENCOUNTER.A"] = new EncounterMeta { MonsterIds = ["MONSTER.STALE"] };
        stale.Save(path);

        var loaded = StatsDb.Load(path);

        Assert.Equal(StatsDb.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Empty(loaded.EncounterMeta);
    }
}
