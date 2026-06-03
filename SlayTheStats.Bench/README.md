# SlayTheStats.Bench

Standalone harness that times `RunParser.ProcessNewRuns` over a real data folder,
producing a `slay-the-stats.json` — the same full pipeline the mod runs at launch
(history scan → per-run parse → aggregate → serialize/save). Its purpose is a
**baseline to compare logic changes against** (e.g. the #6 run-id refactor): run
it now, run it again after the change, diff the numbers.

It compiles the same Godot-free core files as `SlayTheStats.Tests` (per-file
`Compile Include`, no Godot SDK) plus that project's config/loc stubs, so
`SlayTheStatsConfig.DataDirectory` and `L.*` resolve.

## Usage

```bash
dotnet run -c Release --project SlayTheStats.Bench -- [dataDir] [iterations]
```

- `dataDir` — parent of `steam/` (the harness appends `steam`). Defaults to the
  live appdata mount `/media/sf_sts2-appdata`.
- `iterations` — default 5. Iteration 1 is discarded as warmup (JIT + cold cache).
  A forced GC precedes each timed run so a prior iteration's garbage doesn't
  collect mid-measurement (this workload allocates heavily via `JsonNode`).

**Run against a local copy, not the vboxsf mount**, for stable timing — the
shared folder's IO variance swamps the compute signal, and production reads from
a native FS anyway:

```bash
rm -rf /tmp/sts-bench-data && mkdir -p /tmp/sts-bench-data
cp -r /media/sf_sts2-appdata/steam /tmp/sts-bench-data/steam
dotnet run -c Release --project SlayTheStats.Bench -- /tmp/sts-bench-data 12
```

The headline metric is **MIN** (least scheduler/GC interference = cleanest
compute floor; a logic change that adds real work raises the min too). Median is
reported for context. This VM is noisy run-to-run; trust the min.

## Baseline — 2026-06-02, schema v8 (#4 + #5 logic in place)

Data: theshoe124's appdata, 401 `.run` files of which **283 valid / 118 empty**
(the empties are zero-byte files — the known empty-save / Steam-Cloud-race
symptom; the parser skips them with a warning, which the harness tallies).

| Metric | Value |
|---|---|
| Runs processed | 283 |
| Output JSON | ~8.5 MB |
| Time (min) | **~124 ms** |
| Per run (min) | ~0.44 ms/run |

Compare future runs against the **min**, same data folder and schema-comparable
state, ideally on the same machine.
