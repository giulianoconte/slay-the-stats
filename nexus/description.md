# SlayTheStats

A Slay the Spire 2 mod that tracks card and relic stats across your runs and shows them as tooltips when you hover over them.

Supports [ModConfig-STS2](https://www.nexusmods.com/slaythespire2/mods/2).

---

## What It Does

SlayTheStats reads your full run history retroactively. Stats are updated when you finish a run or start the game. Abandoned runs are skipped.

- **Pick%** — how often you pick a card when it's offered on as a fight reward.
- **Win%** — how often you win runs that include a given card or relic.
- **Picks** — shown as a fraction of runs the card was in your final deck / runs it appeared on a fight reward screen (e.g. `12/30`). For relics, just the number of runs it was present.
- **Buys** — for colorless cards and relics: Shown as a fraction of runs you purchased the item from a shop / runs it appeared in a shop (e.g. `12/30`).

Stats are shown as a tooltip when you hover over cards and relics during a run, in shops, and in the compendium.

**Color coding** helps you read the data at a glance:
- Pick% and Win% are colored relative to your personal baseline — your overall win rate for the current character, and your average pick rate across all fight reward screens.
- Color intensity scales with both sample size and deviation from baseline — muted with little evidence, vivid when a strong trend holds across many runs.
- Color intensity is sensitive to both sample size and deviation, so a high winrate over a few runs can be treated similar to a moderately high winrate across many runs.

---

## Requirements

- [GUMM (Godot Universal Mod Manager)](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) installed for STS2.
- [BaseLib](https://www.nexusmods.com/slaythespire2/mods/103) v0.2.1 or later.

---

## Installation

1. Install [GUMM](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) for STS2 if you haven't already
2. Download and extract [BaseLib](https://www.nexusmods.com/slaythespire2/mods/103) (v0.2.1 or later) into your mods folder if you haven't already
3. Download `SlayTheStats-v0.1.0.zip` from the Files tab and extract it into your mods folder — you should end up with a `SlayTheStats/` folder inside `mods/` containing `SlayTheStats.dll`, `SlayTheStats.json`, `SlayTheStats.pck`, and a `fonts/` subfolder

---

## Notes

**Pick%** is sourced only from fight rewards. Shop purchases, event cards, and other sources are not counted.

**Win%** counts all runs in which the card or relic was present in your final build, from any source.

**Shop purchases (Buys):** For colorless cards and relics, the tooltip shows how often you purchased an item relative to how often it appeared in a shop, colored relative to your overall shop buy rate. Note that appearances are counted regardless of whether you could afford the item.

**GroupCardUpgrades**: **Upgraded cards** (e.g. Coolheaded+) can be tracked separately or grouped with their unupgraded counterparts.

**OnlyHighestWonAscension**: by default stats include all your runs. Enable this setting to filter each character's stats to their highest won ascension only. For example, if you're currently trying to beat ascension 8, you will be shown stats for ascension 7. However you will have less data to draw from.

**Colorless Cards**, **Event Cards**, **Ancient Relics**, **Curses**, etc are all tracked.

Settings can be configured in-game via [ModConfig](https://www.nexusmods.com/slaythespire2/mods/2) if installed (optional).

---

## Accessibility

**ColorBlindMode** (teal/orange instead of green/red) is available in settings.

---

## Save Data

Stats are stored in `slay-the-stats.json`:
- **Windows:** `%APPDATA%\Slay the Spire 2\`
- **Linux:** `~/.local/share/Slay the Spire 2/`

---

## FAQ

**Why does a card show more "present" runs than "offered" runs (e.g. 5/1)?**
With **GroupCardUpgrades** off (the default), upgraded and base versions are tracked separately. "Present" counts all runs the card ended up in your deck from any source — including upgrading the base at a campfire — while "offered" only counts fight reward screens. Enable **GroupCardUpgrades** to merge them. Starter cards and relics (those you begin a run with) will always show 0 for "offered" since they never appear on fight reward screens.

**Why is my Win% 100% but the stat is grey?**
Color intensity reflects both sample size and magnitude of deviation from your baseline. With very few runs, even a perfect rate stays muted — the color intensifies as evidence builds up.

**Why don't I see stats for a card I've used?**
Check your settings: if **OnlyHighestWonAscension** is enabled, runs at other ascension levels are hidden, which can make data sparse or absent if most of your runs were at a different level. You may also just need more runs with the card.

---

## Troubleshooting

Check the Godot log for `[SlayTheStats]` entries (close the game first):
- **Windows:** `%APPDATA%\Godot\app_userdata\Slay the Spire 2\logs\godot.log`
- **Linux:** `~/.local/share/godot/app_userdata/Slay the Spire 2/logs/godot.log`
