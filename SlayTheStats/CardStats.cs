using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlayTheStats;

/// <summary>
/// Per-run membership flags for a single card in a single context. OR'd into a
/// run's entry so a run that, e.g., was offered the card, picked it, and ended
/// with it in the deck is one entry with three bits. <c>Won</c> is set only when
/// the card was present AND the run was won, so it needs no separate won-set.
/// </summary>
[Flags]
public enum RunFlag : byte
{
    None       = 0,
    Present    = 1,
    Offered    = 2,
    Picked     = 4,
    ShopSeen   = 8,
    ShopBought = 16,
    Won        = 32,
}

/// <summary>
/// Stats for a single card in a single context. Per-instance counters
/// (Offered/Picked/Won) count every occurrence and sum across contexts. The
/// run-level counts (RunsPresent/RunsWon/RunsOffered/RunsPicked/RunsShopSeen/
/// RunsShopBought) are NOT stored as ints — they derive from <see cref="RunFlags"/>,
/// a set of contributing run indices with per-run flags. Deriving from a run-id
/// set is what makes unions across acts (the Total row) and across upgrade
/// variants (grouping) count each run once, regardless of act count. See #6;
/// supersedes the #5 overlap term.
/// </summary>
public class CardStat
{
    // Per-instance counters (occurrence counts — every copy; sum across contexts).
    [JsonPropertyName("offered")] public int Offered { get; set; }
    [JsonPropertyName("picked")] public int Picked { get; set; }
    [JsonPropertyName("won")] public int Won { get; set; }

    /// <summary>
    /// Contributing run index (see <see cref="StatsDb.RunIndex"/>) → OR'd
    /// <see cref="RunFlag"/> bits. The single source of truth for every
    /// Runs* count below.
    /// </summary>
    [JsonPropertyName("runs")] public Dictionary<int, byte> RunFlags { get; set; } = new();

    // Verbose acquisition breakdown — fight rewards (pre-upgraded cards only)
    [JsonPropertyName("runs_offered_upgraded")] public int RunsOfferedUpgraded { get; set; }
    [JsonPropertyName("runs_picked_upgraded")]  public int RunsPickedUpgraded  { get; set; }

    // Verbose acquisition breakdown — shop (pre-upgraded cards only)
    [JsonPropertyName("runs_shop_seen_upgraded")]   public int RunsShopSeenUpgraded   { get; set; }
    [JsonPropertyName("runs_shop_bought_upgraded")] public int RunsShopBoughtUpgraded { get; set; }

    // Verbose acquisition breakdown — upgrades from non-reward sources
    [JsonPropertyName("campfire_upgrades")]    public int CampfireUpgrades    { get; set; }
    [JsonPropertyName("event_relic_upgrades")] public int EventRelicUpgrades  { get; set; }

    // --- Derived run-level counts (not serialized; counted from RunFlags) ---
    [JsonIgnore] public int RunsPresent    => CountFlag(RunFlag.Present);
    [JsonIgnore] public int RunsOffered    => CountFlag(RunFlag.Offered);
    [JsonIgnore] public int RunsPicked     => CountFlag(RunFlag.Picked);
    [JsonIgnore] public int RunsShopSeen   => CountFlag(RunFlag.ShopSeen);
    [JsonIgnore] public int RunsShopBought => CountFlag(RunFlag.ShopBought);
    [JsonIgnore] public int RunsWon        => CountFlag(RunFlag.Won);

    private int CountFlag(RunFlag flag)
    {
        byte bit = (byte)flag;
        int n = 0;
        foreach (var v in RunFlags.Values)
            if ((v & bit) != 0) n++;
        return n;
    }

    /// <summary>Records that run <paramref name="runIndex"/> had these flags for this card+context.</summary>
    public void SetRun(int runIndex, RunFlag flags)
    {
        RunFlags.TryGetValue(runIndex, out var cur);
        RunFlags[runIndex] = (byte)(cur | (byte)flags);
    }

    /// <summary>
    /// Merges another stat into this one: per-instance and verbose ints sum;
    /// run flags UNION (OR per run index). Used to aggregate across contexts
    /// (acts → per-act / Total) and across upgrade variants (grouping). Because
    /// run flags union by index, a run appearing in both operands is counted
    /// once — this is the #6 fix and the replacement for #5's overlap term.
    /// </summary>
    public void MergeFrom(CardStat other)
    {
        Offered += other.Offered;
        Picked  += other.Picked;
        Won     += other.Won;
        RunsOfferedUpgraded    += other.RunsOfferedUpgraded;
        RunsPickedUpgraded     += other.RunsPickedUpgraded;
        RunsShopSeenUpgraded   += other.RunsShopSeenUpgraded;
        RunsShopBoughtUpgraded += other.RunsShopBoughtUpgraded;
        CampfireUpgrades       += other.CampfireUpgrades;
        EventRelicUpgrades     += other.EventRelicUpgrades;
        foreach (var (runIndex, flags) in other.RunFlags)
            SetRun(runIndex, (RunFlag)flags);
    }
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
    public const int CurrentSchemaVersion = 9; // bumped from 8: run-id model — CardStat.RunFlags + RunIndex; removed CardsGroupOverlap (#6)

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
    /// <summary>
    /// Interned run identities: ordered list of processed run ids. Card RunFlags
    /// (and, after the relic pass, relic run-sets) reference a run by its index
    /// here instead of repeating the id string. Rebuilt-consistent on every full
    /// reprocess. See #6.
    /// </summary>
    [JsonPropertyName("run_index")] public List<string> RunIndex { get; set; } = new();
    [JsonPropertyName("relics")]     public Dictionary<string, Dictionary<string, RelicStat>> Relics     { get; set; } = new();
    [JsonPropertyName("characters")] public Dictionary<string, CharacterStat>                 Characters { get; set; } = new();
    [JsonPropertyName("processed_runs")] public HashSet<string> ProcessedRuns { get; set; } = new();
    [JsonPropertyName("total_reward_screens")] public int TotalRewardScreens { get; set; }
    [JsonPropertyName("total_skips")]          public int TotalSkips          { get; set; }
    /// <summary>
    /// Highest ascension won per character (e.g. "CHARACTER.IRONCLAD" → 9).
    /// Populated by RunParser; currently unread since the OnlyHighestWonAscension
    /// option was removed. Retained in the schema for now (see GetHighestWonAscension).
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
        RunIndex.Clear();
        _runLookup = null;
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

    /// <summary>
    /// Interns a run id to its index in <see cref="RunIndex"/>, appending it if
    /// new. The reverse lookup is rebuilt lazily (e.g. after a load) from the
    /// persisted list. See #6.
    /// </summary>
    public int GetOrAddRunIndex(string runId)
    {
        if (_runLookup == null)
        {
            _runLookup = new Dictionary<string, int>(RunIndex.Count);
            for (int i = 0; i < RunIndex.Count; i++)
                _runLookup[RunIndex[i]] = i;
        }

        if (_runLookup.TryGetValue(runId, out var idx))
            return idx;

        idx = RunIndex.Count;
        RunIndex.Add(runId);
        _runLookup[runId] = idx;
        return idx;
    }

    [JsonIgnore] private Dictionary<string, int>? _runLookup;
}
