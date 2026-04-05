using Xunit;
using SlayTheStats;
using static SlayTheStats.Tests.RunFixture;

namespace SlayTheStats.Tests;

public class RunParserRelicTests : IDisposable
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

    // --- Basic presence ---

    [Fact]
    public void RelicPicked_RecordsPresence()
    {
        var db = new StatsDb();
        var path = TempRun(Build(relicActs:
        [[
            [new("RELIC.BURNING_BLOOD", Picked: true)]
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var stat = db.Relics["RELIC.BURNING_BLOOD"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN|default"];
        Assert.Equal(1, stat.RunsPresent);
    }

    [Fact]
    public void RelicNotPicked_NotRecorded()
    {
        var db = new StatsDb();
        var path = TempRun(Build(relicActs:
        [[
            [new("RELIC.BURNING_BLOOD", Picked: false)]
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.False(db.Relics.ContainsKey("RELIC.BURNING_BLOOD"));
    }

    // --- Win/loss attribution ---

    [Fact]
    public void WinningRun_IncrementsRelicRunsWon()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: true, relicActs:
        [[
            [new("RELIC.BURNING_BLOOD", Picked: true)]
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var stat = db.Relics["RELIC.BURNING_BLOOD"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN|default"];
        Assert.Equal(1, stat.RunsWon);
    }

    [Fact]
    public void LosingRun_DoesNotIncrementRelicRunsWon()
    {
        var db = new StatsDb();
        var path = TempRun(Build(won: false, relicActs:
        [[
            [new("RELIC.BURNING_BLOOD", Picked: true)]
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var stat = db.Relics["RELIC.BURNING_BLOOD"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN|default"];
        Assert.Equal(0, stat.RunsWon);
    }

    // --- Abandoned runs ---

    [Fact]
    public void AbandonedRun_NoRelicsRecorded()
    {
        var db = new StatsDb();
        var path = TempRun(Build(abandoned: true, relicActs:
        [[
            [new("RELIC.BURNING_BLOOD", Picked: true)]
        ]]));

        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.Empty(db.Relics);
    }

    // --- Act context ---

    [Fact]
    public void RelicInAct2_GetsAct2Context()
    {
        var db = new StatsDb();
        var path = TempRun(Build(relicActs:
        [
            [],  // act 1 — no relics
            [[new("RELIC.VAJRA", Picked: true)]]
        ]));

        RunParser.ProcessRun(path, "run1", "default", db);

        Assert.True(db.Relics["RELIC.VAJRA"].ContainsKey("CHARACTER.IRONCLAD|0|2|UNKNOWN|UNKNOWN|default"));
        Assert.False(db.Relics["RELIC.VAJRA"].ContainsKey("CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN|default"));
    }

    // --- Starter relic ---

    [Fact]
    public void StarterRelic_AssignedToAct1()
    {
        var db = new StatsDb();
        var path = TempRun(Build(starterRelics: ["RELIC.BURNING_BLOOD"]));

        RunParser.ProcessRun(path, "run1", "default", db);

        var stat = db.Relics["RELIC.BURNING_BLOOD"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN|default"];
        Assert.Equal(1, stat.RunsPresent);
    }

    [Fact]
    public void RelicInChoicesAndStarterList_NotDoubleCountedAsStarter()
    {
        var db = new StatsDb();
        // Simulates a real run: relic appears in both relic_choices and players[0].relics
        var path = TempRun(Build(
            relicActs: [[[new("RELIC.BURNING_BLOOD", Picked: true)]]],
            starterRelics: ["RELIC.BURNING_BLOOD"]
        ));

        RunParser.ProcessRun(path, "run1", "default", db);

        var stat = db.Relics["RELIC.BURNING_BLOOD"]["CHARACTER.IRONCLAD|0|1|UNKNOWN|UNKNOWN|default"];
        Assert.Equal(1, stat.RunsPresent);
    }
}
