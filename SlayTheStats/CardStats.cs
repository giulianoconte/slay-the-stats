using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlayTheStats;

/// <summary>
/// Stats for a single card, tracked both per-instance and per-run.
/// Per-instance: counts every occurrence (e.g. 2 copies of a card in a winning deck = 2 wins).
/// Per-run: counts at most once per run regardless of copies.
/// RunsPresent/RunsWon use end-of-run deck presence (all acquisition sources).
/// RunsOffered/RunsPicked use fight-reward floor walk only (for Pick% stats).
/// </summary>
public class CardStat
{
    // Per-instance counters
    [JsonPropertyName("offered")] public int Offered { get; set; }
    [JsonPropertyName("picked")] public int Picked { get; set; }
    [JsonPropertyName("won")] public int Won { get; set; }

    // Experimental insights — per-event counters for within-offer (#1)
    // and skip-as-control (#2) techniques. See sts2-docs/slay-the-stats/insights.md.
    // PickedWon = picks where the run was won; OfferedWon = all offers in won runs.
    // OfferedSkipped = offers where the entire reward screen was skipped (no card taken);
    // OfferedSkippedWon = those that occurred in won runs.
    [JsonPropertyName("picked_won")]          public int PickedWon          { get; set; }
    [JsonPropertyName("offered_won")]         public int OfferedWon         { get; set; }
    [JsonPropertyName("offered_skipped")]     public int OfferedSkipped     { get; set; }
    [JsonPropertyName("offered_skipped_won")] public int OfferedSkippedWon  { get; set; }

    // Per-run counters tracking presence at *any point* during the run, not just
    // end-of-run final deck. Captures cards picked-then-removed (transforms,
    // event removals). Sums acquisition events (picks, shop buys, event gains,
    // transform targets) ∪ end-of-run deck. See insights.md.
    [JsonPropertyName("runs_ever_present")] public int RunsEverPresent { get; set; }
    [JsonPropertyName("runs_ever_won")]     public int RunsEverWon     { get; set; }

    // Per-run counters (Pick% — fight rewards only)
    [JsonPropertyName("runs_offered")] public int RunsOffered { get; set; }
    [JsonPropertyName("runs_picked")] public int RunsPicked { get; set; }

    // Per-run counters (Win% — end-of-run deck presence, all acquisition sources)
    [JsonPropertyName("runs_present")] public int RunsPresent { get; set; }
    [JsonPropertyName("runs_won")]     public int RunsWon     { get; set; }

    // Per-run counters (Shop% — shop appearances and purchases)
    [JsonPropertyName("runs_shop_seen")]   public int RunsShopSeen   { get; set; }
    [JsonPropertyName("runs_shop_bought")] public int RunsShopBought { get; set; }

    // Verbose acquisition breakdown — fight rewards (pre-upgraded cards only)
    [JsonPropertyName("runs_offered_upgraded")] public int RunsOfferedUpgraded { get; set; }
    [JsonPropertyName("runs_picked_upgraded")]  public int RunsPickedUpgraded  { get; set; }

    // Verbose acquisition breakdown — shop (pre-upgraded cards only)
    [JsonPropertyName("runs_shop_seen_upgraded")]   public int RunsShopSeenUpgraded   { get; set; }
    [JsonPropertyName("runs_shop_bought_upgraded")] public int RunsShopBoughtUpgraded { get; set; }

    // Verbose acquisition breakdown — upgrades from non-reward sources
    [JsonPropertyName("campfire_upgrades")]    public int CampfireUpgrades    { get; set; }
    [JsonPropertyName("event_relic_upgrades")] public int EventRelicUpgrades  { get; set; }
}

/// <summary>
/// Context key for a card stat entry: character|ascension|act|gameMode|buildVersion
/// e.g. "CHARACTER.IRONCLAD|0|1|standard|v0.98.0".
/// Acts are 1-indexed. Use RunContext.ToKey() to build and RunContext.Parse() to read.
/// </summary>
public readonly record struct RunContext(string Character, int Ascension, int Act, string GameMode, string BuildVersion, string Profile = "default")
{
    public string ToKey() => $"{Character}|{Ascension}|{Act}|{GameMode}|{BuildVersion}|{Profile}";

    public static RunContext Parse(string key)
    {
        var parts = key.Split('|');
        if (parts.Length < 5
            || !int.TryParse(parts[1], out var asc)
            || !int.TryParse(parts[2], out var act))
            return new RunContext("UNKNOWN", 0, 1, "UNKNOWN", "UNKNOWN", "default");
        var profile = parts.Length >= 6 ? parts[5] : "default";
        return new RunContext(parts[0], asc, act, parts[3], parts[4], profile);
    }
}

/// <summary>Total runs played and won for a character+gameMode combination.</summary>
public class CharacterStat
{
    [JsonPropertyName("runs")] public int Runs { get; set; }
    [JsonPropertyName("wins")] public int Wins { get; set; }
}

/// <summary>
/// Per-run summary appended once per processed run. Used for filter-aware
/// baselines (character WR / global WR) where the aggregated Characters
/// table loses the asc/version/profile context.
/// </summary>
public class RunSummary
{
    [JsonPropertyName("character")]     public string Character { get; set; } = "";
    [JsonPropertyName("ascension")]     public int Ascension { get; set; } = 0;
    [JsonPropertyName("game_mode")]     public string GameMode { get; set; } = "standard";
    [JsonPropertyName("build_version")] public string BuildVersion { get; set; } = "";
    [JsonPropertyName("profile")]       public string Profile { get; set; } = "default";
    [JsonPropertyName("won")]           public bool Won { get; set; } = false;
    [JsonPropertyName("reward_screens_offered")] public int RewardScreensOffered { get; set; } = 0;
    [JsonPropertyName("reward_screens_skipped")] public int RewardScreensSkipped { get; set; } = 0;
}

/// <summary>
/// Full stats database, keyed by card ID (e.g. "CARD.STRIKE_R") or "SKIP",
/// then by RunContext key (e.g. "CHARACTER.IRONCLAD|0|1").
/// </summary>
public class StatsDb
{
    public const string CurrentModVersion = "v1.0.0";

    /// <summary>
    /// Schema version — bumped whenever the db structure changes in a way
    /// that requires re-parsing all runs (e.g. adding DamageValues to
    /// EncounterEvent). Independent of the mod version so a data-model
    /// change within the same release still triggers a reparse. When the
    /// loaded db's schema version doesn't match, Load() returns a fresh db
    /// and all runs are re-processed.
    /// </summary>
    public const int CurrentSchemaVersion = 9; // bumped from 8: experimental RunsEverPresent / RunsEverWon (presence at any point during run)

    [JsonPropertyName("mod_version")] public string ModVersion { get; set; } = CurrentModVersion;
    /// <summary>
    /// Persisted schema version. Defaults to 0 (NOT CurrentSchemaVersion) so
    /// that old JSON files that predate this field deserialize as 0, which
    /// doesn't match CurrentSchemaVersion and triggers a reparse. New dbs
    /// created in-memory get CurrentSchemaVersion written by Save() because
    /// the Load() path sets it explicitly after validation passes.
    /// </summary>
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 0;
    [JsonPropertyName("cards")]      public Dictionary<string, Dictionary<string, CardStat>>  Cards      { get; set; } = new();
    [JsonPropertyName("relics")]     public Dictionary<string, Dictionary<string, RelicStat>> Relics     { get; set; } = new();
    [JsonPropertyName("characters")] public Dictionary<string, CharacterStat>                 Characters { get; set; } = new();
    [JsonPropertyName("processed_runs")] public HashSet<string> ProcessedRuns { get; set; } = new();
    [JsonPropertyName("total_reward_screens")] public int TotalRewardScreens { get; set; }
    [JsonPropertyName("total_skips")]          public int TotalSkips          { get; set; }
    /// <summary>
    /// Highest ascension won per character (e.g. "CHARACTER.IRONCLAD" → 9).
    /// Used by the OnlyHighestWonAscension config option to filter stats.
    /// </summary>
    [JsonPropertyName("highest_won_ascensions")] public Dictionary<string, int> HighestWonAscensions { get; set; } = new();
    [JsonPropertyName("encounters")]      public Dictionary<string, Dictionary<string, EncounterEvent>> Encounters     { get; set; } = new();
    [JsonPropertyName("encounter_meta")]  public Dictionary<string, EncounterMeta>                      EncounterMeta  { get; set; } = new();
    /// <summary>
    /// Per-visit event records, keyed by event ID (e.g. "EVENT.DROWNING_BEACON").
    /// Each list entry carries its own run context. Aggregation happens at read
    /// time — see slay-the-stats.md §Event tracking and
    /// game-state-diagram-v1.0.0.md. Ancient-event ids (<see cref="AncientEvents"/>)
    /// are excluded at parse time.
    /// </summary>
    [JsonPropertyName("event_visits")]    public Dictionary<string, List<EventVisit>>                   EventVisits    { get; set; } = new();
    [JsonPropertyName("event_meta")]      public Dictionary<string, EventMeta>                          EventMeta      { get; set; } = new();
    /// <summary>
    /// Starting max HP per character ID, derived from floor 0 of parsed runs:
    /// max_hp - max_hp_gained + max_hp_lost (i.e. before Neow relic bonuses).
    /// Used to normalize damage values into Dmg% for cross-character comparison.
    /// </summary>
    [JsonPropertyName("character_starting_hp")] public Dictionary<string, int> CharacterStartingHp { get; set; } = new();
    /// <summary>
    /// Per-run summaries appended at run-completion time. Used by
    /// <see cref="StatsAggregator.GetCharacterWR"/> / <see cref="StatsAggregator.GetGlobalWR"/>
    /// to produce filter-aware baselines (asc range / version range / profile)
    /// that the aggregated Characters table cannot express. Old DBs (schema &lt; 4)
    /// deserialize with an empty list — callers fall back to the Characters
    /// aggregate when Runs is empty.
    /// </summary>
    [JsonPropertyName("runs")] public List<RunSummary> Runs { get; set; } = new();
    public static StatsDb Load(string path, Action<string>? warn = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var db = JsonSerializer.Deserialize<StatsDb>(json);
                if (db == null) return new StatsDb { SchemaVersion = CurrentSchemaVersion };

                if (db.ModVersion != CurrentModVersion || db.SchemaVersion != CurrentSchemaVersion)
                {
                    warn?.Invoke($"Db schema changed (file mod={db.ModVersion} schema={db.SchemaVersion}, current mod={CurrentModVersion} schema={CurrentSchemaVersion}). Reprocessing all runs.");
                    return new StatsDb { SchemaVersion = CurrentSchemaVersion };
                }

                // Stamp the current schema so Save() writes the right version.
                db.SchemaVersion = CurrentSchemaVersion;

                // Re-apply category derivation against the current EncounterCategory rules so
                // that data stored under older classifications (e.g. OVERGROWTH_CRAWLERS as
                // "unknown") picks up new overrides without requiring a full reparse.
                foreach (var (encId, meta) in db.EncounterMeta)
                    meta.Category = EncounterCategory.Derive(encId);

                return db;
            }
        }
        catch (Exception e)
        {
            warn?.Invoke($"Failed to load stats: {e.Message}");
        }
        return new StatsDb { SchemaVersion = CurrentSchemaVersion };
    }

    public void Save(string path, Action<string>? warn = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(path, json);
        }
        catch (Exception e)
        {
            warn?.Invoke($"Failed to save stats: {e.Message}");
        }
    }

    public void Reset()
    {
        Cards.Clear();
        Relics.Clear();
        Characters.Clear();
        ProcessedRuns.Clear();
        TotalRewardScreens = 0;
        TotalSkips         = 0;
        HighestWonAscensions.Clear();
        Encounters.Clear();
        EncounterMeta.Clear();
        EventVisits.Clear();
        EventMeta.Clear();
        CharacterStartingHp.Clear();
        Runs.Clear();
    }

    public List<EventVisit> GetOrCreateEventVisits(string eventId)
    {
        if (!EventVisits.TryGetValue(eventId, out var list))
        {
            list = new List<EventVisit>();
            EventVisits[eventId] = list;
        }
        return list;
    }

    public EventMeta GetOrCreateEventMeta(string eventId)
    {
        if (!EventMeta.TryGetValue(eventId, out var meta))
        {
            meta = new EventMeta();
            EventMeta[eventId] = meta;
        }
        return meta;
    }

    public CharacterStat GetOrCreateCharacter(string character, string gameMode)
    {
        var key = $"{character}|{gameMode}";
        if (!Characters.TryGetValue(key, out var stat))
        {
            stat = new CharacterStat();
            Characters[key] = stat;
        }
        return stat;
    }

    public RelicStat GetOrCreateRelic(string relicId, RunContext context)
    {
        if (!Relics.TryGetValue(relicId, out var contextMap))
        {
            contextMap = new Dictionary<string, RelicStat>();
            Relics[relicId] = contextMap;
        }

        var key = context.ToKey();
        if (!contextMap.TryGetValue(key, out var stat))
        {
            stat = new RelicStat();
            contextMap[key] = stat;
        }
        return stat;
    }

    public EncounterEvent GetOrCreateEncounter(string encounterId, RunContext context)
    {
        if (!Encounters.TryGetValue(encounterId, out var contextMap))
        {
            contextMap = new Dictionary<string, EncounterEvent>();
            Encounters[encounterId] = contextMap;
        }

        var key = context.ToKey();
        if (!contextMap.TryGetValue(key, out var stat))
        {
            stat = new EncounterEvent();
            contextMap[key] = stat;
        }
        return stat;
    }

    public CardStat GetOrCreate(string cardId, RunContext context)
    {
        if (!Cards.TryGetValue(cardId, out var contextMap))
        {
            contextMap = new Dictionary<string, CardStat>();
            Cards[cardId] = contextMap;
        }

        var key = context.ToKey();
        if (!contextMap.TryGetValue(key, out var stat))
        {
            stat = new CardStat();
            contextMap[key] = stat;
        }
        return stat;
    }
}
