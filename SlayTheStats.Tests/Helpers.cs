namespace SlayTheStats.Tests;

/// <summary>
/// Builds minimal .run JSON for use in tests.
/// </summary>
public static class RunFixture
{
    public record CardChoice(string Id, bool Picked, int UpgradeLevel = 0);
    public record RelicChoice(string Id, bool Picked);
    public record DeckCard(string Id, int FloorAdded, int UpgradeLevel = 0);

    /// <summary>
    /// Each element of 'acts' is a list of reward screens for that act.
    /// Each reward screen is a list of card choices.
    /// 'relicActs' follows the same shape for relic choice screens.
    /// 'starterRelics' are placed in players[0].relics (the starter relic that has no floor event).
    /// 'deck' is placed in players[0].deck (final deck at end of run, used for presence tracking).
    /// </summary>
    public static string Build(
        bool won = false,
        bool abandoned = false,
        int ascension = 0,
        string character = "CHARACTER.IRONCLAD",
        string buildVersion = "UNKNOWN",
        string gameMode = "UNKNOWN",
        List<List<List<CardChoice>>>? acts = null,
        List<List<List<RelicChoice>>>? relicActs = null,
        List<string>? starterRelics = null,
        List<DeckCard>? deck = null)
    {
        acts ??= [];
        relicActs ??= [];

        int actCount = Math.Max(acts.Count, relicActs.Count);

        var actsJson = string.Join(",", Enumerable.Range(0, actCount).Select(actIdx =>
        {
            var floorJsons = new List<string>();

            if (actIdx < acts.Count)
            {
                foreach (var rewardScreen in acts[actIdx])
                {
                    var choicesJson = string.Join(",", rewardScreen.Select(c =>
                    {
                        var upgradeField = c.UpgradeLevel > 0 ? $@", ""current_upgrade_level"": {c.UpgradeLevel}" : "";
                        return $@"{{ ""card"": {{ ""id"": ""{c.Id}""{upgradeField} }}, ""was_picked"": {c.Picked.ToString().ToLower()} }}";
                    }));
                    floorJsons.Add($@"{{ ""map_point_type"": ""monster"", ""player_stats"": [{{ ""card_choices"": [{choicesJson}] }}] }}");
                }
            }

            if (actIdx < relicActs.Count)
            {
                foreach (var relicScreen in relicActs[actIdx])
                {
                    var choicesJson = string.Join(",", relicScreen.Select(r =>
                        $@"{{ ""choice"": ""{r.Id}"", ""was_picked"": {r.Picked.ToString().ToLower()} }}"));
                    floorJsons.Add($@"{{ ""player_stats"": [{{ ""relic_choices"": [{choicesJson}] }}] }}");
                }
            }

            return $"[{string.Join(",", floorJsons)}]";
        }));

        var buildIdField = buildVersion != "UNKNOWN" ? $@"""build_id"": ""{buildVersion}"", " : "";
        var gameModeField = gameMode != "UNKNOWN" ? $@"""game_mode"": ""{gameMode}"", " : "";
        var relicsField = starterRelics is { Count: > 0 }
            ? $@", ""relics"": [{string.Join(",", starterRelics.Select(r => $@"{{""id"":""{r}""}}") )}]"
            : "";
        var deckField = deck is { Count: > 0 }
            ? $@", ""deck"": [{string.Join(",", deck.Select(c =>
            {
                var upgrade = c.UpgradeLevel > 0 ? $@", ""current_upgrade_level"": {c.UpgradeLevel}" : "";
                return $@"{{""id"":""{c.Id}"", ""floor_added_to_deck"": {c.FloorAdded}{upgrade}}}";
            }))}]"
            : "";

        return $$"""
        {
            "was_abandoned": {{abandoned.ToString().ToLower()}},
            "win": {{won.ToString().ToLower()}},
            "ascension": {{ascension}},
            {{buildIdField}}{{gameModeField}}"players": [{ "character": "{{character}}"{{relicsField}}{{deckField}} }],
            "map_point_history": [{{actsJson}}]
        }
        """;
    }

    /// <summary>
    /// Represents a combat encounter on a floor for test fixture building.
    /// </summary>
    public record EncounterFloor(
        string ModelId,
        List<string> MonsterIds,
        int TurnsTaken,
        int DamageTaken,
        int CurrentHp,
        int MaxHp,
        int HpHealed = 0,
        List<string>? PotionsUsed = null,
        string MapPointType = "monster");

    /// <summary>
    /// Builds a .run JSON with encounter data for testing encounter parsing.
    /// Each act is a list of floors; each floor may be an EncounterFloor (combat) or null (non-combat with hp tracking).
    /// </summary>
    public static string BuildWithEncounters(
        bool won = false,
        bool abandoned = false,
        int ascension = 0,
        string character = "CHARACTER.IRONCLAD",
        string buildVersion = "UNKNOWN",
        string gameMode = "UNKNOWN",
        string killedByEncounter = "NONE.NONE",
        List<string>? actsArray = null,
        List<List<EncounterFloor>>? encounterActs = null)
    {
        actsArray ??= ["ACT.OVERGROWTH", "ACT.HIVE", "ACT.GLORY"];
        encounterActs ??= [];

        var actsJsonParts = new List<string>();
        for (int actIdx = 0; actIdx < encounterActs.Count; actIdx++)
        {
            var floorJsons = new List<string>();
            foreach (var enc in encounterActs[actIdx])
            {
                var monsterIdsJson = string.Join(",", enc.MonsterIds.Select(m => $@"""{m}"""));
                var potionUsedField = enc.PotionsUsed is { Count: > 0 }
                    ? $@", ""potion_used"": [{string.Join(",", enc.PotionsUsed.Select(p => $@"""{p}"""))}]"
                    : "";
                floorJsons.Add($$"""
                {
                    "map_point_type": "{{enc.MapPointType}}",
                    "rooms": [{ "model_id": "{{enc.ModelId}}", "monster_ids": [{{monsterIdsJson}}], "room_type": "monster", "turns_taken": {{enc.TurnsTaken}} }],
                    "player_stats": [{ "damage_taken": {{enc.DamageTaken}}, "current_hp": {{enc.CurrentHp}}, "max_hp": {{enc.MaxHp}}, "hp_healed": {{enc.HpHealed}}{{potionUsedField}} }]
                }
                """);
            }
            actsJsonParts.Add($"[{string.Join(",", floorJsons)}]");
        }

        // Pad remaining acts from actsArray with empty floors
        for (int i = actsJsonParts.Count; i < actsArray.Count; i++)
            actsJsonParts.Add("[]");

        var actsArrayJson = string.Join(",", actsArray.Select(a => $@"""{a}"""));
        var buildIdField = buildVersion != "UNKNOWN" ? $@"""build_id"": ""{buildVersion}"", " : "";
        var gameModeField = gameMode != "UNKNOWN" ? $@"""game_mode"": ""{gameMode}"", " : "";

        return $$"""
        {
            "was_abandoned": {{abandoned.ToString().ToLower()}},
            "win": {{won.ToString().ToLower()}},
            "ascension": {{ascension}},
            {{buildIdField}}{{gameModeField}}"players": [{ "character": "{{character}}" }],
            "killed_by_encounter": "{{killedByEncounter}}",
            "acts": [{{actsArrayJson}}],
            "map_point_history": [{{string.Join(",", actsJsonParts)}}]
        }
        """;
    }

    /// <summary>
    /// Builds a .run JSON for an event encounter (2 rooms: event room + combat room).
    /// </summary>
    public static string BuildEventEncounterFloorJson(
        string eventModelId,
        string encounterModelId,
        List<string> monsterIds,
        int turnsTaken,
        int damageTaken,
        int currentHp,
        int maxHp,
        int hpHealed = 0)
    {
        var monsterIdsJson = string.Join(",", monsterIds.Select(m => $@"""{m}"""));
        return $$"""
        {
            "map_point_type": "unknown",
            "rooms": [
                { "model_id": "{{eventModelId}}", "room_type": "event", "turns_taken": 0 },
                { "model_id": "{{encounterModelId}}", "monster_ids": [{{monsterIdsJson}}], "room_type": "monster", "turns_taken": {{turnsTaken}} }
            ],
            "player_stats": [{ "damage_taken": {{damageTaken}}, "current_hp": {{currentHp}}, "max_hp": {{maxHp}}, "hp_healed": {{hpHealed}} }]
        }
        """;
    }

    public static string WriteTempFile(string json)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        return path;
    }
}
