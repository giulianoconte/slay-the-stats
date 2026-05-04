# SlayTheStats

A Slay the Spire 2 mod that tracks card, relic, and encounter stats across your runs. Card and relic stats show as tooltips on hover; encounter stats show on a new Stats Bestiary compendium page, in a tooltip on combat enemy hovers, and in a comparison tooltip on the post-fight reward screen.

---

## For Players

### What It Does

SlayTheStats reads your full run history retroactively. Stats are updated when you finish a run or start the game. Abandoned runs are skipped.

**Card and relic stats:**

- **Pick%** — how often you pick a card when it's offered as a fight reward.
- **Win%** — how often you win runs that include a given card or relic.
- **Runs** — shown as a fraction of runs the card was in your final deck / runs it appeared on a fight reward screen (e.g. `12/30`). For relics, just the number of runs it was present.
- **Buys** — for colorless cards and relics: Shown as a fraction of runs you purchased the item from a shop / runs it appeared in a shop (e.g. `12/30`).

Stats are shown as a tooltip when you hover over cards and relics during a run, in shops, and in the compendium. Stats are broken out by act acquired.

**Encounter stats** (Stats Bestiary + in-combat + post-fight):

- **Stats Bestiary** — new compendium page listing every encounter, grouped by biome/act and category (weak / normal / elite / boss / event). Columns: Fights, Dist (damage distribution sparkline), Dmg (median damage taken), Mid 50% (typical damage range), Spread (Mid 50% as a fraction of the median), Turns, Pots, and Deaths.
- **Live monster preview** — hover an encounter to see its monsters rendered live in the right-hand panel. A few encounters (Skulking Colony, Kaiser Crab, Doormaker) don't have previews yet and show a "Preview WIP" placeholder instead.
- **Table Style selector** — switch between an all-characters comparison view and a per-character focused view with the full stat breakdown.
- **In-combat tooltip** — hover an enemy (or its health bar) for ~0.75s during combat to see your historical stats for that encounter, scoped to your current run's character.
- **Post-fight comparison tooltip** — after winning a fight, a tooltip on the reward screen compares this fight's damage / turns / potions to your historical average with a significance-colored delta.

**Event stats** (in-run hover):

- Hover an event option in-run to see how many times you've picked it, your win rate when you did, and a baseline for comparison.
- Multi-step events (e.g. Slippery Bridge) bucket per terminal option so each branch of the choice tree gets its own stats.
- Some events don't record what was offered, only what was picked — e.g. Future of Potions, where the rarity is randomized per visit. For those, the tooltip shows raw pick counts rather than a Pick%, since the denominator (times offered) isn't recoverable from the run history.

**Color coding** helps you read the data at a glance:
- Pick% and Win% are colored relative to your personal baseline — your overall win rate for the current character, and your average pick rate across all fight reward screens (accounting for how often you skip). Green/teal means above baseline, orange/red means below.
- Color intensity scales with both sample size and deviation: a small deviation or thin sample stays muted; a large deviation with solid evidence shows vivid color. Intensity is computed as `tanh(k × n × deviation)`, so it is jointly sensitive to both — a moderate deviation across many runs can match a strong deviation across few runs.
- A color-blind mode (teal/orange instead of green/red) is available in settings.

### Requirements

- [BaseLib](https://www.nexusmods.com/slaythespire2/mods/103) v3.1.0 or later.
- **NOTE:** GUMM is NOT required. If you previously installed it for this mod, see [Uninstalling GUMM](#uninstalling-gumm) below.

### Installation

1. Download and extract [BaseLib](https://www.nexusmods.com/slaythespire2/mods/103) (v3.1.0 or later) into your mods folder if you haven't already
2. Download `SlayTheStats-vX.X.X.zip` from the releases page and extract it into your STS2 mods folder — the result should be a `SlayTheStats/` folder inside `mods/` containing `SlayTheStats.dll`, `SlayTheStats.json`, and `SlayTheStats.pck`

### Uninstalling

To fully remove the mod (or wipe its state for a clean reinstall), delete these things:

1. The `SlayTheStats/` folder inside your STS2 `mods/` directory
2. `SlayTheStats.cfg` in the `mod_configs/` folder next to your STS2 save data — stores mod settings and filter defaults
3. `slay-the-stats.json` in your STS2 save data folder — stores the parsed run stats database

Paths for items 2–3:
- **Windows:** `%APPDATA%\Slay the Spire 2\`  (contains `mod_configs\SlayTheStats.cfg` and `slay-the-stats.json`)
- **Linux:** `~/.local/share/Slay the Spire 2/`  (contains `mod_configs/SlayTheStats.cfg` and `slay-the-stats.json`)

If you only want to reset stats but keep your settings, delete just `slay-the-stats.json` — it will be rebuilt from your run history on next launch.

### Uninstalling GUMM

I was mistaken — you never needed GUMM to run this mod. Sorry! Here's how to uninstall GUMM:

1. In your STS2 install folder, next to the executable, delete `override.cfg` — otherwise the game won't launch (Godot tries to load GUMM's loader scene and exits).
2. Delete the GUMM installation folder.
3. Delete the GUMM file from your game's data folder:
   - **Windows:** `%APPDATA%\Slay the Spire 2\`
   - **Linux:** `~/.local/share/Slay the Spire 2/`

### Compendium Filters

Open the in-game compendium (Card Library or Relic Collection) and click the **SlayTheStats** button in the sidebar to open the filter pane. The pane lets you tune which runs feed your stats:

- **Ascension range** — show stats only from runs within an ascension band. The "Lowest" and "Highest" entries auto-track the actual ascensions present in your data, so they keep working if mods add new levels.
- **Version range** — restrict to specific game versions, useful for ignoring runs from old balance patches.
- **Profile** — restrict to a single save profile, or aggregate across all profiles.
- **Class** — `Auto` restricts to the character class for the card. You can also force a specific character — e.g. to show only Silent's stats for colorless cards.
- **Group card upgrades** — merge upgraded variants (e.g. Strike+ counts toward Strike) or track them separately. Default on.
- **Include multiplayer runs** — include co-op runs alongside standard runs. Default off (only standard runs are counted, matching prior behavior). **Multiplayer stats tracking is untested.**

Active filters are highlighted in green so it's obvious which controls are diverging from your defaults. Three action buttons at the bottom:

- **Clear Filters** — back to all-open (no constraints)
- **Reset** — restore your saved defaults
- **Save Defaults** — persist the current values as your new defaults

Filter changes you make while in a run are temporary — they let you slice the data without committing — and snap back to your saved defaults the next time you boot the game. **Save Defaults** is the only way to persist a change across game boots. You can also open the filter pane standalone from the mod settings menu (via "Open Filters") to edit your defaults without entering the compendium.

### Understanding the Stats

**Pick%** is sourced only from fight reward screens — the 3-card choice after defeating an enemy. Shop purchases, event cards, ancient (Neow) rewards, and other acquisition sources are not counted. Some relics modify reward screens (e.g. adding an extra card or replacing choices); those modified screens are also excluded since the pool is no longer a standard 3-card offer.

**Win%** counts all runs in which the card or relic was present in your final build, from any source.

**Runs** shows `present/offered` for cards (e.g. `12/30` — present in 12 runs, offered on a fight reward screen in 30) and just the present count for relics.

**Shop purchases (Buys):** For colorless cards and relics, the tooltip shows how often you purchased an item relative to how often it appeared in a shop, colored relative to your overall shop buy rate. Note that appearances are counted regardless of whether you could afford the item.

**Upgraded cards** (e.g. Tremble+) are grouped with their base versions by default — Tremble+ counts toward Tremble's stats. Toggle this via the **Group card upgrades** control in the filter pane (see [Compendium Filters](#compendium-filters) above).

**Colorless Cards**, **Event Cards**, **Ancient Relics**, **Curses**, etc are all tracked.

**Duplicate runs across profiles** are only counted once. If the same run file appears in multiple profile folders (e.g. after transferring a vanilla profile into a modded slot), the mod dedupes by run id so each run contributes a single entry — regardless of how many profile folders it lives in.

Mod settings can be configured in-game from BaseLib's mod configuration page:

- **Color Blind Mode** — teal/orange palette instead of green/red for stat coloring.
- **Show In-Run Stats** — toggle stat tooltips during a run (card rewards, shop, relic hovers). When off, stats only appear in the compendium.
- **Disable All Stat Tooltips** — master off switch; turns off every stat tooltip in the game.
- **Tutorial Seen / Bestiary Tutorial Seen** — toggle off to re-show the corresponding welcome overlay next time you open the compendium or the bestiary.
- **Encounter Stats (requires restart)** — dropdown with three modes: `BestiaryAndTooltips` (Stats Bestiary button in the compendium + in-combat enemy hover tooltip, default), `Tooltips` (tooltip only, bestiary button hidden), or `Disabled` (both off). Takes effect on next launch.
- **Data Directory** — override the SlayTheSpire2 data directory path (the folder containing `steam/`). Leave empty to use the platform default.
- **Debug Mode** — enable verbose logging for bug reports.

Filtering by ascension, version, profile, character, or upgrade grouping is done via the filter pane in the compendium — see [Compendium Filters](#compendium-filters) above. The bestiary has its own settings pane (bottom-left, next to the filter button) with a toggle to disable the live monster preview if you'd prefer zero GPU cost.

### FAQ

**Why does a card show more "present" runs than "offered" runs (e.g. 5/1)?**
With **Group card upgrades** turned off in the filter pane, upgraded and base versions are tracked separately. "Present" counts all runs the card ended up in your deck from any source — including upgrading the base at a campfire — while "offered" only counts fight reward screens. Turn **Group card upgrades** back on (the default) in the filter pane to merge them. Starter cards and relics (those you begin a run with) will always show 0 for "offered" since they never appear on fight reward screens.

**Why is my Win% 100% but the stat is grey?**
Color intensity reflects both sample size and magnitude of deviation from your baseline. With very few runs, even a perfect rate stays muted — the color intensifies as evidence builds up.

**Why don't I see stats for a card I've used?**
Open the filter pane in the compendium and check your filters. If your ascension range is restricted, runs outside that band are hidden. If your class filter is set to a specific character, cards from other characters are hidden. If **Group card upgrades** is off, the upgraded and base versions of each card are tracked separately, which can split sparse data. You may also just need more runs with the card.

**I'm seeing "no data" for everything.**
The mod can't find your run history. Set the **Data Directory** setting (in BaseLib's mod configuration page) to the `SlayTheSpire2` folder containing your save data — `%APPDATA%\SlayTheSpire2` on Windows, `~/.local/share/SlayTheSpire2/` on Linux. Note that you need the full path. Please file a bug report so I can fix the platform default.

**I lost my save data after installing.**
It's not lost. The game separates modded profiles from normal ones by default, so installing mods makes vanilla progress look gone. If you want vanilla untouched, create a new modded profile and use an unlock mod for progress. To copy a vanilla profile into a modded slot, see [Transferring a Vanilla Profile to a Modded Profile](#transferring-a-vanilla-profile-to-a-modded-profile) below. The UnifiedSavePath mod is another option.

### Where Stats Are Saved

Stats are stored in `slay-the-stats.json`:
- **Windows:** `%APPDATA%\Slay the Spire 2\`
- **Linux:** `~/.local/share/Slay the Spire 2/`

On startup, if the mod version has changed since your last session, all run history is reprocessed to update stats.

### Transferring a Vanilla Profile to a Modded Profile

> Disclaimer — this is how I did it on 2026-04-24. It may no longer work. For the most up-to-date method, ask in the `sts2-modding` channel on the Slay the Spire Discord.

The game stores modded saves in a separate `modded/` subfolder, so installing mods makes vanilla progress look gone. To copy a vanilla profile over:

1. **BACK UP your vanilla profile folder first.** Do **not** later try to copy modded saves back over vanilla — modded run history can contain data vanilla can't parse and will corrupt the file. Vanilla → modded only.
2. Launch with mods loaded and switch in-game to a profile slot **other** than the one you want to overwrite (e.g. to import vanilla profile 1, first switch to modded profile 2 or 3). The game holds the active slot in memory and would otherwise overwrite your copy on the next save.
3. Leaving the game running, copy the contents of `profile1/` (or whichever) into `modded/profile1` (same profile number). Save roots:
   - **Windows:** `%APPDATA%\SlayTheSpire2\steam\<STEAM_ID>\`
   - **macOS:** `~/Library/Application Support/SlayTheSpire2/steam/<STEAM_ID>/`
   - **Linux:** `~/.local/share/SlayTheSpire2/steam/<STEAM_ID>/`
   - You can also open the in-game console (`` ` `` or `~`) and type `open saves` to jump there.
4. Back in-game, switch to the target profile slot. Your vanilla progress should now be visible.

### Troubleshooting

Check the Godot log for `[SlayTheStats]` entries:
- **Windows:** `%APPDATA%\Godot\app_userdata\Slay the Spire 2\logs\godot.log`
- **Linux:** `~/.local/share/godot/app_userdata/Slay the Spire 2/logs/godot.log`

### Translations

The mod is localized into Russian and Simplified Chinese in-game; the locale follows the game's language setting. Native-speaker translation review is pending — if you spot awkward or wrong translations, please leave a comment or ping me on Discord.

Want to help translate the mod into another language? DM me on the Slay the Spire Discord — my handle is `.theshoe`.

---

## For Modders / Developers

### Tech Stack

- **Language:** C# (.NET 9)
- **Modding layer:** [BaseLib](https://github.com/Alchyr/BaseLib-StS2) + HarmonyX (uses STS2's built-in mod loader)
- **Build target:** Godot 4.5.1 / STS2

### Prerequisites

- [.NET 9.0 SDK](https://dot.net)
- `sts2.dll` and `0Harmony.dll` copied from your STS2 install into `SlayTheStats/libs/`
  - Windows path: `steamapps\common\Slay the Spire 2\`

### Setup

1. Copy `SlayTheStats/local.props.example` to `SlayTheStats/local.props`
2. Set `ModsPath` to your STS2 mods directory

Without `local.props`, build output goes to `SlayTheStats/dist/` and you need to copy it manually.

### Building

```bash
./deploy.sh
```

Compiles the mod and copies the DLL and manifest to `ModsPath` (or `dist/` if not configured).

To create a release build:

```bash
./deploy.sh --release
```

Builds with Release configuration and produces `SlayTheStats-vX.X.X.zip` next to `deploy.sh`.

### Running Tests

```bash
cd SlayTheStats.Tests
dotnet test
```

### Project Structure

| File | Purpose |
|---|---|
| `MainFile.cs` | Mod entry point — initializes Harmony, loads and processes the stats DB |
| `RunParser.cs` | Parses `.run` save files from STS2 run history |
| `CardStats.cs` | Data models for card stat entries; also contains `StatsDb` and `RunContext` |
| `RelicStats.cs` | Data model for relic stat entries |
| `StatsAggregator.cs` | Aggregates raw per-context stats into per-act totals for display |
| `CardStatsTooltipPatch.cs` | Harmony patches for card hover tooltips |
| `RelicStatsTooltipPatch.cs` | Harmony patches for relic hover tooltips |
| `TooltipHelper.cs` | Shared tooltip rendering and color helpers |
| `Patches.cs` | Harmony patches for run-end hook and character tracking |
| `SlayTheStatsConfig.cs` | Mod settings (color blind mode, in-run stats toggle, master tooltip kill switch, debug mode, data directory) and persisted filter-pane state (ascension/version/profile/class/group-upgrades + their saved defaults) |
| `StatsLogger.cs` | Debug utility — logs per-act stat tables to the Godot log |

### How Stats Are Tracked

Stats are keyed by a context string: `character|ascension|act|gameMode|buildVersion`. The DB is a flat dictionary of these composite keys to `CardStat` counters. This makes aggregation over any subset of dimensions a simple filter-and-sum pass.

Run files (`.run`) are parsed from the STS2 run history directory. Each run is parsed once; already-processed runs are skipped. If the mod version changes, all runs are reprocessed from scratch.

Pick rate is only incremented on fight reward screens (`card_choices` entries on monster/elite/boss floors). Shop inventory browsing, events, and other sources affect win rate only.

### Resources

- [STS2 Hello World mod guide](https://github.com/giulianoconte/slay-the-spire-2-mod-guide)
- [Mod template](https://github.com/Alchyr/ModTemplate-StS2)
- [BaseLib + wiki](https://github.com/Alchyr/BaseLib-StS2) / [wiki](https://alchyr.github.io/BaseLib-Wiki/)
