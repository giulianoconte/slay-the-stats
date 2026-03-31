using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlayTheStats;

public static class RunParser
{
    /// <summary>
    /// Finds all unprocessed .run files in the history directory and processes them into the stats db.
    /// </summary>
    public const string SkipId = "SKIP";

    public static void ProcessNewRuns(StatsDb db, string savePath, Action<string>? log = null, Action<string>? warn = null)
    {
        // If the file was deleted externally, reset in-memory state and reprocess everything
        if (!File.Exists(savePath) && db.ProcessedRuns.Count > 0)
        {
            log?.Invoke("Stats file was deleted. Reprocessing all runs.");
            db.Reset();
        }

        var historyDirs = GetHistoryDirectories();
        if (historyDirs.Count == 0)
        {
            warn?.Invoke("Could not find any STS2 run history directories.");
            return;
        }

        int newCount = 0;

        foreach (var historyDir in historyDirs)
        {
            log?.Invoke($"Scanning history directory: {historyDir}");
            var runFiles = Directory.GetFiles(historyDir, "*.run");

            foreach (var path in runFiles)
            {
                var runId = Path.GetFileNameWithoutExtension(path);
                if (db.ProcessedRuns.Contains(runId))
                    continue;

                try
                {
                    ProcessRun(path, runId, db, warn);
                    db.ProcessedRuns.Add(runId);
                    newCount++;
                }
                catch (Exception e)
                {
                    warn?.Invoke($"Failed to parse run {runId}: {e.Message}");
                }
            }
        }

        log?.Invoke($"Processed {newCount} new run(s). Total runs: {db.ProcessedRuns.Count}.");

        if (newCount > 0)
            db.Save(savePath, warn);
    }

    internal static void ProcessRun(string path, string runId, StatsDb db, Action<string>? warn = null)
    {
        var json = File.ReadAllText(path);
        var root = JsonNode.Parse(json) ?? throw new Exception("Failed to parse JSON");

        // Skip abandoned runs
        if (root["was_abandoned"]?.GetValue<bool>() == true)
            return;

        bool won = root["win"]?.GetValue<bool>() ?? false;

        var character = root["players"]?[0]?["character"]?.GetValue<string>();
        if (character == null)
            warn?.Invoke($"Run {runId}: missing players[0].character — game format may have changed.");
        character ??= "UNKNOWN";

        var ascension = root["ascension"]?.GetValue<int>() ?? 0;

        var buildVersion = root["build_id"]?.GetValue<string>();
        if (buildVersion == null)
            warn?.Invoke($"Run {runId}: missing build_id — game format may have changed.");
        buildVersion ??= "UNKNOWN";

        var gameMode = root["game_mode"]?.GetValue<string>();
        if (gameMode == null)
            warn?.Invoke($"Run {runId}: missing game_mode — game format may have changed.");
        gameMode ??= "UNKNOWN";

        // Walk all map points across all acts and collect card choices and relic acquisitions with context
        var allChoices = new List<(string cardId, bool wasPicked, RunContext context)>();
        var relicsAcquired = new List<(string relicId, RunContext context)>();
        var relicsSeenInFloors = new HashSet<string>();

        // Per-run shop tracking (deduplicated per item+context)
        var shopSeenThisRun        = new HashSet<(string id, string contextKey)>();
        var shopBoughtThisRun      = new HashSet<(string id, string contextKey)>();
        var relicShopSeenThisRun   = new HashSet<(string id, string contextKey)>();
        var relicShopBoughtThisRun = new HashSet<(string id, string contextKey)>();

        var actArray = root["map_point_history"]?.AsArray();
        if (actArray == null)
        {
            warn?.Invoke($"Run {runId}: missing map_point_history — game format may have changed.");
            return;
        }

        // Build cumulative floor counts per act so deck cards can be assigned an act
        // based on their floor_added_to_deck value.
        int cumulativeFloors = 0;
        var actBoundaries = new List<int>(actArray.Count);
        for (int i = 0; i < actArray.Count; i++)
        {
            cumulativeFloors += actArray[i]?.AsArray()?.Count ?? 0;
            actBoundaries.Add(cumulativeFloors);
        }

        int FloorToAct(int floor)
        {
            for (int a = 0; a < actBoundaries.Count; a++)
            {
                if (floor <= actBoundaries[a]) return a + 1;
            }
            return Math.Max(1, actBoundaries.Count);
        }

        for (int actIndex = 0; actIndex < actArray.Count; actIndex++)
        {
            var floors = actArray[actIndex]?.AsArray();
            if (floors == null) continue;

            var context = new RunContext(character, ascension, actIndex + 1, gameMode, buildVersion);

            foreach (var floor in floors)
            {
                var mapPointType = floor?["map_point_type"]?.GetValue<string>();
                bool isShop        = mapPointType == "shop";
                bool isFightReward = mapPointType == "monster" || mapPointType == "elite" || mapPointType == "boss";

                var playerStatsArr = floor?["player_stats"]?.AsArray();
                if (playerStatsArr == null) continue;

                foreach (var playerStats in playerStatsArr)
                {
                    var cardChoices = playerStats?["card_choices"]?.AsArray();
                    if (cardChoices != null)
                    {
                        if (isFightReward)
                        {
                            // Fight reward screen (monster/elite/boss): track for Pick% stats
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
                            if (cardChoices.Count > 0)
                            {
                                db.TotalRewardScreens++;
                                if (!anyPicked)
                                {
                                    db.TotalSkips++;
                                    allChoices.Add((SkipId, true, context));
                                }
                            }
                        }
                        else
                        {
                            // Shop floor: card_choices lists the shop inventory; track for Shop% seen
                            foreach (var choice in cardChoices)
                            {
                                var cardId = choice?["card"]?["id"]?.GetValue<string>();
                                if (cardId == null) continue;
                                var upgradeLevel = choice?["card"]?["current_upgrade_level"]?.GetValue<int>() ?? 0;
                                var cardKey = upgradeLevel > 0 ? cardId + "+" : cardId;
                                shopSeenThisRun.Add((cardKey, context.ToKey()));
                            }
                        }
                    }

                    if (isShop)
                    {
                        // cards_gained on shop floors = purchased cards (no upgrade level available)
                        var cardsGained = playerStats?["cards_gained"]?.AsArray();
                        if (cardsGained != null)
                        {
                            foreach (var card in cardsGained)
                            {
                                var cardId = card?["id"]?.GetValue<string>();
                                if (cardId != null)
                                    shopBoughtThisRun.Add((cardId, context.ToKey()));
                            }
                        }

                        // bought_relics = purchased relics
                        var boughtRelics = playerStats?["bought_relics"]?.AsArray();
                        if (boughtRelics != null)
                        {
                            foreach (var entry in boughtRelics)
                            {
                                var relicId = entry?.GetValue<string>();
                                if (relicId != null)
                                    relicShopBoughtThisRun.Add((relicId, context.ToKey()));
                            }
                        }
                    }

                    var relicChoices = playerStats?["relic_choices"]?.AsArray();
                    if (relicChoices != null)
                    {
                        foreach (var choice in relicChoices)
                        {
                            var relicId = choice?["choice"]?.GetValue<string>();
                            if (relicId == null) continue;

                            relicsSeenInFloors.Add(relicId);

                            bool wasPicked = choice?["was_picked"]?.GetValue<bool>() ?? false;
                            if (wasPicked)
                                relicsAcquired.Add((relicId, context));

                            // Shop floor: relic_choices lists the shop's relic inventory
                            if (isShop)
                                relicShopSeenThisRun.Add((relicId, context.ToKey()));
                        }
                    }
                }
            }
        }

        // Starter relic: present in players[0].relics but never appears in relic_choices — assign to act 1
        var starterContext = new RunContext(character, ascension, 1, gameMode, buildVersion);
        var finalRelics = root["players"]?[0]?["relics"]?.AsArray();
        if (finalRelics != null)
        {
            foreach (var relic in finalRelics)
            {
                var relicId = relic?["id"]?.GetValue<string>();
                if (relicId != null && !relicsSeenInFloors.Contains(relicId))
                    relicsAcquired.Add((relicId, starterContext));
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

        // Apply per-run fight-reward counters (Pick% stats)
        foreach (var (cardId, contextKey) in offeredThisRun)
            db.GetOrCreate(cardId, RunContext.Parse(contextKey)).RunsOffered++;

        foreach (var (cardId, contextKey) in pickedThisRun)
            db.GetOrCreate(cardId, RunContext.Parse(contextKey)).RunsPicked++;

        // End-of-run presence pass: read players[0].deck and record RunsPresent/RunsWon
        // for every card present in the final build, regardless of acquisition source.
        var presenceThisRun = new HashSet<(string cardId, string contextKey)>();
        var finalDeck = root["players"]?[0]?["deck"]?.AsArray();
        if (finalDeck != null)
        {
            foreach (var card in finalDeck)
            {
                var deckCardId = card?["id"]?.GetValue<string>();
                if (deckCardId == null) continue;

                var upgradeLevel = card?["current_upgrade_level"]?.GetValue<int>() ?? 0;
                var cardKey = upgradeLevel > 0 ? deckCardId + "+" : deckCardId;

                var floor = card?["floor_added_to_deck"]?.GetValue<int>() ?? 1;
                var act   = FloorToAct(floor);
                var ctx   = new RunContext(character, ascension, act, gameMode, buildVersion);
                presenceThisRun.Add((cardKey, ctx.ToKey()));
            }
        }

        foreach (var (cardId, contextKey) in presenceThisRun)
        {
            var stat = db.GetOrCreate(cardId, RunContext.Parse(contextKey));
            stat.RunsPresent++;
            if (won) stat.RunsWon++;
        }

        // Enforce consistency: a purchased item must have been seen.
        // Colorless cards sold from a special shop section appear in cards_gained but not in
        // card_choices, so their seen entry is missing. Add it here so RunsShopBought ≤ RunsShopSeen.
        foreach (var entry in shopBoughtThisRun)    shopSeenThisRun.Add(entry);
        foreach (var entry in relicShopBoughtThisRun) relicShopSeenThisRun.Add(entry);

        // Per-run shop counters
        foreach (var (id, contextKey) in shopSeenThisRun)
            db.GetOrCreate(id, RunContext.Parse(contextKey)).RunsShopSeen++;

        foreach (var (id, contextKey) in shopBoughtThisRun)
            db.GetOrCreate(id, RunContext.Parse(contextKey)).RunsShopBought++;

        foreach (var (id, contextKey) in relicShopSeenThisRun)
            db.GetOrCreateRelic(id, RunContext.Parse(contextKey)).RunsShopSeen++;

        foreach (var (id, contextKey) in relicShopBoughtThisRun)
            db.GetOrCreateRelic(id, RunContext.Parse(contextKey)).RunsShopBought++;

        // Relic stats: one entry per acquired relic
        foreach (var (relicId, context) in relicsAcquired)
        {
            var stat = db.GetOrCreateRelic(relicId, context);
            stat.RunsPresent++;
            if (won) stat.RunsWon++;
        }

        // Character-level run/win counts — used as the WR baseline in tooltips
        var charStat = db.GetOrCreateCharacter(character, gameMode);
        charStat.Runs++;
        if (won)
        {
            charStat.Wins++;
            if (!db.HighestWonAscensions.TryGetValue(character, out var prev) || ascension > prev)
                db.HighestWonAscensions[character] = ascension;
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
            foreach (var profileDir in Directory.GetDirectories(steamIdDir, "profile*"))
            {
                var history = Path.Combine(profileDir, "saves", "history");
                if (Directory.Exists(history))
                    found.Add(history);
            }

            var moddedDir = Path.Combine(steamIdDir, "modded");
            if (Directory.Exists(moddedDir))
            {
                foreach (var profileDir in Directory.GetDirectories(moddedDir, "profile*"))
                {
                    var history = Path.Combine(profileDir, "saves", "history");
                    if (Directory.Exists(history))
                        found.Add(history);
                }
            }
        }

        return found;
    }
}
