using System.Diagnostics;
using System.Text.Json;
using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

namespace Sevo.Mod.Launcher;

public sealed partial class MainForm
{
    private string State(ProfileRecord profile) => Path.Combine(dataRoot, "runtime", profile.Id);
    private async Task NewProfileAsync()
    {
        var name = Prompts.Text(this, "New profile", "Profile name", "My mods"); if (string.IsNullOrWhiteSpace(name)) return;
        var game = ChooseGameFolder("Choose this profile's Starship EVO game folder. Use a separate game copy for a separate profile.");
        if (game == null) return;
        var profile = await Task.Run(() => service.CreateProfile(name, game));
        if (!CanUseControls) return;
        RefreshViews(); profiles.SelectedItem = profiles.Items.Cast<ProfileChoice>().First(x => x.Id == profile.Id);
        Write("Created profile " + profile.Name + ". Save files remain those of the selected game installation.");
    }
    private static PackageImportOptions? KnownSource(string filename)
    {
        string? repository = filename.Contains("HullFiller", StringComparison.OrdinalIgnoreCase) ? "MadTheDog/StarshipEVO-HullFiller"
            : filename.Contains("Graphical-Tweaks-Unity6", StringComparison.OrdinalIgnoreCase) ? "MadTheDog/StarshipEVO-Graphical-Tweaks-Unity6" : null;
        return repository == null ? null : new PackageImportOptions { Repository = repository, AssetName = filename };
    }
    private async Task ImportAsync()
    {
        using var file = new OpenFileDialog { Filter = "Mod package (*.zip)|*.zip", Multiselect = true, Title = "Import compiled mod packages" };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in file.FileNames)
        {
            if (!CanUseControls) return;
            var record = await Task.Run(() => service.ImportPackage(path, KnownSource(Path.GetFileName(path))));
            Write($"Imported {record.Name} {record.Version} ({record.Sha256[..12]}…).");
        }
    }
    private async Task AddGitHubAsync()
    {
        var repository = Prompts.Text(this, "Add a GitHub release", "Public repository (owner/repository)", "MadTheDog/StarshipEVO-Graphical-Tweaks-Unity6");
        if (string.IsNullOrWhiteSpace(repository)) return;
        repository = repository.Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        Write("Checking published releases for " + repository + "…");
        var client = new GitHubReleaseClient(http); var release = await client.GetLatestAsync(repository);
        if (!CanUseControls) return;
        var assets = release.Assets.Where(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (assets.Count == 0) throw new InvalidOperationException("This release has no compiled ZIP asset. Source archives cannot be installed as mods.");
        var name = Prompts.Choose(this, "Select the package for your game/runtime", assets.Select(x => x.Name)); if (name == null) return;
        var asset = assets.Single(x => x.Name == name); var path = await ReleaseDownloads.DownloadAsync(client, asset, Path.Combine(dataRoot, "downloads"));
        var record = await Task.Run(() => service.ImportPackage(path, new PackageImportOptions { Repository = repository, AssetName = asset.Name }));
        Write($"Imported {record.Name} {record.Version} from {release.PageUrl}.");
    }
    private async Task InstallAsync()
    {
        var profile = Profile(); var row = library.CurrentRow ?? throw new InvalidOperationException("Select a library package first.");
        var id = (string)row.Cells["Id"].Value; var version = (string)row.Cells["Version"].Value;
        await Task.Run(() => service.Install(profile.Id, id, version)); Write($"Installed {id} {version}. Restart the game to apply changes.");
    }
    private async Task ToggleAsync()
    {
        var profile = Profile(); var id = InstalledId(); var mod = profile.Mods.Single(x => x.PackageId == id);
        await Task.Run(() => service.SetEnabled(profile.Id, id, !mod.Enabled)); Write($"{id}: {(mod.Enabled ? "disabled" : "enabled")} for next launch.");
    }
    private async Task PinAsync()
    {
        var profile = Profile(); var id = InstalledId(); var mod = profile.Mods.Single(x => x.PackageId == id);
        await Task.Run(() => service.SetPinned(profile.Id, id, !mod.Pinned)); Write($"{id}: {(mod.Pinned ? "unpinned" : "pinned")}.");
    }
    private async Task RemoveAsync()
    {
        var profile = Profile(); var id = InstalledId(); await Task.Run(() => service.Remove(profile.Id, id)); Write($"Removed {id}; configuration and cached packages were preserved.");
    }
    private async Task RollbackAsync()
    {
        var profile = Profile(); var id = InstalledId(); await Task.Run(() => service.Rollback(profile.Id, id)); Write("Restored the previous package version for " + id + ". World changes are not reverted.");
    }
    private async Task UpdateAsync()
    {
        var profile = Profile(); var id = InstalledId(); var mod = profile.Mods.Single(x => x.PackageId == id);
        if (mod.Pinned) throw new InvalidOperationException("This mod is pinned. Unpin it before updating.");
        var package = service.ListPackages().Single(x => x.Id == id && x.Version == mod.Version);
        if (package.Repository == null) throw new InvalidOperationException("No GitHub update source is recorded. Import this mod using Add from GitHub.");
        var client = new GitHubReleaseClient(http); var release = await client.GetLatestAsync(package.Repository);
        if (!CanUseControls) return;
        if (release.TagName.TrimStart('v', 'V') == mod.Version) { Write(id + " is already on the latest published release."); return; }
        var name = Prompts.Choose(this, $"Available release {release.TagName} — choose compatible ZIP", release.Assets.Where(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name)); if (name == null) return;
        var asset = release.Assets.Single(x => x.Name == name); var path = await ReleaseDownloads.DownloadAsync(client, asset, Path.Combine(dataRoot, "downloads"));
        var updated = await Task.Run(() => service.ImportPackage(path, new PackageImportOptions { Repository = package.Repository, AssetName = name }));
        if (updated.Id != id) throw new InvalidOperationException("The selected asset has a different mod identity. It was cached but not installed.");
        await Task.Run(() => service.Install(profile.Id, id, updated.Version)); Write($"Updated {id} to {updated.Version}; previous version is available through Roll back.");
    }
    private async Task PrepareAsync()
    {
        var profile = Profile(); Write("Preparing the game runtime…");
        var loader = await RuntimeDownloads.EnsureLoaderAsync(http, Path.Combine(dataRoot, "runtime-downloads"), Path.Combine(appRoot, "runtime", RuntimeDownloads.LoaderFileName));
        var framework = Path.Combine(appRoot, "framework");
        if (!Directory.Exists(framework)) throw new InvalidOperationException("Starship Keel is missing. Extract the complete Starship Uplink release, including its framework folder.");
        var result = await Task.Run(() => new RuntimeProvisioner(dataRoot).Prepare(profile.GameDirectory, State(profile), loader, framework));
        Write(result.ReusedLoader
            ? "Verified existing BepInEx. Starship Keel is prepared for the next launch."
            : $"Runtime prepared ({result.FilesInstalled} files). Startup readiness will be checked after launching.");
    }
    private async Task ValidateAsync()
    {
        var profile = Profile(); var errors = await Task.Run(() => service.ValidateProfile(profile.Id));
        Write(errors.Count == 0 ? "Package profile validated. This does not establish gameplay or multiplayer compatibility." : string.Join(Environment.NewLine, errors));
    }
    private async Task RemoveRuntimeAsync()
    {
        var profile = Profile();
        if (MessageBox.Show(this, "Remove the runtime files installed by Uplink? Existing external runtime files, mod packages, configuration and saves are preserved.", "Remove managed runtime", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await Task.Run(() => new RuntimeProvisioner(dataRoot).Remove(profile.GameDirectory, State(profile)));
        Write("Removed the owned runtime. Configuration, packages and saves were preserved.");
    }
    private Task OpenGameAsync() { Process.Start(new ProcessStartInfo(Profile().GameDirectory) { UseShellExecute = true }); return Task.CompletedTask; }
    private async Task PlayAsync()
    {
        var profile = Profile();
        LaunchTicket ticket;
        Process launched;
        using (RuntimeProvisioner.LockGame(profile.GameDirectory))
        {
            var errors = service.ValidateProfile(profile.Id);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            new RuntimeProvisioner(dataRoot).ValidatePrepared(profile.GameDirectory, State(profile));
            RuntimeProvisioner.AssertNotRunning(profile.GameDirectory);
            ticket = GameLaunch.Create(profile.GameDirectory, State(profile));
            launched = Process.Start(ticket.StartInfo) ?? throw new InvalidOperationException("Game process did not start.");
        }
        using var process = launched;
        Write($"Game process {process.Id} started; waiting for this launch's runtime report…");
        var id = process.Id; string? report = null; bool ready = false, failed = false;
        for (int i = 0; i < 60 && !process.HasExited && CanUseControls; i++)
        {
            await Task.Delay(1000);
            var matching = GameLaunch.ReadSessionStatus(State(profile), ticket.Session, id);
            if (matching == null) continue;
            if (report == null) Write("Runtime connected; waiting for its resources to become ready…");
            report = matching;
            using var document = JsonDocument.Parse(report);
            var value = document.RootElement;
            failed = value.TryGetProperty("failed", out var failure) && failure.ValueKind == JsonValueKind.True;
            ready = value.TryGetProperty("resourcesReady", out var resources) && resources.ValueKind == JsonValueKind.True;
            if (failed || ready) break;
        }
        if (!CanUseControls) return;
        if (report != null) Write("Latest runtime report: " + report);
        Write(failed ? "The runtime reported a startup failure. Inspect the session log before playing."
            : ready ? "Runtime resources are ready."
            : process.HasExited ? "The game exited before runtime readiness was confirmed. Check the session log."
            : report != null ? "Runtime connected, but resources were still pending after 60 seconds. Startup remains unverified; inspect the session log."
            : "No matching runtime report arrived within 60 seconds. Startup remains unverified; inspect the session log.");
    }
}
