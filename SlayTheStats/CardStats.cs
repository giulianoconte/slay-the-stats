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

    // Per-run counters (Pick% — fight rewards only)
    [JsonPropertyName("runs_offered")] public int RunsOffered { get; set; }
    [JsonPropertyName("runs_picked")] public int RunsPicked { get; set; }

    // Per-run counters (Win% — end-of-run deck presence, all acquisition sources)
    [JsonPropertyName("runs_present")] public int RunsPresent { get; set; }
    [JsonPropertyName("runs_won")]     public int RunsWon     { get; set; }

    // Per-run counters (Shop% — shop appearances and purchases)
    [JsonPropertyName("runs_shop_seen")]   public int RunsShopSeen   { get; set; }
    [JsonPropertyName("runs_shop_bought")] public int RunsShopBought { get; set; }
}

/// <summary>
/// Context key for a card stat entry: character|ascension|act|gameMode|buildVersion
/// e.g. "CHARACTER.IRONCLAD|0|1|standard|v0.98.0".
/// Acts are 1-indexed. Use RunContext.ToKey() to build and RunContext.Parse() to read.
/// </summary>
public readonly record struct RunContext(string Character, int Ascension, int Act, string GameMode, string BuildVersion)
{
    public string ToKey() => $"{Character}|{Ascension}|{Act}|{GameMode}|{BuildVersion}";

    public static RunContext Parse(string key)
    {
        var parts = key.Split('|');
        if (parts.Length < 5
            || !int.TryParse(parts[1], out var asc)
            || !int.TryParse(parts[2], out var act))
            return new RunContext("UNKNOWN", 0, 1, "UNKNOWN", "UNKNOWN");
        return new RunContext(parts[0], asc, act, parts[3], parts[4]);
    }
}

/// <summary>Total runs played and won for a character+gameMode combination.</summary>
public class CharacterStat
{
    [JsonPropertyName("runs")] public int Runs { get; set; }
    [JsonPropertyName("wins")] public int Wins { get; set; }
}

/// <summary>
/// Full stats database, keyed by card ID (e.g. "CARD.STRIKE_R") or "SKIP",
/// then by RunContext key (e.g. "CHARACTER.IRONCLAD|0|1").
/// </summary>
public class StatsDb
{
    public const string CurrentModVersion = "v0.1.4";

    [JsonPropertyName("mod_version")] public string ModVersion { get; set; } = CurrentModVersion;
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
    public static StatsDb Load(string path, Action<string>? warn = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var db = JsonSerializer.Deserialize<StatsDb>(json);
                if (db == null) return new StatsDb();

                if (db.ModVersion != CurrentModVersion)
                {
                    warn?.Invoke($"Mod version changed (file={db.ModVersion}, current={CurrentModVersion}). Reprocessing all runs.");
                    return new StatsDb();
                }

                return db;
            }
        }
        catch (Exception e)
        {
            warn?.Invoke($"Failed to load stats: {e.Message}");
        }
        return new StatsDb();
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
