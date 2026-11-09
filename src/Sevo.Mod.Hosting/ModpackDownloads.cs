using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

public static class ModpackDownloads
{
    public static async Task<Modpack> ExportAsync(GitHubReleaseClient client, ProfileRecord profile,
        IReadOnlyList<PackageRecord> library)
    {
        if (profile.Mods.Count == 0) throw new PlatformException("Install at least one mod before exporting a modpack.");
        if (profile.Mods.Count > 256) throw new PlatformException("A modpack can contain up to 256 mods.");
        var entries = new List<ModpackEntry>();
        var releases = new Dictionary<string, IReadOnlyList<ReleaseInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in profile.Mods)
        {
            var package = library.Single(item => item.Id == mod.PackageId && item.Version == mod.Version);
            if (package.Repository == null || package.AssetName == null)
                throw new PlatformException($"{package.Name} has no GitHub release source. Import its release using Add from GitHub first.");
            if (!releases.TryGetValue(package.Repository, out var published))
            {
                published = await client.GetPublishedAsync(package.Repository).ConfigureAwait(false);
                releases.Add(package.Repository, published);
            }
            var release = published.FirstOrDefault(candidate => candidate.Assets.Any(asset =>
                asset.Name == package.AssetName && package.Sha256.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)));
            if (release == null)
                throw new PlatformException($"GitHub has no published asset matching the cached ZIP for {package.Name} {package.Version}.");
            entries.Add(new ModpackEntry(package.Id, package.Version, package.Repository, release.TagName,
                package.AssetName, package.Sha256, mod.Enabled));
        }
        return new Modpack(1, profile.Name, entries.ToArray());
    }

    public static async Task<ProfileRecord> ImportAsync(GitHubReleaseClient client, PlatformService service,
        ProfileRecord profile, Modpack pack, string downloads)
    {
        pack.Validate();
        var library = service.ListPackages().ToDictionary(package => (package.Id, package.Version));
        var releases = new Dictionary<(string Repository, string Tag), ReleaseInfo>();
        foreach (var entry in pack.Mods)
        {
            if (library.TryGetValue((entry.Id, entry.Version), out var cached))
            {
                if (!entry.Sha256.Equals(cached.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new PlatformException($"The library already has different bytes for {entry.Id} {entry.Version}.");
                continue;
            }

            var key = (entry.Repository.ToLowerInvariant(), entry.Tag);
            if (!releases.TryGetValue(key, out var release))
            {
                release = await client.GetByTagAsync(entry.Repository, entry.Tag).ConfigureAwait(false);
                releases.Add(key, release);
            }
            var assets = release.Assets.Where(asset => asset.Name == entry.Asset).ToArray();
            if (assets.Length != 1 || assets[0].Sha256 is { } digest &&
                !entry.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase))
                throw new PlatformException($"The release asset for {entry.Id} is missing, ambiguous, or has changed.");

            var verified = new ReleaseAsset { Name = entry.Asset, DownloadUrl = assets[0].DownloadUrl, Sha256 = entry.Sha256 };
            var imported = await ReleaseDownloads.ImportPackageAsync(client, verified, downloads, service, entry.Repository,
                expectedId: entry.Id, expectedVersion: entry.Version).ConfigureAwait(false);
            library.Add((imported.Id, imported.Version), imported);
        }
        return service.ApplyModpack(profile.Id, profile.Generation, pack);
    }
}
