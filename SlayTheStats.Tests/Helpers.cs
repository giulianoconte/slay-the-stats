namespace SlayTheStats.Tests;

/// <summary>
/// Builds minimal .run JSON for use in tests.
/// </summary>
public static class RunFixture
{
    public record CardChoice(string Id, bool Picked, int UpgradeLevel = 0);

    /// <summary>
    /// Each element of 'acts' is a list of reward screens for that act.
    /// Each reward screen is a list of card choices.
    /// </summary>
    public static string Build(
        bool won = false,
        bool abandoned = false,
        int ascension = 0,
        string character = "CHARACTER.IRONCLAD",
        List<List<List<CardChoice>>>? acts = null)
    {
        acts ??= [];

        var actsJson = string.Join(",", acts.Select(act =>
        {
            var floorsJson = string.Join(",", act.Select(rewardScreen =>
            {
                var choicesJson = string.Join(",", rewardScreen.Select(c =>
                {
                    var upgradeField = c.UpgradeLevel > 0 ? $@", ""current_upgrade_level"": {c.UpgradeLevel}" : "";
                    return $@"{{ ""card"": {{ ""id"": ""{c.Id}""{upgradeField} }}, ""was_picked"": {c.Picked.ToString().ToLower()} }}";
                }));
                return $@"{{ ""player_stats"": [{{ ""card_choices"": [{choicesJson}] }}] }}";
            }));
            return $"[{floorsJson}]";
        }));

        return $$"""
        {
            "was_abandoned": {{abandoned.ToString().ToLower()}},
            "win": {{won.ToString().ToLower()}},
            "ascension": {{ascension}},
            "players": [{ "character": "{{character}}" }],
            "map_point_history": [{{actsJson}}]
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
