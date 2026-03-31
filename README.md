# SlayTheStats

TODO: image

A Slay the Spire 2 mod that tracks card and relic stats across your runs.

---

## For Players

### What It Does

Every time you finish or abandon a run, SlayTheStats updates your personal stats:

- **Pick%** — how often you pick a card when it's offered on a fight reward screen
- **Win%** — how often you win runs that include a given card or relic
- **Picks** — shown as `present/offered` (e.g. `12/30`): runs the card was in your final deck / runs it appeared on a fight reward screen. For relics, just the number of runs it was present.

Stats are shown as a tooltip when you hover over cards and relics. Stats are broken out by act acquired.

**Color coding** helps you read the data at a glance:
- Pick% and Win% are colored relative to your personal baseline — your overall win rate for the current character, and your average pick rate across all fight reward screens (accounting for how often you skip). Green/teal means above baseline, orange/red means below.
- Color intensity scales with evidence: a small sample barely shifts the color; a large sample at the same deviation shows vivid color.
- A color-blind mode (teal/orange instead of green/red) is available in settings.

### Installation

1. Install [GUMM (Godot Universal Mod Manager)](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) in STS2
2. Download `SlayTheStats-vX.X.X.zip` from the releases page and extract it into your STS2 mods folder — the result should be a `SlayTheStats/` folder inside `mods/` containing `SlayTheStats.dll`, `SlayTheStats.json`, and a `fonts/` subfolder

### Understanding the Stats

**Pick%** is sourced only from fight reward screens — the 3-card choice after defeating an enemy. Shop purchases, event cards, ancient (Neow) rewards, and other acquisition sources are not counted. Some relics modify reward screens (e.g. adding an extra card or replacing choices); those modified screens are also excluded since the pool is no longer a standard 3-card offer.

**Win%** counts all runs in which the card was present in your final deck, from any source.

**Picks** shows `present/offered` for cards (e.g. `12/30` — present in 12 runs, offered on a fight reward screen in 30) and just the present count for relics.

**Upgraded cards** (e.g. Tremble+) are tracked separately from each other by default. This can be changed in the settings.

> **Note (split upgrades mode):** When upgrades are tracked separately, the Picks column for an upgraded card (e.g. Tremble+) can show a higher "present" count than "offered" count — for example `5/1`. This happens because "present" counts all runs the card ended up in your deck from any source (including upgrading the base card at a campfire or event), while "offered" only counts fight reward screens where the card appeared already upgraded. Pick% is similarly unreliable for upgraded cards in this mode. If you find this confusing, enable **Group card upgrades** in settings to merge base and upgraded stats together.

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
- **Build target:** Godot 4 / STS2

### Prerequisites

- [.NET 9.0 SDK](https://dot.net)
- [GUMM](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) installed in STS2
- `sts2.dll` and `0Harmony.dll` copied from your STS2 install into `SlayTheStats/libs/`
  - Windows path: `steamapps\common\Slay the Spire 2\`
  - These are build references only — not shipped with the mod

### Setup

1. Copy `SlayTheStats/local.props.example` to `SlayTheStats/local.props`
2. Set `ModsPath` to your STS2 mods directory

Without `local.props`, build output goes to `SlayTheStats/dist/` and you need to copy it manually.

### Building

```bash
./deploy.sh
```

Compiles the mod and copies the DLL and manifest to `ModsPath` (or `dist/` if not configured).

To create a distributable release archive:

```bash
./deploy.sh --release
```

Builds with Release configuration and produces `SlayTheStats-vX.X.X.zip` next to `deploy.sh`, ready for upload to Nexus and attaching to GitHub releases.

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
| `StatsDb.cs` | In-memory stats database, serialized to `slay-the-stats.json` |
| `StatsAggregator.cs` | Aggregates raw per-context stats into per-act totals for display |
| `CardStats.cs` / `RelicStats.cs` | Data models for card and relic stat entries |
| `CardStatsTooltipPatch.cs` | Harmony patches for card hover tooltips |
| `RelicStatsTooltipPatch.cs` | Harmony patches for relic hover tooltips |
| `TooltipHelper.cs` | Shared tooltip panel management |
| `Patches.cs` | Additional Harmony patches (run-end hook, character tracking) |

### How Stats Are Tracked

Stats are keyed by a context string: `character|ascension|act|gameMode|buildVersion`. The DB is a flat dictionary of these composite keys to `CardStat` counters. This makes aggregation over any subset of dimensions a simple filter-and-sum pass.

Run files (`.run`) are parsed from the STS2 run history directory. Each run is parsed once; already-processed runs are skipped. If the mod version changes, all runs are reprocessed from scratch.

Pick rate is only incremented on fight reward screens (`card_choices` entries on monster/elite/boss floors). Shop inventory browsing, events, and other sources affect win rate only.

### Resources

- [STS2 Hello World mod guide](https://github.com/giulianoconte/slay-the-spire-2-mod-guide)
- [Mod template](https://github.com/Alchyr/ModTemplate-StS2)
- [BaseLib + wiki](https://github.com/Alchyr/BaseLib-StS2) / [wiki](https://alchyr.github.io/BaseLib-Wiki/)
