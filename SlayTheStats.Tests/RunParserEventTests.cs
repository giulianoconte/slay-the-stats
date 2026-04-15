using Xunit;
using SlayTheStats;
using static SlayTheStats.Tests.RunFixture;

namespace SlayTheStats.Tests;

public class RunParserEventTests : IDisposable
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

    /// <summary>
    /// Single-floor run with one event. Keeps JSON flat so individual tests
    /// can tweak floor content. Non-event floors are padded with empty acts
    /// to keep the acts[] / map_point_history[] shapes valid.
    /// </summary>
    private static string BuildWithEventFloor(string floorJson, bool won = false)
    {
        return $$"""
        {
            "was_abandoned": false,
            "win": {{won.ToString().ToLower()}},
            "ascension": 0,
            "players": [{ "character": "CHARACTER.IRONCLAD" }],
            "killed_by_encounter": "NONE.NONE",
            "acts": ["ACT.OVERGROWTH", "ACT.HIVE", "ACT.GLORY"],
            "map_point_history": [[{{floorJson}}], [], []]
        }
        """;
    }

    [Fact]
    public void SingleEvent_RecordsVisitWithOptionPath()
    {
        var floor = """
        {
            "map_point_type": "unknown",
            "rooms": [{ "model_id": "EVENT.DROWNING_BEACON", "room_type": "event", "turns_taken": 0 }],
            "player_stats": [{
                "damage_taken": 0,
                "hp_healed": 0,
                "max_hp_lost": 13,
                "gold_gained": 0,
                "event_choices": [
                    { "title": { "key": "DROWNING_BEACON.pages.INITIAL.options.CLIMB.title" } }
                ],
                "relic_choices": [
                    { "choice": "RELIC.FRESNEL_LENS", "was_picked": true }
                ]
            }]
        }
        """;

        var db = new StatsDb();
        RunParser.ProcessRun(TempRun(BuildWithEventFloor(floor)), "run1", "default", db);

        Assert.True(db.EventVisits.ContainsKey("EVENT.DROWNING_BEACON"));
        var visits = db.EventVisits["EVENT.DROWNING_BEACON"];
        Assert.Single(visits);

        var v = visits[0];
        Assert.Equal("CHARACTER.IRONCLAD", v.Character);
        Assert.Equal(1, v.Act);
        Assert.Single(v.OptionPath);
        Assert.Equal("DROWNING_BEACON.pages.INITIAL.options.CLIMB.title", v.OptionPath[0]);
        Assert.Equal(13, v.MaxHpLost);
        Assert.Single(v.RelicsGained);
        Assert.Equal("RELIC.FRESNEL_LENS", v.RelicsGained[0]);
    }

    [Fact]
    public void MultiPageEvent_RecordsAllOptionSteps()
    {
        // The Lantern Key: INITIAL -> KEEP_THE_KEY -> FIGHT
        var floor = """
        {
            "map_point_type": "unknown",
            "rooms": [
                { "model_id": "EVENT.THE_LANTERN_KEY", "room_type": "event", "turns_taken": 0 },
                { "model_id": "ENCOUNTER.MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER", "monster_ids": ["MONSTER.MYSTERIOUS_KNIGHT"], "room_type": "monster", "turns_taken": 3 }
            ],
            "player_stats": [{
                "damage_taken": 11,
                "current_hp": 27,
                "max_hp": 74,
                "hp_healed": 6,
                "event_choices": [
                    { "title": { "key": "THE_LANTERN_KEY.pages.INITIAL.options.KEEP_THE_KEY.title" } },
                    { "title": { "key": "THE_LANTERN_KEY.pages.KEEP_THE_KEY.options.FIGHT.title" } }
                ],
                "cards_gained": [{ "id": "CARD.LANTERN_KEY" }]
            }]
        }
        """;

        var db = new StatsDb();
        RunParser.ProcessRun(TempRun(BuildWithEventFloor(floor)), "run1", "default", db);

        var v = db.EventVisits["EVENT.THE_LANTERN_KEY"][0];
        Assert.Equal(2, v.OptionPath.Count);
        Assert.Equal("FIGHT", EventIdHelpers.TerminalOption(v.OptionPath));
        Assert.Equal("ENCOUNTER.MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER", v.SpawnedEncounterId);
        Assert.Contains("CARD.LANTERN_KEY", v.CardsGained);
    }

    [Fact]
    public void EventVariables_AreFlattenedIntoVisitAndMeta()
    {
        var floor = """
        {
            "map_point_type": "unknown",
            "rooms": [{ "model_id": "EVENT.DROWNING_BEACON", "room_type": "event", "turns_taken": 0 }],
            "player_stats": [{
                "event_choices": [
                    {
                        "title": { "key": "DROWNING_BEACON.pages.INITIAL.options.CLIMB.title" },
                        "variables": {
                            "Potion": { "type": "DynamicString", "string_value": "Glowwater Potion" },
                            "Relic":  { "type": "DynamicString", "string_value": "Fresnel Lens" }
                        }
                    }
                ]
            }]
        }
        """;

        var db = new StatsDb();
        RunParser.ProcessRun(TempRun(BuildWithEventFloor(floor)), "run1", "default", db);

        var v = db.EventVisits["EVENT.DROWNING_BEACON"][0];
        Assert.Equal("Glowwater Potion", v.Variables["Potion"]);
        Assert.Equal("Fresnel Lens",     v.Variables["Relic"]);

        var meta = db.EventMeta["EVENT.DROWNING_BEACON"];
        Assert.Contains("Potion", meta.ObservedVariables);
        Assert.Contains("Relic",  meta.ObservedVariables);
        Assert.Contains("CLIMB",  meta.ObservedOptions);
    }

    [Fact]
    public void AncientEvents_AreSkipped()
    {
        var floor = """
        {
            "map_point_type": "unknown",
            "rooms": [{ "model_id": "EVENT.NEOW", "room_type": "event", "turns_taken": 0 }],
            "player_stats": [{
                "event_choices": [{ "title": { "key": "NEW_LEAF.title", "table": "relics" } }],
                "relic_choices": [{ "choice": "RELIC.NEW_LEAF", "was_picked": true }]
            }]
        }
        """;

        var db = new StatsDb();
        RunParser.ProcessRun(TempRun(BuildWithEventFloor(floor)), "run1", "default", db);

        // Neow is ancient — no EventVisit record. The relic is still tracked via
        // the existing relic pipeline (verified implicitly: no Events state means
        // the exclusion fired).
        Assert.False(db.EventVisits.ContainsKey("EVENT.NEOW"));
        Assert.False(db.EventMeta.ContainsKey("EVENT.NEOW"));
    }

    [Fact]
    public void CardsTransformed_RecordsFromAndTo()
    {
        var floor = """
        {
            "map_point_type": "unknown",
            "rooms": [{ "model_id": "EVENT.BYRDONIS_NEST", "room_type": "event", "turns_taken": 0 }],
            "player_stats": [{
                "cards_transformed": [{
                    "final_card":    { "id": "CARD.BYRD_SWOOP" },
                    "original_card": { "id": "CARD.BYRDONIS_EGG" }
                }],
                "event_choices": [{ "title": { "key": "BYRDONIS_NEST.pages.INITIAL.options.HATCH.title" } }]
            }]
        }
        """;

        var db = new StatsDb();
        RunParser.ProcessRun(TempRun(BuildWithEventFloor(floor)), "run1", "default", db);

        var v = db.EventVisits["EVENT.BYRDONIS_NEST"][0];
        Assert.Single(v.CardsTransformed);
        Assert.Equal("CARD.BYRDONIS_EGG", v.CardsTransformed[0].From);
        Assert.Equal("CARD.BYRD_SWOOP",   v.CardsTransformed[0].To);
    }

    [Fact]
    public void WonStamp_IsAppliedAfterFloorWalk()
    {
        var floor = """
        {
            "map_point_type": "unknown",
            "rooms": [{ "model_id": "EVENT.DROWNING_BEACON", "room_type": "event", "turns_taken": 0 }],
            "player_stats": [{
                "event_choices": [{ "title": { "key": "DROWNING_BEACON.pages.INITIAL.options.CLIMB.title" } }]
            }]
        }
        """;

        var db = new StatsDb();
        RunParser.ProcessRun(TempRun(BuildWithEventFloor(floor, won: true)), "run1", "default", db);

        Assert.True(db.EventVisits["EVENT.DROWNING_BEACON"][0].Won);
    }

    [Fact]
    public void EventMeta_BiomeFromActsArray()
    {
        var floor = """
        {
            "map_point_type": "unknown",
            "rooms": [{ "model_id": "EVENT.DROWNING_BEACON", "room_type": "event", "turns_taken": 0 }],
            "player_stats": [{
                "event_choices": [{ "title": { "key": "DROWNING_BEACON.pages.INITIAL.options.CLIMB.title" } }]
            }]
        }
        """;

        var db = new StatsDb();
        RunParser.ProcessRun(TempRun(BuildWithEventFloor(floor)), "run1", "default", db);

        var meta = db.EventMeta["EVENT.DROWNING_BEACON"];
        Assert.Equal(1, meta.Act);
        Assert.Equal("ACT.OVERGROWTH", meta.Biome);
    }
}
