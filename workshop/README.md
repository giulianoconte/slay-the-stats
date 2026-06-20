# Steam Workshop publishing

Version-controlled config for publishing this mod to the Steam Workshop via the
official uploader (`github.com/megacrit/sts2-mod-uploader`, `ModUploader.exe`).

## Files here (tracked)
- `workshop.json` — Workshop item metadata (title, description, visibility, deps,
  branch range). Edit fields here; `null`/omitted fields stay unchanged on
  re-upload. `visibility` starts `"private"` — flip to `"public"` once verified.
- `image.png` — Workshop thumbnail. **You must add this** (not committed yet);
  reuse the mod's Nexus thumbnail or make one.
- `mod_id.txt` — the Workshop item identity, written by the uploader on the FIRST
  publish. Commit it: updates reuse it, so losing it would create a duplicate item.
- `content/` — **generated, gitignored.** The built mod files (`.json`/`.pck`/
  `.dll`) that get uploaded. Produced by `workshop-stage`; never hand-edit.

## Workflow
The build runs on the Linux VM; the upload must run on the Windows host (Steam).

1. On the VM, from the mod's source tree:  `workshop-stage`
   — release-builds the mod and assembles the full workspace under the staging
   share so the host can see it.
2. On the Windows host:  `ModUploader.exe upload -w <staged-workspace>`
3. First publish only: copy the generated `mod_id.txt` back here and commit it.

## Notes
- `dependencies` = Steam Workshop **item IDs** (e.g. BaseLib's) for auto-subscribe
  — distinct from the runtime dependency in the mod's manifest JSON. Fill in
  BaseLib's Workshop ID once known; the manifest already enforces the runtime dep.
- `minBranch`/`maxBranch` are `null` (all versions) while the game's main and beta
  branches are merged (0.107.1). When beta re-diverges, ship a branch-specific
  version per branch (separate uploader run, same item) or set the range on Steam.
