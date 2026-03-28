using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlayTheStats;

public static class RunParser
{
    /// <summary>
    /// Finds all unprocessed .run files in the history directory and processes them into the stats db.
    /// </summary>
    public const string SkipId = "SKIP";

    public static void ProcessNewRuns(StatsDb db)
    {
        // If the file was deleted externally, reset in-memory state and reprocess everything
        if (!File.Exists(MainFile.SavePath) && db.ProcessedRuns.Count > 0)
        {
            MainFile.Logger.Info("Stats file was deleted. Reprocessing all runs.");
            db.Reset();
        }

        var historyDirs = GetHistoryDirectories();
        if (historyDirs.Count == 0)
        {
            MainFile.Logger.Warn("Could not find any STS2 run history directories.");
            return;
        }

        int newCount = 0;

        foreach (var historyDir in historyDirs)
        {
            MainFile.Logger.Info($"Scanning history directory: {historyDir}");
            var runFiles = Directory.GetFiles(historyDir, "*.run");

            foreach (var path in runFiles)
            {
                var runId = Path.GetFileNameWithoutExtension(path);
                if (db.ProcessedRuns.Contains(runId))
                    continue;

                try
                {
                    ProcessRun(path, runId, db);
                    db.ProcessedRuns.Add(runId);
                    newCount++;
                }
                catch (Exception e)
                {
                    MainFile.Logger.Warn($"Failed to parse run {runId}: {e.Message}");
                }
            }
        }

        MainFile.Logger.Info($"Processed {newCount} new run(s). Total runs: {db.ProcessedRuns.Count}.");

        if (newCount > 0)
        {
            db.Save(MainFile.SavePath, msg => MainFile.Logger.Warn(msg));
            StatsLogger.LogAllCards(db);
        }
    }

    private static void ProcessRun(string path, string runId, StatsDb db)
    {
        var json = File.ReadAllText(path);
        var root = JsonNode.Parse(json) ?? throw new Exception("Failed to parse JSON");

        // Skip abandoned runs
        if (root["was_abandoned"]?.GetValue<bool>() == true)
            return;

        bool won = root["win"]?.GetValue<bool>() ?? false;

        var character = root["players"]?[0]?["character"]?.GetValue<string>() ?? "UNKNOWN";
        var ascension = root["ascension"]?.GetValue<int>() ?? 0;

        // Walk all map points across all acts and collect card choices with context
        var allChoices = new List<(string cardId, bool wasPicked, RunContext context)>();

        var actArray = root["map_point_history"]?.AsArray();
        if (actArray == null) return;

        for (int actIndex = 0; actIndex < actArray.Count; actIndex++)
        {
            var floors = actArray[actIndex]?.AsArray();
            if (floors == null) continue;

            var context = new RunContext(character, ascension, actIndex + 1);

            foreach (var floor in floors)
            {
                var playerStatsArr = floor?["player_stats"]?.AsArray();
                if (playerStatsArr == null) continue;

                foreach (var playerStats in playerStatsArr)
                {
                    var cardChoices = playerStats?["card_choices"]?.AsArray();
                    if (cardChoices == null) continue;

                    bool anyPicked = false;
                    foreach (var choice in cardChoices)
                    {
                        var cardId = choice?["card"]?["id"]?.GetValue<string>();
                        if (cardId == null) continue;

                        var upgradeLevel = choice?["card"]?["current_upgrade_level"]?.GetValue<int>() ?? 0;
                        var cardKey = upgradeLevel > 0 ? cardId + "+" : cardId;

                        bool wasPicked = choice?["was_picked"]?.GetValue<bool>() ?? false;
                        allChoices.Add((cardKey, wasPicked, context));
                        if (wasPicked) anyPicked = true;
                    }

                    // Record a skip if a reward screen was shown but nothing was picked
                    if (cardChoices.Count > 0 && !anyPicked)
                        allChoices.Add((SkipId, true, context));
                }
            }
        }

        // Aggregate into stats db
        // Per-run tracking: which (card, context) pairs were offered/picked this run (deduplicated)
        var offeredThisRun = new HashSet<(string cardId, string contextKey)>();
        var pickedThisRun = new HashSet<(string cardId, string contextKey)>();

        foreach (var (cardId, wasPicked, context) in allChoices)
        {
            var stat = db.GetOrCreate(cardId, context);

            // Per-instance
            stat.Offered++;
            if (wasPicked) stat.Picked++;

            // Per-run (deduplicated per card+context)
            offeredThisRun.Add((cardId, context.ToKey()));
            if (wasPicked) pickedThisRun.Add((cardId, context.ToKey()));
        }

        // Apply per-run counters
        foreach (var (cardId, contextKey) in offeredThisRun)
            db.GetOrCreate(cardId, RunContext.Parse(contextKey)).RunsOffered++;

        foreach (var (cardId, contextKey) in pickedThisRun)
        {
            var stat = db.GetOrCreate(cardId, RunContext.Parse(contextKey));
            stat.RunsPicked++;
            if (won) stat.RunsWon++;
        }

        // Per-instance wins: count each picked card that was in a winning deck
        if (won)
        {
            foreach (var (cardId, wasPicked, context) in allChoices)
                if (wasPicked) db.GetOrCreate(cardId, context).Won++;
        }
    }

    private static List<string> GetHistoryDirectories()
    {
        var found = new List<string>();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var sts2Root = Path.Combine(appData, "SlayTheSpire2", "steam");

        if (!Directory.Exists(sts2Root))
            return found;

        foreach (var steamIdDir in Directory.GetDirectories(sts2Root))
        {
            var standardHistory = Path.Combine(steamIdDir, "profile1", "saves", "history");
            if (Directory.Exists(standardHistory))
                found.Add(standardHistory);

            var moddedHistory = Path.Combine(steamIdDir, "modded", "profile1", "saves", "history");
            if (Directory.Exists(moddedHistory))
                found.Add(moddedHistory);
        }

        return found;
    }
}
