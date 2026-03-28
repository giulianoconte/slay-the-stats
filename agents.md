# Agent Notes

## Mod Version
Bump `CurrentModVersion` in `CardStats.cs` for any code or schema change that affects stat calculation or output. This is the single trigger for reprocessing all run history on next game load.

## Keeping Docs Up To Date
`README.md` and `agents.md` should stay current. If build steps, testing, project structure, or deployment change, update both files as part of the same change. Don't leave them describing the old setup.

## Deployment
Building the project auto-deploys the DLL and `SlayTheStats.json` to the mods folder via the `CopyToModsFolderOnBuild` MSBuild target. No manual deploy step needed.

## Project Structure
- `CardStats.cs` — data model (`CardStat`, `RunContext`, `StatsDb`). No Godot dependency; safe to reference from tests.
- `RunParser.cs` — reads `.run` history files and populates `StatsDb`.
- `StatsLogger.cs` — logs per-card stats tables to the game log.
- `MainFile.cs` — mod entry point, owns `SavePath`.
- `Patches.cs` — Harmony patch to trigger run processing on return to main menu.
- `SlayTheStats.Tests/` — xUnit tests, compiles `CardStats.cs` directly.

## Stats JSON
Saved to `slay-the-stats.json` in Godot's user data dir (`AppData\Roaming\SlayTheSpire2\` on Windows).
Structure: `cards[cardId][contextKey] -> CardStat`, where `contextKey` is `"CHARACTER|ascension|act|gameMode|buildVersion"`.
Upgraded cards use `+` suffix on the card ID (e.g. `CARD.SETUP_STRIKE+`).

