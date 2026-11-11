# Starship Uplink

Starship Uplink is a Windows mod launcher and package manager for Starship EVO. It installs mods from ZIP packages and GitHub releases, manages which mods load in a profile, and supports version pinning and rollback.

Uplink provisions BepInEx and ships with [Starship Keel v0.1.9](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.9), the in-game framework for registering mod content and preserving its IDs.

## Install and use

1. Install the Windows x64 **.NET 8 Desktop Runtime**.
2. Download the latest Windows ZIP from [Releases](https://github.com/MadTheDog/Starship-Uplink/releases) and extract the whole archive to a writable folder.
3. Run **Starship Uplink.exe**. Create a profile and select the folder containing `Starship EVO.exe`. Each profile owns one game copy.
4. Select **Prepare game**. Uplink verifies the official BepInEx archive and reuses an existing installation when its loader DLLs and Doorstop configuration match. It installs the bundled Keel runtime or reuses matching manually installed Keel DLLs.
5. Open **Package library** to import ZIPs or choose **Add from GitHub**. Tick the packages you want and choose **Install selected**. Use **Game tools → Validate profile** before **Play**.

Profiles select plugins. Save isolation requires a separate Unity data path. Keep backups when testing experimental content. If Uplink reports a missing or different loader file, use a clean game copy or repair the external loader before preparing it. For a fresh game copy, Uplink checks the latest stable BepInEx 5.4 Windows x64 release and verifies its ZIP against the published SHA-256. Existing loader installations are checked against their own official release and reused when the required files match. Keel requires `HideManagerGameObject = true` under `[Chainloader]` in `BepInEx.cfg`. **Remove runtime** removes the files Uplink installed and preserves verified external files.

Select a version in **Package library** and choose **Remove from library** to delete its cached ZIP and payload. Uplink protects versions installed in any profile, including disabled mods, and versions reserved for rollback.

In **Installed mods**, **Check all for updates** reads GitHub release metadata for every mod in the selected profile and fills the **Update** column. It does not download or install ZIPs. **Review release** means the release tag or asset choice needs review before Uplink can tell which package version is newer.

In **Installed mods**, tick one or more rows to enable, disable, pin, unpin, roll back, or remove them. **Review updates** checks the selected mods and shows the eligible updates and skipped entries. Applying the updates keeps each mod's enabled state, clears its rollback slot, and prunes older cached versions that no profile uses. Switching versions with **Install selected** still records a local rollback.

In **Modpacks**, choose **Export profile** to share the installed versions and enabled states as a JSON file. Uplink matches each cached ZIP against its published GitHub release, so a pack can include mods from different repositories. A package with no matching published asset and SHA-256 cannot be exported. Choose **Import modpack** to review and merge a pack into the selected profile. Uplink verifies the exact ZIPs and validates the full profile before deploying it. Other mods in the profile are kept, and downloaded packages can remain in the library if deployment is refused.

## Update or clean up

**Game tools → Reset to factory settings** repairs a broken mod installation, including when the entire BepInEx folder was deleted or runtime files have changed. Select the affected profile, close its game, choose the command and confirm the displayed game path. Uplink downloads and verifies a fresh BepInEx release before removing anything, then reinstalls BepInEx and bundled Keel. Internet access is required for fresh runtime discovery/download.

Reset permanently removes the selected installation's entire `BepInEx` folder, including **all manual and managed mods, mod settings, logs and cleanup backups**, plus `doorstop_libs`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, Uplink runtime/update records and that profile's runtime data. It clears the profile's installed-mod selection. Old-file hashes, missing files and corrupt runtime ownership records do not block it. It keeps the **base game, saves, game settings, other profiles and shared package library**. It cannot restore game files previously replaced by a mod; use Steam verification separately if needed.

If reset is interrupted or Windows reports a locked/inaccessible file, close the game, resolve permissions and run the same command again. An incomplete reset blocks ordinary runtime preparation/mod deployment. After completion choose **Play modded** to check Keel startup, then reinstall desired mods from the library. Successful reset establishes installation integrity; it does not establish compatibility of every mod or game runtime.

**Update Uplink** checks the stable GitHub release. If a newer version is available, confirm the in-place upgrade. Uplink verifies the downloaded ZIP, backs up the old launcher files, and restarts with your existing workspace. The updater uses the release ZIP layout and a writable launcher directory. If the bundled Keel runtime and SDK were replaced, the updater verifies both against their exact official Keel release and keeps them in the rollback backup. The incoming Uplink package must include that Keel version or a newer one.

If an older Uplink refuses to update because its bundled Keel files changed, extract the new Uplink ZIP into a separate folder and start its executable with `--workspace "absolute path to your existing Uplink data folder"`. Keep that workspace in place. The older executable performs its own checks before it can install this fix.

**Game tools → Clean game** is also available directly, including when the old workspace was lost. Close the selected game and review the confirmation before continuing. Uplink releases the profile's game claim and archives its deployment and owned runtime files under `BepInEx/SevoCleanupBackups`, outside the plugin scan path. The package library, saves, configuration, and manually installed files are preserved. For a game still claimed by an accessible workspace, open that workspace to clean it.

To install this version over Uplink 0.1.1 or earlier, close Uplink and copy the contents of the new ZIP's launcher folder into the existing Uplink folder. Keep the `data` workspace at its existing path.

The version box shows the running Uplink version and the Keel version in the selected game profile. An unprepared profile shows the bundled Keel version separately. **Update Uplink** updates the launcher, and **Update Keel** checks and updates the selected profile independently. If **Update Keel** is unavailable, the version box explains whether to select or prepare a profile, resume an interrupted update, or manage a manual installation.

Close Starship EVO before applying a Keel update. Uplink verifies the official BepInEx ZIP and replaces the two Keel DLLs it manages for the selected profile. The confirmation labels preview releases.

If the update is interrupted, close the game and choose **Prepare game** or **Play** to finish it. Uplink leaves manually installed Keel alone; update both DLLs from the [Keel release](https://github.com/MadTheDog/Starship-Keel/releases) manually, then prepare the runtime again after removing Uplink's old runtime record with **Remove runtime**.

The launcher was developed against Windows x64 Mono, **Steam build 26451593 / 26w45a**, Unity **6000.6.6f1**. Keel checks the game contracts it uses at startup. The build ID records the test target.

## Build from source

Install the .NET 8 SDK and run these commands from the repository root:

```powershell
dotnet publish src/Sevo.Mod.Launcher/Sevo.Mod.Launcher.csproj -c Release -o artifacts/uplink
dotnet publish src/Sevo.Mod.Cli/Sevo.Mod.Cli.csproj -c Release -o artifacts/uplink/cli
```

For **Prepare game**, create `artifacts/uplink/framework` and put `Sevo.Framework.Runtime.dll` and `Sevo.Framework.Sdk.dll` from the [Keel release pinned by `BundledKeelVersion`](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.9) there. `KEEL-RELEASE.txt` in the Uplink download identifies its bundled version and source commit.

To build the update-compatible ZIP from a clean Git checkout, use PowerShell 7 and run `./tools/Build-UplinkRelease.ps1 -KeelDirectory <extracted-keel-release>`. The script writes the ZIP and its SHA-256 under `artifacts/releases`.

The launcher and command line share the same package manager. Run `cli/Sevo.Mod.Cli.exe --help` for commands and use `--root` to select the same workspace as the launcher. The launcher stores its workspace in the `data` folder by default and accepts `--workspace` to open an existing workspace at its current path. See the [package format](docs/PACKAGES.md).

Starship Uplink is an unofficial community project.
