# Starship Uplink

Starship Uplink is a Windows mod launcher and package manager for Starship EVO. It installs mods from ZIP packages and GitHub releases, manages which mods load in a profile, and supports version pinning and rollback.

Uplink provisions BepInEx and ships with [Starship Keel](https://github.com/MadTheDog/Starship-Keel), the in-game framework for registering mod content and preserving its IDs.

## Install and use

1. Install the Windows x64 **.NET 8 Desktop Runtime**.
2. Download `Starship-Uplink-0.1.0-win-x64.zip` from [Releases](https://github.com/MadTheDog/Starship-Uplink/releases) and extract the whole archive to a writable folder.
3. Run **Starship Uplink.exe**. Create a profile and choose a clean Starship EVO game directory. Each profile owns one game copy.
4. Select **Prepare game**. Uplink downloads and checks the official BepInEx archive, then installs the bundled Keel runtime.
5. Import a mod ZIP or choose **Add from GitHub**. Select the package version, install it, and use **Validate** before **Play**.

Profiles select plugins. Save isolation requires a separate Unity data path. Keep backups when testing experimental content. Uplink preserves files it does not own and refuses to overwrite an unmanaged loader. Use **Remove runtime** to remove the bootstrap and framework files installed by Uplink.

The launcher was developed against Windows x64 Mono, **Steam build 26451593 / 26w45a**, Unity **6000.6.6f1**. Keel checks the game contracts it uses at startup. The build ID records the test target.

## Build from source

Install the .NET 8 SDK and run these commands from the repository root:

```powershell
dotnet publish src/Sevo.Mod.Launcher/Sevo.Mod.Launcher.csproj -c Release -o artifacts/uplink
dotnet publish src/Sevo.Mod.Cli/Sevo.Mod.Cli.csproj -c Release -o artifacts/uplink/cli
```

For **Prepare game**, create `artifacts/uplink/framework` and put `Sevo.Framework.Runtime.dll` and `Sevo.Framework.Sdk.dll` from the matching [Keel release](https://github.com/MadTheDog/Starship-Keel/releases) there. `KEEL-RELEASE.txt` in the Uplink download identifies its bundled version and source commit.

The launcher and command line share the same package manager. Run `cli/Sevo.Mod.Cli.exe --help` for commands and use `--root` to select the same workspace as the launcher. The launcher stores its workspace in the `data` folder by default and accepts `--workspace` to open an existing workspace at its current path. See the [package format](docs/PACKAGES.md).

Starship Uplink is an unofficial community project.
