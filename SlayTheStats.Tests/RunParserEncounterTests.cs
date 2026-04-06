using Xunit;
using SlayTheStats;
using static SlayTheStats.Tests.RunFixture;

namespace SlayTheStats.Tests;

public class RunParserEncounterTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private const string Ctx1 = "CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN|default";

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

    [Fact]
    public void SingleEncounter_RecordsFoughtAndStats()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT", "MONSTER.RAT"],
                TurnsTaken: 4, DamageTaken: 7, CurrentHp: 63, MaxHp: 80, HpHealed: 6)
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc = db.Encounters["ENCOUNTER.RATS_WEAK"][Ctx1];
        Assert.Equal(1, enc.Fought);
        Assert.Equal(4, enc.TurnsTakenSum);
        Assert.Equal(7, enc.DamageTakenSum);
        Assert.Equal(49, enc.DamageTakenSqSum); // 7*7
        Assert.Equal(80, enc.MaxHpSum);
    }

    [Fact]
    public void Death_RecordsDied()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(
            killedByEncounter: "ENCOUNTER.BOSS_BOSS",
            encounterActs:
            [[
                new EncounterFloor("ENCOUNTER.BOSS_BOSS", ["MONSTER.BOSS"],
                    TurnsTaken: 10, DamageTaken: 50, CurrentHp: 0, MaxHp: 80)
            ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc = db.Encounters["ENCOUNTER.BOSS_BOSS"][Ctx1];
        Assert.Equal(1, enc.Died);
    }

    [Fact]
    public void NoDeath_DiedStaysZero()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(
            killedByEncounter: "NONE.NONE",
            encounterActs:
            [[
                new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                    TurnsTaken: 3, DamageTaken: 5, CurrentHp: 75, MaxHp: 80)
            ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc = db.Encounters["ENCOUNTER.RATS_WEAK"][Ctx1];
        Assert.Equal(0, enc.Died);
    }

    [Fact]
    public void HpEntering_FromPreviousFloor()
    {
        // Floor 1: current_hp=63 (after combat). Floor 2: another encounter.
        // hp_entering for floor 2 should be 63 (previous floor's current_hp).
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                TurnsTaken: 3, DamageTaken: 10, CurrentHp: 63, MaxHp: 80, HpHealed: 7),
            new EncounterFloor("ENCOUNTER.SLUGS_NORMAL", ["MONSTER.SLUG"],
                TurnsTaken: 5, DamageTaken: 15, CurrentHp: 54, MaxHp: 80, HpHealed: 6)
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc2 = db.Encounters["ENCOUNTER.SLUGS_NORMAL"][Ctx1];
        Assert.Equal(63, enc2.HpEnteringSum); // previous floor's current_hp
    }

    [Fact]
    public void HpEntering_FirstFloor_ComputedFromStats()
    {
        // First floor: no previous floor. hp_entering = current_hp + damage_taken - hp_healed
        // 63 + 10 - 7 = 66
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                TurnsTaken: 3, DamageTaken: 10, CurrentHp: 63, MaxHp: 80, HpHealed: 7)
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc = db.Encounters["ENCOUNTER.RATS_WEAK"][Ctx1];
        Assert.Equal(66, enc.HpEnteringSum); // 63 + 10 - 7
    }

    [Fact]
    public void PotionCounting_WithPotions()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.BOSS_BOSS", ["MONSTER.BOSS"],
                TurnsTaken: 10, DamageTaken: 30, CurrentHp: 20, MaxHp: 80,
                PotionsUsed: ["POTION.ATTACK_POTION", "POTION.BLOOD_POTION"])
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc = db.Encounters["ENCOUNTER.BOSS_BOSS"][Ctx1];
        Assert.Equal(2, enc.PotionsUsedSum);
    }

    [Fact]
    public void PotionCounting_NoPotions()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                TurnsTaken: 3, DamageTaken: 5, CurrentHp: 75, MaxHp: 80)
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc = db.Encounters["ENCOUNTER.RATS_WEAK"][Ctx1];
        Assert.Equal(0, enc.PotionsUsedSum);
    }

    [Fact]
    public void EventEncounter_UsesSecondRoom()
    {
        // Build event encounter JSON manually since it has 2 rooms
        var eventFloorJson = BuildEventEncounterFloorJson(
            eventModelId: "EVENT.MYSTERIOUS_KNIGHT",
            encounterModelId: "ENCOUNTER.MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER",
            monsterIds: ["MONSTER.MYSTERIOUS_KNIGHT"],
            turnsTaken: 3, damageTaken: 11, currentHp: 50, maxHp: 80);

        var json = $$"""
        {
            "was_abandoned": false,
            "win": false,
            "ascension": 0,
            "players": [{ "character": "CHARACTER.IRONCLAD" }],
            "killed_by_encounter": "NONE.NONE",
            "acts": ["ACT.OVERGROWTH", "ACT.HIVE", "ACT.GLORY"],
            "map_point_history": [[{{eventFloorJson}}]]
        }
        """;

        var db = new StatsDb();
        RunParser.ProcessRun(TempRun(json), "run1", "default", db);

        Assert.True(db.Encounters.ContainsKey("ENCOUNTER.MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER"));
        var enc = db.Encounters["ENCOUNTER.MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER"][Ctx1];
        Assert.Equal(1, enc.Fought);
        Assert.Equal(3, enc.TurnsTakenSum);
        Assert.Equal(11, enc.DamageTakenSum);
    }

    [Fact]
    public void EncounterMeta_RecordsMonsterIdsAndCategory()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.KNIGHTS_ELITE", ["MONSTER.KNIGHT_A", "MONSTER.KNIGHT_B", "MONSTER.KNIGHT_C"],
                TurnsTaken: 6, DamageTaken: 20, CurrentHp: 40, MaxHp: 80)
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.True(db.EncounterMeta.ContainsKey("ENCOUNTER.KNIGHTS_ELITE"));
        var meta = db.EncounterMeta["ENCOUNTER.KNIGHTS_ELITE"];
        Assert.Equal(["MONSTER.KNIGHT_A", "MONSTER.KNIGHT_B", "MONSTER.KNIGHT_C"], meta.MonsterIds);
        Assert.Equal("elite", meta.Category);
    }

    [Fact]
    public void EncounterMeta_RecordsBiomeFromActsArray()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(
            actsArray: ["ACT.UNDERDOCKS", "ACT.HIVE", "ACT.GLORY"],
            encounterActs:
            [[
                new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                    TurnsTaken: 3, DamageTaken: 5, CurrentHp: 75, MaxHp: 80)
            ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var meta = db.EncounterMeta["ENCOUNTER.RATS_WEAK"];
        Assert.Equal("ACT.UNDERDOCKS", meta.Biome);
        Assert.Equal(1, meta.Act);
    }

    [Fact]
    public void WonRun_IncrementedOnWin()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(won: true, encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                TurnsTaken: 3, DamageTaken: 5, CurrentHp: 75, MaxHp: 80)
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc = db.Encounters["ENCOUNTER.RATS_WEAK"][Ctx1];
        Assert.Equal(1, enc.WonRun);
    }

    [Fact]
    public void WonRun_NotIncrementedOnLoss()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(won: false, encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                TurnsTaken: 3, DamageTaken: 5, CurrentHp: 75, MaxHp: 80)
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc = db.Encounters["ENCOUNTER.RATS_WEAK"][Ctx1];
        Assert.Equal(0, enc.WonRun);
    }

    [Fact]
    public void MultipleEncounters_AcrossActs_CorrectContextKeys()
    {
        var ctx2 = "CHARACTER.IRONCLAD|0|2|UNKNOWN|UNKNOWN|default";
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(
            actsArray: ["ACT.OVERGROWTH", "ACT.HIVE", "ACT.GLORY"],
            encounterActs:
            [
                [new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                    TurnsTaken: 3, DamageTaken: 5, CurrentHp: 75, MaxHp: 80)],
                [new EncounterFloor("ENCOUNTER.BOSS_BOSS", ["MONSTER.BOSS"],
                    TurnsTaken: 10, DamageTaken: 30, CurrentHp: 20, MaxHp: 80)]
            ]));

        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.True(db.Encounters["ENCOUNTER.RATS_WEAK"].ContainsKey(Ctx1));
        Assert.True(db.Encounters["ENCOUNTER.BOSS_BOSS"].ContainsKey(ctx2));
    }

    [Fact]
    public void DmgPct_ComputedCorrectly()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                TurnsTaken: 3, DamageTaken: 20, CurrentHp: 60, MaxHp: 80)
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var enc = db.Encounters["ENCOUNTER.RATS_WEAK"][Ctx1];
        Assert.Equal(0.25, enc.DmgPctSum, 4);       // 20/80 = 0.25
        Assert.Equal(0.0625, enc.DmgPctSqSum, 4);   // 0.25^2 = 0.0625
    }

    [Fact]
    public void AbandonedRun_SkipsEncounters()
    {
        var db = new StatsDb();
        var path = TempRun(BuildWithEncounters(abandoned: true, encounterActs:
        [[
            new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT"],
                TurnsTaken: 3, DamageTaken: 5, CurrentHp: 75, MaxHp: 80)
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.Empty(db.Encounters);
    }

    [Fact]
    public void EncounterMeta_NotOverwrittenOnSecondRun()
    {
        var db = new StatsDb();
        // First run: encounter with 2 rats
        var path1 = TempRun(BuildWithEncounters(
            actsArray: ["ACT.OVERGROWTH", "ACT.HIVE", "ACT.GLORY"],
            encounterActs:
            [[
                new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT", "MONSTER.RAT"],
                    TurnsTaken: 3, DamageTaken: 5, CurrentHp: 75, MaxHp: 80)
            ]]));
        RunParser.ProcessRun(path1, "run1", "default", db);

        // Second run: same encounter (meta already cached)
        var path2 = TempRun(BuildWithEncounters(
            actsArray: ["ACT.UNDERDOCKS", "ACT.HIVE", "ACT.GLORY"],
            encounterActs:
            [[
                new EncounterFloor("ENCOUNTER.RATS_WEAK", ["MONSTER.RAT", "MONSTER.RAT"],
                    TurnsTaken: 4, DamageTaken: 8, CurrentHp: 72, MaxHp: 80)
            ]]));
        RunParser.ProcessRun(path2, "run2", "default", db);

        // Meta should reflect first occurrence
        Assert.Equal("ACT.OVERGROWTH", db.EncounterMeta["ENCOUNTER.RATS_WEAK"].Biome);
        // But stats should be accumulated
        Assert.Equal(2, db.Encounters["ENCOUNTER.RATS_WEAK"][Ctx1].Fought);
    }
}

public class EncounterCategoryTests
{
    [Theory]
    [InlineData("ENCOUNTER.RATS_WEAK", "weak")]
    [InlineData("ENCOUNTER.SLUGS_NORMAL", "normal")]
    [InlineData("ENCOUNTER.KNIGHTS_ELITE", "elite")]
    [InlineData("ENCOUNTER.LAGAVULIN_MATRIARCH_BOSS", "boss")]
    [InlineData("ENCOUNTER.MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER", "event")]
    [InlineData("ENCOUNTER.OVERGROWTH_CRAWLERS", "unknown")]
    public void DeriveCategory(string modelId, string expected)
    {
        Assert.Equal(expected, EncounterCategory.Derive(modelId));
    }

    [Theory]
    [InlineData("ENCOUNTER.LAGAVULIN_MATRIARCH_BOSS", "Lagavulin Matriarch")]
    [InlineData("ENCOUNTER.RATS_WEAK", "Rats")]
    [InlineData("ENCOUNTER.MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER", "Mysterious Knight")]
    [InlineData("ENCOUNTER.OVERGROWTH_CRAWLERS", "Overgrowth Crawlers")]
    [InlineData("ENCOUNTER.SOUL_NEXUS_ELITE", "Soul Nexus")]
    public void FormatName(string modelId, string expected)
    {
        Assert.Equal(expected, EncounterCategory.FormatName(modelId));
    }
}
