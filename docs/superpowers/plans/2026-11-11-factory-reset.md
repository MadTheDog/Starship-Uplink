# Uplink factory reset

User request: add **Reset to factory settings**, forcibly removing mods, BepInEx and loader dependencies and reinstalling them, without modifying the base game, saves or game settings. Missing files, changed hashes and old runtime records must not prevent reset.

## Design

Use a separate destructive reset path; ordinary preparation and cleanup retain their existing contracts. Target the selected profile's installation only. Remove the complete `BepInEx` tree (including manual plugins and mod settings), `doorstop_libs`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and Uplink runtime/update records. Clear the selected profile's mod selection and runtime state, retaining its identity/name, other profiles and the shared package library. Never enumerate the game data, saves or game settings for deletion. Detach filesystem links instead of following them.

Resolve and validate fresh replacement payloads before deletion; ignore the installed BepInEx version during discovery. Bundle latest verified Keel 0.2.3. Reset uses game/workspace/runtime locks, refuses a running game, and writes a reset marker that blocks normal mutation until completion. Retrying reset always starts the same bounded cleanup and installation again. Do not trust deletion paths or hashes from old installation manifests. Missing/modified/corrupt runtime records are simply discarded. OS permission or sharing failures remain real failures and tell the player to retry.

The existing **Clean game** is unsuitable: it preserves manual mods and requires valid ownership/inventories. Weakening normal preparation would silently destroy installations. A dedicated confirmed command expresses the requested destructive intent.

## Implementation plan

- [ ] Add real filesystem regressions reproducing deletion of BepInEx, altered/malformed runtime records, and interrupted reset. Verify the missing reset API causes failure first.
- [ ] Implement selected-profile reset helpers and fixed-target cleanup/provisioning. Test preservation of game/settings/other profiles/cache, running-game refusal, invalid payload preflight, read-only files and links.
- [ ] Add **Reset to factory settings** to Game tools with explicit selected path, removal scope and preservation text. Download/verify before invoking reset; report failure/retry clearly.
- [ ] Update version to 0.2.2, bundle Keel 0.2.3, document use, run complete core/hosting suites and Release build.
- [ ] Build a local installable ZIP and verify the packaged reset with real BepInEx/Keel in an isolated fixture. Review without publishing or changing the Steam installation.

## Evidence boundaries

Latest GitHub releases verified 2026-11-11: Uplink 0.2.1, Keel 0.2.3. Steam app-info: newest Unity 6 build 26451593; installed manifest matches; existing version evidence identifies 26w45a. Reset fixture tests do not establish game startup or fix the original ToHashSet failure.
