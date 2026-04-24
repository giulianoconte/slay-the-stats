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

/// <summary>
/// Shape classification of an event's option set, derived at aggregation
/// time from the recorded visits. Drives both how options are bucketed
/// and how the tooltip renders them.
/// </summary>
public enum EventShape
{
    /// <summary>Fewer than 2 visits, or a single terminal key with stable
    /// variables — can't distinguish Shape1 from Shape2. Default to
    /// Shape1-style bucketing but note the uncertainty to the user.</summary>
    Unknown,

    /// <summary>≥2 distinct terminal option keys observed — the game
    /// offers semantically distinct options each visit and run files
    /// preserve which was picked. Pick% computed against TotalVisits is
    /// meaningful. Most events land here.</summary>
    Shape1_DistinctKeys,

    /// <summary>All visits share the same terminal option key but one or
    /// more variables vary — the game uses a single loc key for N options
    /// with the distinction captured only in <c>variables</c> (e.g. Future
    /// of Potions: every option is "POTION" with Rarity = Common/Uncommon/
    /// Rare/Event). Run files never record options *offered*, only the one
    /// picked. Bucket by terminal + BucketVariable; tooltip shows raw
    /// Picks without a fraction and notes offered data is unavailable.</summary>
    Shape2_VariableKey,
}

/// <summary>
/// Read-time aggregate for one event under a filter. <see cref="Options"/>
/// holds per-option breakdowns; bucket key depends on <see cref="Shape"/>.
/// Totals sum across all matching visits regardless of option. Computed by
/// <c>StatsAggregator.AggregateEvent</c>.
/// </summary>
public class EventAggregate
{
    public string EventId = "";
    public int TotalVisits;
    public int TotalWins;
    public double WinRate => TotalVisits > 0 ? (double)TotalWins / TotalVisits : 0.0;
    public Dictionary<string, EventOptionAggregate> Options = new();

    /// <summary>Classification recomputed every aggregation — auto-corrects
    /// as more visits land. Drives both bucketing and tooltip rendering.</summary>
    public EventShape Shape = EventShape.Unknown;

    /// <summary>For <see cref="EventShape.Shape2_VariableKey"/>: the
    /// variable name whose value disambiguates otherwise-identical options.
    /// Null for other shapes.</summary>
    public string? BucketVariable;

    /// <summary>
    /// Bucket-key delimiter used to join terminal key + variable value when
    /// <see cref="Shape"/> is Shape2. Pipe chosen because it doesn't occur
    /// in event loc keys (dotted) or observed variable values (enum names
    /// and id strings like <c>POTION.FIRE_POTION</c>).
    /// </summary>
    public const char BucketKeySep = '|';

    /// <summary>
    /// Composes the bucket key for a (terminal, variables) pair per the
    /// aggregate's Shape. Shape2 appends the BucketVariable's value;
    /// everything else returns the terminal unchanged.
    /// </summary>
    public string BuildBucketKey(string terminal, IReadOnlyDictionary<string, string>? variables)
    {
        if (Shape != EventShape.Shape2_VariableKey || BucketVariable == null) return terminal;
        if (variables != null && variables.TryGetValue(BucketVariable, out var v) && !string.IsNullOrEmpty(v))
            return terminal + BucketKeySep + v;
        return terminal;
    }
}

/// <summary>
/// Per-option aggregate slice within an <see cref="EventAggregate"/>.
/// Delta lists carry the raw per-visit values so medians and means can be
/// computed on demand, matching the encounter-stats pattern (sum + list).
/// </summary>
public class EventOptionAggregate
{
    public string OptionKey = "";
    public int Picks;
    public int Wins;
    public List<int> HpDeltas    = new(); // hp_healed - damage_taken
    public List<int> MaxHpDeltas = new(); // max_hp_gained - max_hp_lost
    public List<int> GoldDeltas  = new(); // gold_gained - gold_spent - gold_stolen - gold_lost

    public double WinRate => Picks > 0 ? (double)Wins / Picks : 0.0;

    public double HpDeltaMean()     => Mean(HpDeltas);
    public double? HpDeltaMedian()  => Median(HpDeltas);
    public double MaxHpDeltaMean()  => Mean(MaxHpDeltas);
    public double? MaxHpDeltaMedian()  => Median(MaxHpDeltas);
    public double GoldDeltaMean()   => Mean(GoldDeltas);
    public double? GoldDeltaMedian() => Median(GoldDeltas);

    private static double Mean(List<int> xs) => xs.Count == 0 ? 0.0 : xs.Average();

    private static double? Median(List<int> xs)
    {
        if (xs.Count == 0) return null;
        var sorted = xs.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
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
        => optionPath.Count == 0 ? "" : TerminalOptionOfKey(optionPath[^1]);

    /// <summary>
    /// Extracts the <c>OPTION</c> segment from a single
    /// <c>EVENT_ID.pages.PAGE.options.OPTION.title</c> title key. Falls back
    /// to the raw key when it doesn't match the expected shape.
    /// </summary>
    public static string TerminalOptionOfKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        var parts = key.Split('.');
        var optionsIdx = Array.IndexOf(parts, "options");
        if (optionsIdx >= 0 && optionsIdx + 1 < parts.Length)
            return parts[optionsIdx + 1];
        return key;
    }
}
