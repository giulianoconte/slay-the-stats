# SlayTheStats

A Slay the Spire 2 mod that tracks card pick rates and win rates across runs, broken out by act, character, and ascension.

## Installation

1. Install [GUMM (Godot Universal Mod Manager)](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) in STS2
2. Copy `SlayTheStats.dll` and `SlayTheStats.json` into your STS2 mods folder

## How It Works

Stats are calculated in two situations:
- **On startup** — if the mod version has changed since the last run, all run history is reprocessed
- **After a run** — when you return to the main menu after finishing or abandoning a run

Check the Godot log for `[SlayTheStats]` entries showing per-card stats tables.

Log location:
- **Windows:** `%APPDATA%\Godot\app_userdata\Slay the Spire 2\logs\godot.log`
- **Linux:** `~/.local/share/godot/app_userdata/Slay the Spire 2/logs/godot.log`

Stats are saved to `slay-the-stats.json` in:
- **Windows:** `%APPDATA%\SlayTheSpire2\`
- **Linux:** `~/.local/share/SlayTheSpire2\`

## Building from Source

Built on Linux, tested on Windows and Linux.

### Prerequisites

- [.NET 9.0 SDK](https://dot.net)
- [GUMM (Godot Universal Mod Manager)](https://sts2mods.com/godot-universal-mod-manager-for-sts-2/) installed in STS2
- `sts2.dll` and `0Harmony.dll` copied from your STS2 install into `SlayTheStats/libs/`
  - Windows path: `steamapps\common\Slay the Spire 2\`
  - These are build references only and are not shipped with the mod

### Setup

1. Copy `SlayTheStats/local.props.example` to `SlayTheStats/local.props`
2. Set `ModsPath` to your STS2 mods directory

Without `local.props`, build output is placed in `SlayTheStats/dist/` and you copy it to your mods folder manually.

### Building

```bash
./deploy.sh
```

This compiles the mod and copies the DLL and manifest to `ModsPath` (your mods folder if `local.props` is configured, otherwise `dist/`).

### Running Tests

```bash
cd SlayTheStats.Tests
dotnet test
```

## Resources

- [STS2 Hello World mod guide](https://github.com/giulianoconte/slay-the-spire-2-mod-guide)
- [Mod template](https://github.com/Alchyr/ModTemplate-StS2)
- [BaseLib + wiki](https://github.com/Alchyr/BaseLib-StS2) / [wiki](https://alchyr.github.io/BaseLib-Wiki/)
- Official Slay the Spire Discord
