using System.Text.Json.Serialization;

namespace SlayTheStats;

/// <summary>
/// One record per event visit per run. Unlike card/relic/encounter stats
/// (pre-aggregated into counter maps keyed by context), event visits are
/// stored raw so aggregation can slice by option_path and variable buckets
/// — dimensions too open-ended to pre-key. Volume is small: ~5–10
/// non-ancient events per run × run count.
/// </summary>
public class EventVisit
{
    // Context fields embedded per-visit (no outer context-keyed map).
    [JsonPropertyName("character")]     public string Character    { get; set; } = "";
    [JsonPropertyName("ascension")]     public int    Ascension    { get; set; }
    [JsonPropertyName("act")]           public int    Act          { get; set; }
    [JsonPropertyName("game_mode")]     public string GameMode     { get; set; } = "";
    [JsonPropertyName("build_version")] public string BuildVersion { get; set; } = "";
    [JsonPropertyName("profile")]       public string Profile      { get; set; } = "default";
    [JsonPropertyName("won")]           public bool   Won          { get; set; }
    [JsonPropertyName("run_id")]        public string RunId        { get; set; } = "";

    /// <summary>
    /// Ordered list of <c>event_choices[].title.key</c> values — the full
    /// path the player took through the event. Multi-page events (e.g.
    /// The Lantern Key: INITIAL → KEEP_THE_KEY → FIGHT) produce 2+ entries.
    /// Terminal option is the last entry; aggregation keys on that unless
    /// the full path is needed.
    /// </summary>
    [JsonPropertyName("option_path")] public List<string> OptionPath { get; set; } = new();

    /// <summary>
    /// Flattened <c>event_choices[].variables</c> — randomized outcomes
    /// like the specific Potion/Relic/Curse offered. Keyed by variable
    /// name; value is the most-representative string/decimal for display.
    /// </summary>
    [JsonPropertyName("variables")] public Dictionary<string, string> Variables { get; set; } = new();

    // Floor deltas — read from the event floor's player_stats entry.
    [JsonPropertyName("damage_taken")]  public int DamageTaken  { get; set; }
    [JsonPropertyName("hp_healed")]     public int HpHealed     { get; set; }
    [JsonPropertyName("max_hp_gained")] public int MaxHpGained  { get; set; }
    [JsonPropertyName("max_hp_lost")]   public int MaxHpLost    { get; set; }
    [JsonPropertyName("gold_gained")]   public int GoldGained   { get; set; }
    [JsonPropertyName("gold_lost")]     public int GoldLost     { get; set; }
    [JsonPropertyName("gold_spent")]    public int GoldSpent    { get; set; }
    [JsonPropertyName("gold_stolen")]   public int GoldStolen   { get; set; }

    // Linked acquisitions on this floor.
    [JsonPropertyName("cards_gained")]      public List<string>        CardsGained      { get; set; } = new();
    [JsonPropertyName("cards_removed")]     public List<string>        CardsRemoved     { get; set; } = new();
    [JsonPropertyName("cards_upgraded")]    public List<string>        CardsUpgraded    { get; set; } = new();
    [JsonPropertyName("cards_transformed")] public List<CardTransform> CardsTransformed { get; set; } = new();
    [JsonPropertyName("relics_gained")]     public List<string>        RelicsGained     { get; set; } = new();
    [JsonPropertyName("potions_chosen")]    public List<string>        PotionsChosen    { get; set; } = new();

    /// <summary>
    /// Non-null when the event spawned a follow-up combat (e.g.
    /// The Lantern Key → Mysterious Knight). References an
    /// <see cref="EncounterEvent"/> by id — damage/turns live there to
    /// avoid double-counting against event deltas.
    /// </summary>
    [JsonPropertyName("spawned_encounter")] public string? SpawnedEncounterId { get; set; }
}

public class CardTransform
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")]   public string To   { get; set; } = "";
}

/// <summary>
/// Metadata about an event type, keyed by event ID (e.g. "EVENT.DROWNING_BEACON").
/// Populated lazily during run parsing.
/// </summary>
public class EventMeta
{
    [JsonPropertyName("act")]   public int    Act   { get; set; }
    [JsonPropertyName("biome")] public string Biome { get; set; } = "";

    /// <summary>
    /// Union of terminal option-path entries observed across all parsed
    /// runs. Used as the pick-rate denominator in the in-run tooltip until
    /// authoritative option lists from decomp are wired in — see
    /// slay-the-stats.md §Event tracking open questions.
    /// </summary>
    [JsonPropertyName("observed_options")] public HashSet<string> ObservedOptions { get; set; } = new();

    /// <summary>Union of variable names observed across all parsed runs.</summary>
    [JsonPropertyName("observed_variables")] public HashSet<string> ObservedVariables { get; set; } = new();
}

/// <summary>
/// Event ids whose options are all relic rewards — already covered by
/// <c>RelicTooltip</c> + the ancient-option character-context fix from
/// v0.2.0. The parser skips these entirely to avoid duplicate tracking.
/// Source: classes deriving from <c>AncientEventModel</c> in
/// <c>MegaCrit.Sts2.Core.Models.Events/</c>. Revisit if future ancient
/// events add non-relic options.
/// </summary>
public static class AncientEvents
{
    public static readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        "EVENT.NEOW",
        "EVENT.DARV",
        "EVENT.NONUPEIPE",
        "EVENT.OROBAS",
        "EVENT.PAEL",
        "EVENT.TANX",
        "EVENT.TEZCATARA",
        "EVENT.VAKUU",
    };

    public static bool IsAncient(string eventId) => Ids.Contains(eventId);
}

public static class EventIdHelpers
{
    /// <summary>
    /// Strips the <c>EVENT.</c> prefix and title-cases underscores.
    /// e.g. "EVENT.DROWNING_BEACON" -> "Drowning Beacon".
    /// </summary>
    public static string FormatName(string eventId)
    {
        var name = eventId.StartsWith("EVENT.") ? eventId["EVENT.".Length..] : eventId;
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpper(words[i][0]) + words[i][1..].ToLower();
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// Extracts the terminal option name from the last entry of an option
    /// path. Title keys are shaped as
    /// <c>EVENT_ID.pages.PAGE.options.OPTION.title</c> — this returns
    /// <c>OPTION</c>. Falls back to the full key if parsing fails.
    /// </summary>
    public static string TerminalOption(List<string> optionPath)
    {
        if (optionPath.Count == 0) return "";
        var key = optionPath[^1];
        // <EVENT>.pages.<PAGE>.options.<OPTION>.title
        var parts = key.Split('.');
        var optionsIdx = Array.IndexOf(parts, "options");
        if (optionsIdx >= 0 && optionsIdx + 1 < parts.Length)
            return parts[optionsIdx + 1];
        return key;
    }
}
