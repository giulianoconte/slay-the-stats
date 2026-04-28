using System.Text.Json;
using Xunit;
using SlayTheStats;

namespace SlayTheStats.Tests;

public class StatsDbTests
{
    [Fact]
    public void Load_ReturnsEmptyDb_WhenModVersionMismatch()
    {
        var path = Path.GetTempFileName();
        try
        {
            var old = new StatsDb { ModVersion = "v0.0.0" };
            old.Cards["CARD.TEST"] = new Dictionary<string, CardStat>
            {
                ["CHARACTER.IRONCLAD|0|1"] = new CardStat { Offered = 5, Picked = 2 }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(old));

            var loaded = StatsDb.Load(path);

            Assert.Empty(loaded.Cards);
            Assert.Equal(StatsDb.CurrentModVersion, loaded.ModVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReturnsExistingDb_WhenModVersionMatches()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Both ModVersion and SchemaVersion must match current values for
            // Load to return the existing db. SchemaVersion defaults to 0 on a
            // fresh `new StatsDb()` (intentional — old JSONs that predate the
            // field deserialize to 0 and trigger a reparse), so set it
            // explicitly here to simulate a db saved by the current build.
            var db = new StatsDb { SchemaVersion = StatsDb.CurrentSchemaVersion };
            db.Cards["CARD.TEST"] = new Dictionary<string, CardStat>
            {
                ["CHARACTER.IRONCLAD|0|1"] = new CardStat { Offered = 5, Picked = 2 }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(db));

            var loaded = StatsDb.Load(path);

            Assert.Single(loaded.Cards);
            Assert.Equal(2, loaded.Cards["CARD.TEST"]["CHARACTER.IRONCLAD|0|1"].Picked);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
