# Changelog

## 0.2.2

- Added **Game tools → Reset to factory settings**. Removes all installed mods (including manual plugins), mod settings, BepInEx and loader dependencies, then installs a verified fresh runtime.
- Reset tolerates missing/modified mod files and invalid old runtime records, and can be retried after interruption. It remains reachable when normal deployment recovery fails.
- Preserves base game files, saves, game settings, other profiles and the shared package library. Replacement downloads are verified before deletion.
- Shows the Steam build for the selected installation. Modded launch and reset warn for unsupported or unverified builds. The supported baseline is 26w45A (Steam build 26451593).
- Bundles Starship Keel 0.2.3.

## 0.2.1

- Added **Play vanilla** to bypass Doorstop and BepInEx mods for one launch while keeping the selected profile and using the game's usual saves and settings.
- Added **Remove all from profile** under Installed Mods. It lists the mods for confirmation before removing enabled and disabled entries from the selected profile.

## 0.2.0

- **Prepare game** checks the latest stable BepInEx 5.4 Windows x64 release for a fresh install and verifies its official ZIP.
- Existing loader installations can be reused when their required files match their own verified release. Working 5.4.23.5 profiles keep their loader.
- Preparation reports the required BepInEx configuration setting directly when an existing config needs attention.

## 0.1.9

- Gave Activity a separate heading and scrollable area so the log stays readable at the minimum window size.
- Added a version box for Uplink and the selected profile's Keel runtime.
- Kept **Update Uplink** and **Update Keel** visible as separate controls. The Keel status explains when a profile needs preparation or uses a manually managed installation.

## 0.1.8

- Added **Check all for updates** and an **Update** column to Installed Mods. The check reads GitHub release metadata and leaves downloads and installation to **Review updates**.
- Uplink can upgrade in place when its bundled Keel DLLs were replaced with a verified official Keel release. The original files remain in the rollback backup.
- **Update Uplink** now goes directly to the upgrade confirmation and restarts with the existing workspace.

## 0.1.7

- Added a resizable sidebar workspace with separate pages for **Installed mods**, **Package library**, and **Modpacks**. Profile maintenance is grouped under **Game tools**.
- Added checkbox selection for batch installs, enable and disable, update review, pinning, rollback, and removal.
- Added modpack export and import across GitHub repositories. Packs record exact release assets, SHA-256 checksums, and enabled states. Import previews the merge and validates the resulting profile before deployment.
- Added the Landing Port icon to the Windows executable and launcher window.
- Bundled Starship Keel 0.1.9 for new installations.

## 0.1.6

- Added **Keel update available** beside **Update Uplink**. Select a prepared profile to check for and install newer releases of its Uplink-managed Starship Keel runtime.
- The confirmation names the game folder and identifies Keel previews. Uplink verifies the official ZIP before changing the two managed Keel DLLs. An interrupted update resumes through **Prepare game** or **Play**.
- Bundled Starship Keel 0.1.7 Preview for new installations.
