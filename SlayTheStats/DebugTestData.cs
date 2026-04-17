namespace SlayTheStats;

/// <summary>
/// Injects fake biomes, acts, and characters into the DB for UI scalability testing.
/// Gated on <see cref="SlayTheStatsConfig.DebugMode"/>. Safe to leave in the build —
/// the method no-ops when debug mode is off.
/// </summary>
internal static class DebugTestData
{
    private static readonly string[] FakeBiomes =
    {
        "ACT.CRYSTALCAVERNS",
        "ACT.FORGOTTENTOMB",
        "ACT.SKYHARBOR",
        "ACT.SUNKENRUINS",
        "ACT.ASHLANDS",
    };

    private static readonly string[] FakeCharacters =
    {
        "CHARACTER.ALCHEMIST",
        "CHARACTER.BEASTMASTER",
        "CHARACTER.CHRONOMANCER",
        "CHARACTER.RUNESMITH",
        "CHARACTER.VOID_WALKER",
    };

    private static readonly string[] Categories = { "weak", "normal", "elite", "boss" };

    internal static void InjectIfDebug(StatsDb db)
    {
        if (!SlayTheStatsConfig.DebugMode) return;

        var rng = new Random(42);
        int actBase = 4;

        foreach (var biome in FakeBiomes)
        {
            int act = actBase++;
            foreach (var category in Categories)
            {
                int encounterCount = category switch
                {
                    "weak" => 3,
                    "normal" => 4,
                    "elite" => 2,
                    "boss" => 2,
                    _ => 2
                };

                for (int i = 0; i < encounterCount; i++)
                {
                    string biomeName = biome.Replace("ACT.", "");
                    string monsterName = $"{biomeName}_{category.ToUpperInvariant()}_{i + 1}";
                    string encounterId = $"ENCOUNTER.{monsterName}_{category.ToUpperInvariant()}";

                    if (!db.EncounterMeta.ContainsKey(encounterId))
                    {
                        db.EncounterMeta[encounterId] = new EncounterMeta
                        {
                            MonsterIds = new List<string> { $"MONSTER.{monsterName}" },
                            Category = category,
                            Biome = biome,
                            Act = act,
                        };
                    }

                    var contextMap = db.Encounters.GetValueOrDefault(encounterId)
                                    ?? (db.Encounters[encounterId] = new Dictionary<string, EncounterEvent>());

                    var allChars = new List<string>
                    {
                        "CHARACTER.IRONCLAD", "CHARACTER.SILENT", "CHARACTER.REGENT",
                        "CHARACTER.NECROBINDER", "CHARACTER.DEFECT",
                    };
                    allChars.AddRange(FakeCharacters);

                    foreach (var character in allChars)
                    {
                        var ctx = new RunContext(character, 5, act, "standard", "v0.100.0");
                        var key = ctx.ToKey();
                        if (contextMap.ContainsKey(key)) continue;

                        int fought = rng.Next(5, 30);
                        int died = rng.Next(0, Math.Max(1, fought / 5));
                        int won = rng.Next(fought / 3, fought);
                        int baseDmg = category switch
                        {
                            "weak" => rng.Next(3, 10),
                            "normal" => rng.Next(8, 20),
                            "elite" => rng.Next(15, 35),
                            "boss" => rng.Next(20, 50),
                            _ => 10,
                        };

                        var dmgValues = new List<int>();
                        int dmgSum = 0;
                        double dmgPctSum = 0;
                        for (int f = 0; f < fought; f++)
                        {
                            int dmg = Math.Max(0, baseDmg + rng.Next(-baseDmg / 2, baseDmg));
                            dmgValues.Add(dmg);
                            dmgSum += dmg;
                            dmgPctSum += dmg / 80.0;
                        }

                        contextMap[key] = new EncounterEvent
                        {
                            Fought = fought,
                            Died = died,
                            WonRun = won,
                            TurnsTakenSum = fought * rng.Next(2, 7),
                            DamageTakenSum = dmgSum,
                            DmgPctSum = dmgPctSum,
                            MaxHpSum = fought * 80,
                            HpEnteringSum = fought * rng.Next(40, 70),
                            PotionsUsedSum = rng.Next(0, fought),
                            DamageValues = dmgValues,
                        };
                    }
                }
            }
        }

        // Second pass: inject fake character data into existing real encounters
        // so they appear in the all-characters table when hovering real encounters.
        foreach (var (encId, contextMap) in db.Encounters)
        {
            if (encId.StartsWith("ENCOUNTER.CRYSTALCAVERNS") ||
                encId.StartsWith("ENCOUNTER.FORGOTTENTOMB") ||
                encId.StartsWith("ENCOUNTER.SKYHARBOR") ||
                encId.StartsWith("ENCOUNTER.SUNKENRUINS") ||
                encId.StartsWith("ENCOUNTER.ASHLANDS"))
                continue; // already has fake chars from pass 1

            if (!db.EncounterMeta.TryGetValue(encId, out var meta)) continue;

            foreach (var fakeChar in FakeCharacters)
            {
                var ctx = new RunContext(fakeChar, 5, meta.Act, "standard", "v0.100.0");
                var key = ctx.ToKey();
                if (contextMap.ContainsKey(key)) continue;

                int fought = rng.Next(4, 20);
                int baseDmg = meta.Category switch
                {
                    "weak" => rng.Next(3, 10),
                    "normal" => rng.Next(8, 20),
                    "elite" => rng.Next(15, 35),
                    "boss" => rng.Next(20, 50),
                    _ => 10,
                };

                var dmgValues = new List<int>();
                int dmgSum = 0;
                double dmgPctSum = 0;
                for (int f = 0; f < fought; f++)
                {
                    int dmg = Math.Max(0, baseDmg + rng.Next(-baseDmg / 2, baseDmg));
                    dmgValues.Add(dmg);
                    dmgSum += dmg;
                    dmgPctSum += dmg / 80.0;
                }

                contextMap[key] = new EncounterEvent
                {
                    Fought = fought,
                    Died = rng.Next(0, Math.Max(1, fought / 5)),
                    WonRun = rng.Next(fought / 3, fought),
                    TurnsTakenSum = fought * rng.Next(2, 7),
                    DamageTakenSum = dmgSum,
                    DmgPctSum = dmgPctSum,
                    MaxHpSum = fought * 80,
                    HpEnteringSum = fought * rng.Next(40, 70),
                    PotionsUsedSum = rng.Next(0, fought),
                    DamageValues = dmgValues,
                };
            }
        }

        MainFile.Logger.Info($"DebugTestData: injected {FakeBiomes.Length} fake biomes and {FakeCharacters.Length} fake characters");
    }
}
