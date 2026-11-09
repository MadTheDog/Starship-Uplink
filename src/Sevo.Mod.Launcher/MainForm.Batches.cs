using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

namespace Sevo.Mod.Launcher;

public sealed partial class MainForm
{
    private async Task InstallAsync()
    {
        var profile = Profile();
        var entries = PickedRows(library).Select(row =>
            new PackageSelection((string)row.Cells["Id"].Value, (string)row.Cells["Version"].Value)).ToArray();
        await Task.Run(() => service.InstallBatch(profile.Id, entries));
        Write($"Installed {entries.Length} selected package(s). Restart the game to apply changes.");
    }

    private async Task ChangeEnabledAsync(bool enabled)
    {
        var profile = Profile();
        var ids = PickedModIds();
        await Task.Run(() => service.SetEnabledBatch(profile.Id, ids, enabled));
        Write($"{(enabled ? "Enabled" : "Disabled")} {ids.Length} selected mod(s) for next launch.");
    }

    private async Task ChangePinnedAsync(bool pinned)
    {
        var profile = Profile();
        var ids = PickedModIds();
        await Task.Run(() => service.SetPinnedBatch(profile.Id, ids, pinned));
        Write($"{(pinned ? "Pinned" : "Unpinned")} {ids.Length} selected mod(s).");
    }

    private async Task RemoveAsync()
    {
        var profile = Profile();
        var ids = PickedModIds();
        if (!Prompts.Review(this, "Remove selected mods", $"Remove from {profile.Name}",
            ids, "Remove selected")) return;
        await Task.Run(() => service.RemoveBatch(profile.Id, ids));
        Write($"Removed {ids.Length} selected mod(s). Cached packages and configuration were preserved.");
    }

    private async Task RemoveAllModsAsync()
    {
        var profile = Profile();
        var ids = profile.Mods.Select(mod => mod.PackageId).ToArray();
        if (ids.Length == 0)
        {
            Write($"There are no mods to remove from {profile.Name}.");
            return;
        }

        if (!Prompts.Review(this, "Remove all mods from profile", $"Remove all {ids.Length} mods from {profile.Name}?",
            ids, "Remove all from profile")) return;
        await Task.Run(() => service.RemoveBatch(profile.Id, ids));
        Write($"Removed {ids.Length} mod(s) from {profile.Name}. Cached packages and configuration were preserved.");
    }

    private async Task RollbackAsync()
    {
        var profile = Profile();
        var ids = PickedModIds();
        await Task.Run(() => service.RollbackBatch(profile.Id, ids));
        Write($"Restored previous versions for {ids.Length} selected mod(s).");
    }

    private async Task UpdateAsync()
    {
        var profile = Profile();
        var ids = PickedModIds();
        var librarySnapshot = service.ListPackages().ToDictionary(package => (package.Id, package.Version));
        var client = new GitHubReleaseClient(http);
        var releases = new Dictionary<string, ReleaseInfo>(StringComparer.OrdinalIgnoreCase);
        var updates = new List<PackageUpdate>();
        var review = new List<string>();
        foreach (var id in ids)
        {
            if (!CanUseControls) return;
            var mod = profile.Mods.Single(item => item.PackageId == id);
            var package = librarySnapshot[(id, mod.Version)];
            if (mod.Pinned || package.Repository == null)
            {
                review.Add($"{package.Name} — {(mod.Pinned ? "Pinned" : "No GitHub source")}");
                continue;
            }

            Write($"Checking {package.Name} for updates…");
            try
            {
                if (!releases.TryGetValue(package.Repository, out var release))
                {
                    release = await client.GetLatestAsync(package.Repository);
                    releases.Add(package.Repository, release);
                }
                if (!CanUseControls) return;
                var assets = release.Assets.Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (assets.Length == 0)
                {
                    review.Add($"{package.Name} — No release ZIP");
                    continue;
                }
                var known = assets.Where(asset => asset.Name == package.AssetName).ToArray();
                var name = known.Length == 1 ? known[0].Name : assets.Length == 1 ? assets[0].Name :
                    Prompts.Choose(this, $"Choose the release ZIP for {package.Name}", assets.Select(asset => asset.Name).Distinct());
                if (name == null)
                {
                    review.Add($"{package.Name} — Skipped");
                    continue;
                }
                var matching = assets.Where(asset => asset.Name == name).ToArray();
                if (matching.Length != 1) throw new PlatformException("Ambiguous release asset name.");
                if (package.Sha256.Equals(matching[0].Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    review.Add($"{package.Name} — Up to date ({mod.Version})");
                    continue;
                }
                var next = await ImportGitHubAssetAsync(client, matching[0], package.Repository, id);
                if (!next.IsNewerThan(package))
                {
                    review.Add($"{package.Name} — No newer package ({mod.Version})");
                    continue;
                }
                updates.Add(new PackageUpdate(id, mod.Version, next.Version));
                review.Add($"{package.Name} — {mod.Version} → {next.Version}{(mod.Enabled ? "" : " (disabled)")}");
            }
            catch (Exception ex) when (ex is PlatformException or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
            {
                review.Add($"{package.Name} — {ex.Message}");
            }
        }
        if (!CanUseControls) return;
        if (!Prompts.Review(this, "Review mod updates", updates.Count == 0 ? "No updates ready" :
            $"{updates.Count} update(s) ready for {profile.Name}. Updating clears local rollback for those mods.",
            review, updates.Count == 0 ? null : "Apply updates")) return;
        var result = await Task.Run(() => service.UpdateFromGitHubBatch(profile.Id, updates));
        Write($"Updated {updates.Count} selected mod(s). Restart the game to apply changes.");
        foreach (var warning in result.CleanupWarnings) Write(warning);
    }

    private async Task ExportModpackAsync()
    {
        var profile = Profile();
        using var dialog = new SaveFileDialog { Title = "Export profile as a modpack",
            Filter = "Uplink modpack (*.uplink-pack.json)|*.uplink-pack.json", DefaultExt = "uplink-pack.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var client = new GitHubReleaseClient(http);
        Write($"Resolving exact GitHub releases for {profile.Name}…");
        var pack = await Task.Run(() => ModpackDownloads.ExportAsync(client, profile, service.ListPackages()));
        if (!CanUseControls) return;
        await Task.Run(() => pack.Save(dialog.FileName));
        Write($"Exported {pack.Name} with {pack.Mods.Length} mod(s).");
    }

    private async Task ImportModpackAsync()
    {
        var profile = Profile();
        using var dialog = new OpenFileDialog { Title = "Import a modpack",
            Filter = "Uplink modpack (*.uplink-pack.json)|*.uplink-pack.json|JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var pack = await Task.Run(() => Modpack.Read(dialog.FileName));
        if (!CanUseControls) return;
        var lines = pack.Mods.Select(entry =>
            $"{entry.Id} {entry.Version} · {(entry.Enabled ? "Enabled" : "Disabled")} · {entry.Repository} @ {entry.Tag}");
        if (!Prompts.Review(this, $"Import {pack.Name}", $"Merge into {profile.Name}\n{profile.GameDirectory}",
            lines, "Import and install")) return;
        Write($"Downloading and verifying packages for {pack.Name}…");
        var client = new GitHubReleaseClient(http);
        await Task.Run(() => ModpackDownloads.ImportAsync(client, service, profile, pack, Path.Combine(dataRoot, "downloads")));
        Write($"Imported {pack.Name} into {profile.Name}. Restart the game to apply changes.");
    }
}
