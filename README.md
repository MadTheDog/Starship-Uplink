# Starship Uplink

Starship Uplink is a Windows mod launcher and package manager for Starship EVO. It installs mods from ZIP packages and GitHub releases, manages which mods load in a profile, and supports version pinning and rollback.

Uplink provisions BepInEx and ships with [Starship Keel v0.1.5](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.5), the in-game framework for registering mod content and preserving its IDs.

## Install and use

1. Install the Windows x64 **.NET 8 Desktop Runtime**.
2. Download the latest Windows ZIP from [Releases](https://github.com/MadTheDog/Starship-Uplink/releases) and extract the whole archive to a writable folder.
3. Run **Starship Uplink.exe**. Create a profile and select the folder containing `Starship EVO.exe`. Each profile owns one game copy.
4. Select **Prepare game**. Uplink verifies the official BepInEx archive and reuses an existing installation when its loader DLLs and Doorstop configuration match. It installs the bundled Keel runtime or reuses matching manually installed Keel DLLs.
5. Import a mod ZIP or choose **Add from GitHub**. Select the package version, install it, and use **Validate** before **Play**.

Profiles select plugins. Save isolation requires a separate Unity data path. Keep backups when testing experimental content. If Uplink reports a missing or different loader file, use a clean game copy or repair the external loader before preparing it. Uplink supports BepInEx 5.4.23.5 for Windows x64 Mono with `HideManagerGameObject = true` under `[Chainloader]` in `BepInEx.cfg`. **Remove runtime** removes the files Uplink installed and preserves verified external files.

## Update or clean up

**Update Uplink** checks the stable GitHub release. If a newer version is available, you can install it in place and keep your profiles, or clean a selected game folder. Uplink verifies the downloaded ZIP, backs up the old launcher files, and restarts with the same workspace. The updater uses the release ZIP layout and a writable launcher directory.

**Clean game** is also available directly, including when the old workspace was lost. Close the selected game and review the confirmation before continuing. Uplink releases the profile's game claim and archives its deployment and owned runtime files under `BepInEx/SevoCleanupBackups`, outside the plugin scan path. The package library, saves, configuration, and manually installed files are preserved. For a game still claimed by an accessible workspace, open that workspace to clean it.

To install this version over Uplink 0.1.1 or earlier, close Uplink and copy the contents of the new ZIP's launcher folder into the existing Uplink folder. Keep the `data` workspace at its existing path.

To upgrade Keel in an already prepared game profile, update Uplink first and close Starship EVO. Select the profile and choose **Remove runtime** to clear its previous runtime record. If you installed Keel manually, replace its SDK and runtime DLLs together with the pair from the [Keel release](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.5). Then choose **Prepare game** to install the bundled Keel or verify the manually installed pair. This keeps the profile, content ID registry, installed mods, and saves.

The launcher was developed against Windows x64 Mono, **Steam build 26451593 / 26w45a**, Unity **6000.6.6f1**. Keel checks the game contracts it uses at startup. The build ID records the test target.

## Build from source

Install the .NET 8 SDK and run these commands from the repository root:

```powershell
dotnet publish src/Sevo.Mod.Launcher/Sevo.Mod.Launcher.csproj -c Release -o artifacts/uplink
dotnet publish src/Sevo.Mod.Cli/Sevo.Mod.Cli.csproj -c Release -o artifacts/uplink/cli
```

For **Prepare game**, create `artifacts/uplink/framework` and put `Sevo.Framework.Runtime.dll` and `Sevo.Framework.Sdk.dll` from the [Keel release pinned by `BundledKeelVersion`](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.5) there. `KEEL-RELEASE.txt` in the Uplink download identifies its bundled version and source commit.

To build the update-compatible ZIP from a clean Git checkout, use PowerShell 7 and run `./tools/Build-UplinkRelease.ps1 -KeelDirectory <extracted-keel-release>`. The script writes the ZIP and its SHA-256 under `artifacts/releases`.

The launcher and command line share the same package manager. Run `cli/Sevo.Mod.Cli.exe --help` for commands and use `--root` to select the same workspace as the launcher. The launcher stores its workspace in the `data` folder by default and accepts `--workspace` to open an existing workspace at its current path. See the [package format](docs/PACKAGES.md).

Starship Uplink is an unofficial community project.
