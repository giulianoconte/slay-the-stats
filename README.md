# SlayTheStats

A Slay the Spire 2 mod that tracks card and relic stats across your runs and shows them as tooltips when you hover over them.

---

## For Players

### What It Does

SlayTheStats reads your full run history retroactively. Stats are updated when you finish a run or start the game. Abandoned runs are skipped.

- **Pick%** — how often you pick a card when it's offered on as a fight reward.
- **Win%** — how often you win runs that include a given card or relic.
- **Picks** — shown as a fraction of runs the card was in your final deck / runs it appeared on a fight reward screen (e.g. `12/30`). For relics, just the number of runs it was present.
- **Buys** — for colorless cards and relics: Shown as a fraction of runs you purchased the item from a shop / runs it appeared in a shop (e.g. `12/30`).

Stats are shown as a tooltip when you hover over cards and relics during a run, in shops, and in the compendium. Stats are broken out by act acquired.

**Color coding** helps you read the data at a glance:
- Pick% and Win% are colored relative to your personal baseline — your overall win rate for the current character, and your average pick rate across all fight reward screens (accounting for how often you skip). Green/teal means above baseline, orange/red means below.
- Color intensity scales with both sample size and deviation: a small deviation or thin sample stays muted; a large deviation with solid evidence shows vivid color. Intensity is computed as `tanh(k × n × deviation)`, so it is jointly sensitive to both — a moderate deviation across many runs can match a strong deviation across few runs.
- A color-blind mode (teal/orange instead of green/red) is available in settings.

### Requirements

- [GUMM (Godot Universal Mod Manager)](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) installed for STS2.
- [BaseLib](https://www.nexusmods.com/slaythespire2/mods/103) v0.2.1 or later.

### Installation

1. Install [GUMM (Godot Universal Mod Manager)](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) in STS2
2. Download and extract [BaseLib](https://www.nexusmods.com/slaythespire2/mods/103) (v0.2.1 or later) into your mods folder if you haven't already
3. Download `SlayTheStats-vX.X.X.zip` from the releases page and extract it into your STS2 mods folder — the result should be a `SlayTheStats/` folder inside `mods/` containing `SlayTheStats.dll`, `SlayTheStats.json`, `SlayTheStats.pck`, and a `fonts/` subfolder

### Understanding the Stats

**Pick%** is sourced only from fight reward screens — the 3-card choice after defeating an enemy. Shop purchases, event cards, ancient (Neow) rewards, and other acquisition sources are not counted. Some relics modify reward screens (e.g. adding an extra card or replacing choices); those modified screens are also excluded since the pool is no longer a standard 3-card offer.

**Win%** counts all runs in which the card or relic was present in your final build, from any source.

**Picks** shows `present/offered` for cards (e.g. `12/30` — present in 12 runs, offered on a fight reward screen in 30) and just the present count for relics.

**Shop purchases (Buys):** For colorless cards and relics, the tooltip shows how often you purchased an item relative to how often it appeared in a shop, colored relative to your overall shop buy rate. Note that appearances are counted regardless of whether you could afford the item.

**Upgraded cards** (e.g. Tremble+) are tracked separately from each other by default. This can be changed in the settings.

**GroupCardUpgrades**: **Upgraded cards** (e.g. Coolheaded+) can be tracked separately or grouped with their unupgraded counterparts.

**Colorless Cards**, **Event Cards**, **Ancient Relics**, **Curses**, etc are all tracked.

Settings can be configured in-game via [ModConfig](https://www.nexusmods.com/slaythespire2/mods/2) if installed (optional).

### FAQ

**Why does a card show more "present" runs than "offered" runs (e.g. 5/1)?**
With **GroupCardUpgrades** off (the default), upgraded and base versions are tracked separately. "Present" counts all runs the card ended up in your deck from any source — including upgrading the base at a campfire — while "offered" only counts fight reward screens. Enable **GroupCardUpgrades** to merge them. Starter cards and relics (those you begin a run with) will always show 0 for "offered" since they never appear on fight reward screens.

**Why is my Win% 100% but the stat is grey?**
Color intensity reflects both sample size and magnitude of deviation from your baseline. With very few runs, even a perfect rate stays muted — the color intensifies as evidence builds up.

**Why don't I see stats for a card I've used?**
Check your settings: if **OnlyHighestWonAscension** is enabled, runs at other ascension levels are hidden, which can make data sparse or absent if most of your runs were at a different level. You may also just need more runs with the card.

### Where Stats Are Saved

Stats are stored in `slay-the-stats.json`:
- **Windows:** `%APPDATA%\Slay the Spire 2\`
- **Linux:** `~/.local/share/Slay the Spire 2/`

On startup, if the mod version has changed since your last session, all run history is reprocessed to update stats.

### Troubleshooting

Check the Godot log for `[SlayTheStats]` entries:
- **Windows:** `%APPDATA%\Godot\app_userdata\Slay the Spire 2\logs\godot.log`
- **Linux:** `~/.local/share/godot/app_userdata/Slay the Spire 2/logs/godot.log`

---

## For Modders / Developers

### Tech Stack

- **Language:** C# (.NET 9)
- **Modding layer:** [GUMM](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) + [BaseLib](https://github.com/Alchyr/BaseLib-StS2) + HarmonyX
- **Build target:** Godot 4.5.1 / STS2

### Prerequisites

- [.NET 9.0 SDK](https://dot.net)
- [GUMM](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) installed in STS2
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
| `SlayTheStatsConfig.cs` | Mod settings (OnlyHighestWonAscension, GroupCardUpgrades, ColorBlindMode) |
| `ModConfigBridge.cs` | Optional ModConfig-STS2 integration for in-game settings UI |
| `StatsLogger.cs` | Debug utility — logs per-act stat tables to the Godot log |

### How Stats Are Tracked

Stats are keyed by a context string: `character|ascension|act|gameMode|buildVersion`. The DB is a flat dictionary of these composite keys to `CardStat` counters. This makes aggregation over any subset of dimensions a simple filter-and-sum pass.

Run files (`.run`) are parsed from the STS2 run history directory. Each run is parsed once; already-processed runs are skipped. If the mod version changes, all runs are reprocessed from scratch.

Pick rate is only incremented on fight reward screens (`card_choices` entries on monster/elite/boss floors). Shop inventory browsing, events, and other sources affect win rate only.

### Resources

- [STS2 Hello World mod guide](https://github.com/giulianoconte/slay-the-spire-2-mod-guide)
- [Mod template](https://github.com/Alchyr/ModTemplate-StS2)
- [BaseLib + wiki](https://github.com/Alchyr/BaseLib-StS2) / [wiki](https://alchyr.github.io/BaseLib-Wiki/)
