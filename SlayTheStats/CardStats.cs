using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlayTheStats;

/// <summary>
/// Stats for a single card, tracked both per-instance and per-run.
/// Per-instance: counts every occurrence (e.g. 2 copies of a card in a winning deck = 2 wins).
/// Per-run: counts at most once per run regardless of copies.
/// </summary>
public class CardStat
{
    // Per-instance counters
    [JsonPropertyName("offered")] public int Offered { get; set; }
    [JsonPropertyName("picked")] public int Picked { get; set; }
    [JsonPropertyName("won")] public int Won { get; set; }

    // Per-run counters
    [JsonPropertyName("runs_offered")] public int RunsOffered { get; set; }
    [JsonPropertyName("runs_picked")] public int RunsPicked { get; set; }
    [JsonPropertyName("runs_won")] public int RunsWon { get; set; }
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
        return new RunContext(parts[0], int.Parse(parts[1]), int.Parse(parts[2]), parts[3], parts[4]);
    }
}

/// <summary>
/// Full stats database, keyed by card ID (e.g. "CARD.STRIKE_R") or "SKIP",
/// then by RunContext key (e.g. "CHARACTER.IRONCLAD|0|1").
/// </summary>
public class StatsDb
{
    public const string CurrentModVersion = "v0.0.8";

    [JsonPropertyName("mod_version")] public string ModVersion { get; set; } = CurrentModVersion;
    [JsonPropertyName("cards")] public Dictionary<string, Dictionary<string, CardStat>> Cards { get; set; } = new();
    [JsonPropertyName("processed_runs")] public HashSet<string> ProcessedRuns { get; set; } = new();

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
        ProcessedRuns.Clear();
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
