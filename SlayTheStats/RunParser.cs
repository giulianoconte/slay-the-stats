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

        foreach (var (historyDir, profile) in historyDirs)
        {
            log?.Invoke($"Scanning history directory: {historyDir} (profile: {profile})");
            var runFiles = Directory.GetFiles(historyDir, "*.run");

            foreach (var path in runFiles)
            {
                var runId = Path.GetFileNameWithoutExtension(path);
                if (db.ProcessedRuns.Contains(runId))
                    continue;

                try
                {
                    ProcessRun(path, runId, profile, db, warn);
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

    internal static void ProcessRun(string path, string runId, string profile, StatsDb db, Action<string>? warn = null)
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

        // Read acts array for biome mapping (e.g. ["ACT.OVERGROWTH", "ACT.HIVE", "ACT.GLORY"])
        var actsNode = root["acts"]?.AsArray();
        var biomeByAct = new List<string>();
        if (actsNode != null)
        {
            foreach (var actNode in actsNode)
                biomeByAct.Add(actNode?.GetValue<string>() ?? "");
        }

        var killedByEncounter = root["killed_by_encounter"]?.GetValue<string>();

        // Walk all map points across all acts and collect card choices and relic acquisitions with context
        var allChoices = new List<(string cardId, bool wasPicked, bool wasUpgraded, RunContext context)>();
        var relicsAcquired = new List<(string relicId, RunContext context)>();
        var relicsSeenInFloors = new HashSet<string>();

        // Per-run shop tracking (deduplicated per item+context)
        var shopSeenThisRun        = new HashSet<(string id, string contextKey)>();
        var shopBoughtThisRun      = new HashSet<(string id, string contextKey)>();
        var shopSeenUpgradedThisRun   = new HashSet<(string id, string contextKey)>();
        var shopBoughtUpgradedThisRun = new HashSet<(string id, string contextKey)>();
        var relicShopSeenThisRun   = new HashSet<(string id, string contextKey)>();
        var relicShopBoughtThisRun = new HashSet<(string id, string contextKey)>();

        // Verbose upgrade tracking
        var campfireUpgradesThisRun    = new List<(string cardId, RunContext context)>();
        var eventRelicUpgradesThisRun  = new List<(string cardId, RunContext context)>();

        // Encounter tracking
        var encountersThisRun = new HashSet<(string encounterId, string contextKey)>();
        string? lastEncounterId = null;
        string? lastEncounterContextKey = null;
        int previousHp = -1;

        var actArray = root["map_point_history"]?.AsArray();
        if (actArray == null)
        {
            warn?.Invoke($"Run {runId}: missing map_point_history — game format may have changed.");
            return;
        }

        // Extract starting max HP from floor 0: max_hp - max_hp_gained + max_hp_lost
        // (before Neow relic bonuses). Only update if not already known for this character.
        if (actArray.Count > 0)
        {
            var firstActFloors = actArray[0]?.AsArray();
            if (firstActFloors is { Count: > 0 })
            {
                var floor0Ps = firstActFloors[0]?["player_stats"]?[0];
                if (floor0Ps != null)
                {
                    var maxHpFloor0   = floor0Ps["max_hp"]?.GetValue<int>();
                    var maxHpGained   = floor0Ps["max_hp_gained"]?.GetValue<int>() ?? 0;
                    var maxHpLost     = floor0Ps["max_hp_lost"]?.GetValue<int>() ?? 0;
                    if (maxHpFloor0.HasValue)
                    {
                        int startingHp = maxHpFloor0.Value - maxHpGained + maxHpLost;
                        db.CharacterStartingHp[character] = startingHp;
                    }
                }
            }
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

            var context = new RunContext(character, ascension, actIndex + 1, gameMode, buildVersion, profile);

            foreach (var floor in floors)
            {
                var mapPointType = floor?["map_point_type"]?.GetValue<string>();
                bool isShop        = mapPointType == "shop";
                bool isFightReward = mapPointType == "monster" || mapPointType == "elite" || mapPointType == "boss";
                bool isCampfire    = mapPointType == "campfire";
                bool isEvent       = mapPointType == "unknown";

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
                                bool wasUpgraded = upgradeLevel > 0;

                                bool wasPicked = choice?["was_picked"]?.GetValue<bool>() ?? false;
                                allChoices.Add((cardKey, wasPicked, wasUpgraded, context));
                                if (wasPicked) anyPicked = true;
                            }

                            // Record a skip if a reward screen was shown but nothing was picked
                            if (cardChoices.Count > 0)
                            {
                                db.TotalRewardScreens++;
                                if (!anyPicked)
                                {
                                    db.TotalSkips++;
                                    allChoices.Add((SkipId, true, false, context));
                                }
                            }
                        }
                        else if (isShop)
                        {
                            // Shop floor: card_choices lists the shop inventory; track for Shop% seen
                            foreach (var choice in cardChoices)
                            {
                                var cardId = choice?["card"]?["id"]?.GetValue<string>();
                                if (cardId == null) continue;
                                var upgradeLevel = choice?["card"]?["current_upgrade_level"]?.GetValue<int>() ?? 0;
                                var cardKey = upgradeLevel > 0 ? cardId + "+" : cardId;
                                shopSeenThisRun.Add((cardKey, context.ToKey()));
                                if (upgradeLevel > 0)
                                    shopSeenUpgradedThisRun.Add((cardKey, context.ToKey()));
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

                    // Campfire upgrades: cards_upgraded on campfire floors
                    if (isCampfire)
                    {
                        var cardsUpgraded = playerStats?["cards_upgraded"]?.AsArray();
                        if (cardsUpgraded != null)
                        {
                            foreach (var card in cardsUpgraded)
                            {
                                var cardId = card?["id"]?.GetValue<string>();
                                if (cardId != null)
                                    campfireUpgradesThisRun.Add((cardId, context));
                            }
                        }
                    }

                    // Event upgrades: cards_upgraded on event (unknown) floors
                    if (isEvent)
                    {
                        var cardsUpgraded = playerStats?["cards_upgraded"]?.AsArray();
                        if (cardsUpgraded != null)
                        {
                            foreach (var card in cardsUpgraded)
                            {
                                var cardId = card?["id"]?.GetValue<string>();
                                if (cardId != null)
                                    eventRelicUpgradesThisRun.Add((cardId, context));
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

                // --- Encounter extraction ---
                var rooms = floor?["rooms"]?.AsArray();
                if (rooms != null)
                {
                    // Find the combat room (may be rooms[0] for normal fights, rooms[1] for event encounters)
                    foreach (var room in rooms)
                    {
                        var modelId = room?["model_id"]?.GetValue<string>();
                        if (modelId == null || !modelId.StartsWith("ENCOUNTER.")) continue;

                        var turnsTaken = room?["turns_taken"]?.GetValue<int>() ?? 0;
                        if (turnsTaken <= 0) continue;

                        // Read player stats for this floor (first player)
                        var ps = floor?["player_stats"]?[0];
                        int damageTaken = ps?["damage_taken"]?.GetValue<int>() ?? 0;
                        int currentHp   = ps?["current_hp"]?.GetValue<int>() ?? 0;
                        int maxHp       = ps?["max_hp"]?.GetValue<int>() ?? 1;
                        int hpHealed    = ps?["hp_healed"]?.GetValue<int>() ?? 0;
                        var potionUsed  = ps?["potion_used"]?.AsArray();
                        int potionCount = potionUsed?.Count ?? 0;

                        int hpEntering = previousHp > 0
                            ? previousHp
                            : currentHp + damageTaken - hpHealed;

                        double dmgPct = maxHp > 0 ? (double)damageTaken / maxHp : 0.0;

                        var enc = db.GetOrCreateEncounter(modelId, context);
                        enc.Fought++;
                        enc.TurnsTakenSum    += turnsTaken;
                        enc.DamageTakenSum   += damageTaken;
                        enc.DamageTakenSqSum += damageTaken * damageTaken;
                        (enc.DamageValues ??= new List<int>()).Add(damageTaken);
                        enc.HpEnteringSum    += hpEntering;
                        enc.MaxHpSum         += maxHp;
                        enc.PotionsUsedSum   += potionCount;
                        enc.DmgPctSum        += dmgPct;
                        enc.DmgPctSqSum      += dmgPct * dmgPct;

                        encountersThisRun.Add((modelId, context.ToKey()));
                        lastEncounterId = modelId;
                        lastEncounterContextKey = context.ToKey();

                        // Populate EncounterMeta on first occurrence
                        if (!db.EncounterMeta.ContainsKey(modelId))
                        {
                            var monsterIds = new List<string>();
                            var monsterIdsNode = room?["monster_ids"]?.AsArray();
                            if (monsterIdsNode != null)
                            {
                                foreach (var mid in monsterIdsNode)
                                {
                                    var mId = mid?.GetValue<string>();
                                    if (mId != null) monsterIds.Add(mId);
                                }
                            }

                            string biome = (actIndex < biomeByAct.Count) ? biomeByAct[actIndex] : "";

                            db.EncounterMeta[modelId] = new EncounterMeta
                            {
                                MonsterIds = monsterIds,
                                Category = EncounterCategory.Derive(modelId),
                                Biome = biome,
                                Act = actIndex + 1,
                            };
                        }

                        break; // only process the first combat room per floor
                    }
                }

                // Track previous HP for hp_entering computation
                var psForHp = floor?["player_stats"]?[0];
                if (psForHp != null)
                {
                    var floorHp = psForHp["current_hp"]?.GetValue<int>();
                    if (floorHp.HasValue)
                        previousHp = floorHp.Value;
                }
            }
        }

        // Starter relic: present in players[0].relics but never appears in relic_choices — assign to act 1
        var starterContext = new RunContext(character, ascension, 1, gameMode, buildVersion, profile);
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

        // Encounter: death detection via killed_by_encounter
        if (!string.IsNullOrEmpty(killedByEncounter) && killedByEncounter != "NONE.NONE"
            && lastEncounterId == killedByEncounter && lastEncounterContextKey != null)
        {
            db.GetOrCreateEncounter(killedByEncounter, RunContext.Parse(lastEncounterContextKey)).Died++;
        }

        // Encounter: win tracking — increment WonRun for all encounters fought in this run
        if (won)
        {
            foreach (var (encId, ctxKey) in encountersThisRun)
                db.GetOrCreateEncounter(encId, RunContext.Parse(ctxKey)).WonRun++;
        }

        // Aggregate into stats db
        // Per-run tracking: which (card, context) pairs were offered/picked this run (deduplicated)
        var offeredThisRun = new HashSet<(string cardId, string contextKey)>();
        var pickedThisRun = new HashSet<(string cardId, string contextKey)>();
        var offeredUpgradedThisRun = new HashSet<(string cardId, string contextKey)>();
        var pickedUpgradedThisRun = new HashSet<(string cardId, string contextKey)>();

        foreach (var (cardId, wasPicked, wasUpgraded, context) in allChoices)
        {
            var stat = db.GetOrCreate(cardId, context);

            // Per-instance
            stat.Offered++;
            if (wasPicked) stat.Picked++;

            // Per-run (deduplicated per card+context)
            offeredThisRun.Add((cardId, context.ToKey()));
            if (wasPicked) pickedThisRun.Add((cardId, context.ToKey()));

            // Verbose: track upgraded offers/picks separately
            if (wasUpgraded)
            {
                offeredUpgradedThisRun.Add((cardId, context.ToKey()));
                if (wasPicked) pickedUpgradedThisRun.Add((cardId, context.ToKey()));
            }
        }

        // Apply per-run fight-reward counters (Pick% stats)
        foreach (var (cardId, contextKey) in offeredThisRun)
            db.GetOrCreate(cardId, RunContext.Parse(contextKey)).RunsOffered++;

        foreach (var (cardId, contextKey) in pickedThisRun)
            db.GetOrCreate(cardId, RunContext.Parse(contextKey)).RunsPicked++;

        // Apply per-run verbose upgraded fight-reward counters
        foreach (var (cardId, contextKey) in offeredUpgradedThisRun)
            db.GetOrCreate(cardId, RunContext.Parse(contextKey)).RunsOfferedUpgraded++;

        foreach (var (cardId, contextKey) in pickedUpgradedThisRun)
            db.GetOrCreate(cardId, RunContext.Parse(contextKey)).RunsPickedUpgraded++;

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
                var ctx   = new RunContext(character, ascension, act, gameMode, buildVersion, profile);
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

        // Verbose: upgraded shop counters
        foreach (var (id, contextKey) in shopSeenUpgradedThisRun)
            db.GetOrCreate(id, RunContext.Parse(contextKey)).RunsShopSeenUpgraded++;

        foreach (var (id, contextKey) in shopBoughtUpgradedThisRun)
            db.GetOrCreate(id, RunContext.Parse(contextKey)).RunsShopBoughtUpgraded++;

        foreach (var (id, contextKey) in relicShopSeenThisRun)
            db.GetOrCreateRelic(id, RunContext.Parse(contextKey)).RunsShopSeen++;

        foreach (var (id, contextKey) in relicShopBoughtThisRun)
            db.GetOrCreateRelic(id, RunContext.Parse(contextKey)).RunsShopBought++;

        // Verbose: campfire and event/relic upgrade counters
        foreach (var (cardId, context) in campfireUpgradesThisRun)
            db.GetOrCreate(cardId, context).CampfireUpgrades++;

        foreach (var (cardId, context) in eventRelicUpgradesThisRun)
            db.GetOrCreate(cardId, context).EventRelicUpgrades++;

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

    /// <summary>
    /// Returns (historyDirectoryPath, profileName) tuples for all found history directories.
    /// Profile name is derived from the directory structure (e.g. "profile1", "modded/profile2").
    /// </summary>
    private static List<(string path, string profile)> GetHistoryDirectories()
    {
        var found = new List<(string, string)>();

        string sts2Root;
        if (!string.IsNullOrWhiteSpace(SlayTheStatsConfig.DataDirectory))
        {
            sts2Root = Path.Combine(SlayTheStatsConfig.DataDirectory.Trim(), "steam");
        }
        else
        {
            var specialFolder = OperatingSystem.IsWindows()
                ? Environment.SpecialFolder.ApplicationData
                : Environment.SpecialFolder.LocalApplicationData;
            var appData = Environment.GetFolderPath(specialFolder);
            sts2Root = Path.Combine(appData, "SlayTheSpire2", "steam");
        }

        if (!Directory.Exists(sts2Root))
            return found;

        foreach (var steamIdDir in Directory.GetDirectories(sts2Root))
        {
            foreach (var profileDir in Directory.GetDirectories(steamIdDir, "profile*"))
            {
                var history = Path.Combine(profileDir, "saves", "history");
                var profileName = Path.GetFileName(profileDir);
                if (Directory.Exists(history))
                    found.Add((history, profileName));
            }

            var moddedDir = Path.Combine(steamIdDir, "modded");
            if (Directory.Exists(moddedDir))
            {
                foreach (var profileDir in Directory.GetDirectories(moddedDir, "profile*"))
                {
                    var history = Path.Combine(profileDir, "saves", "history");
                    var profileName = "modded/" + Path.GetFileName(profileDir);
                    if (Directory.Exists(history))
                        found.Add((history, profileName));
                }
            }
        }

        return found;
    }

}
