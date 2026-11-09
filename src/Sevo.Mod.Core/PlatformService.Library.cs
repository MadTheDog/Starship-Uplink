using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sevo.Mod.Core;

public sealed record GitHubUpdateResult(IReadOnlyList<string> RemovedVersions, IReadOnlyList<string> CleanupWarnings);

public sealed partial class PlatformService
{
    private string PackageRemovalPath => SafeFiles.Under(root, "package-remove.json");

    public void RemovePackage(string packageId, string version)
    {
        using var gate = Lock();
        Recover();
        var catalog = Catalog();
        var package = FindCachedPackage(catalog, packageId, version);
        foreach (var profile in Profiles().Items)
        {
            var mod = profile.Mods.SingleOrDefault(mod => mod.PackageId == packageId);
            if (mod?.Version == version)
                throw new PlatformException($"{package.Name} {version} is installed in profile '{profile.Name}'. Remove the mod from that profile first.");
            if (mod?.PreviousVersion == version)
                throw new PlatformException($"{package.Name} {version} is reserved for rollback in profile '{profile.Name}'.");
        }
        RemoveCachedPackage(catalog, package);
    }

    public GitHubUpdateResult UpdateFromGitHub(string profileId, string packageId, string expectedVersion, string version) =>
        UpdateFromGitHubBatch(profileId, [new PackageUpdate(packageId, expectedVersion, version)], enableUpdatedMods: true);

    public GitHubUpdateResult UpdateFromGitHubBatch(string profileId, IReadOnlyList<PackageUpdate> updates) =>
        UpdateFromGitHubBatch(profileId, updates, enableUpdatedMods: false);

    private GitHubUpdateResult UpdateFromGitHubBatch(string profileId, IReadOnlyList<PackageUpdate> updates, bool enableUpdatedMods)
    {
        CheckSelection(updates.Select(update => update.Id));
        using var gate = Lock();
        Recover();
        var catalog = Catalog();
        var profiles = Profiles();
        var replacements = updates.Select(update =>
        {
            var mod = Mod(Find(profiles, profileId), update.Id);
            if (mod.Version != update.InstalledVersion)
                throw new PlatformException($"The installed version of {update.Id} changed. Refresh and try again.");
            if (mod.Pinned) throw new PlatformException($"{update.Id} is pinned. Unpin it before updating.");
            var current = Package(catalog, mod);
            var next = FindCachedPackage(catalog, update.Id, update.NewVersion);
            if (current.Repository == null || !string.Equals(current.Repository, next.Repository, StringComparison.OrdinalIgnoreCase))
                throw new PlatformException($"The update for {current.Name} does not match its GitHub repository.");
            if (PackageVersion.Parse(next.Version).CompareTo(PackageVersion.Parse(current.Version)) <= 0)
                throw new PlatformException($"The GitHub package for {current.Name} is not newer.");
            return (Current: current, Next: next);
        }).ToArray();

        MutateLocked(profiles, catalog, profileId, (profile, _) =>
        {
            foreach (var (current, next) in replacements)
            {
                var installed = Mod(profile, current.Id);
                installed.PreviousVersion = null;
                installed.Version = next.Version;
                if (enableUpdatedMods) installed.Enabled = true;
            }
        });

        var removed = new List<string>();
        var warnings = new List<string>();
        foreach (var (current, next) in replacements)
        {
            PruneUpdatedVersions(profiles, catalog, current, next, removed, warnings);
            if (File.Exists(PackageRemovalPath)) break;
        }
        return new GitHubUpdateResult(removed, warnings);
    }

    private void PruneUpdatedVersions(Profiles profiles, Catalog catalog, PackageRecord current, PackageRecord next,
        List<string> removed, List<string> warnings)
    {
        var packageId = current.Id;
        var nextVersion = PackageVersion.Parse(next.Version);
        var protectedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var installed in profiles.Items.SelectMany(profile => profile.Mods).Where(mod => mod.PackageId == packageId))
        {
            protectedVersions.Add(installed.Version);
            if (installed.PreviousVersion is { } previous) protectedVersions.Add(previous);
        }
        var obsolete = catalog.Packages.Where(package => package.Id == packageId &&
            string.Equals(package.Repository, current.Repository, StringComparison.OrdinalIgnoreCase) &&
            !protectedVersions.Contains(package.Version) &&
            PackageVersion.Parse(package.Version).CompareTo(nextVersion) < 0).ToArray();
        foreach (var package in obsolete)
        {
            try
            {
                RemoveCachedPackage(catalog, package);
                removed.Add(package.Version);
            }
            catch (Exception ex) when (ex is PlatformException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not finish removing {package.Name} {package.Version} from the library. {ex.Message}");
                if (File.Exists(PackageRemovalPath)) break;
            }
        }
    }

    private static PackageRecord FindCachedPackage(Catalog catalog, string packageId, string version)
    {
        SafeFiles.Id(packageId);
        PackageVersion.Parse(version);
        return catalog.Packages.SingleOrDefault(package => package.Id == packageId && package.Version == version)
            ?? throw new PlatformException($"Package {packageId} {version} is missing from the library.");
    }

    private void RemoveCachedPackage(Catalog catalog, PackageRecord package)
    {
        var path = PackageRemovalPath;
        var directory = SafeFiles.Under(store, package.Sha256);
        bool lastReference = !catalog.Packages.Any(other => other != package && other.Sha256 == package.Sha256);
        var attributes = SafeFiles.Attributes(directory);
        if (lastReference && attributes is not null)
        {
            if ((attributes.Value & FileAttributes.Directory) == 0)
                throw new PlatformException("The cached package path is not a directory. Its contents were preserved.");
            var metadataPath = SafeFiles.Under(directory, "metadata.json");
            var metadata = SafeFiles.Read<PackageRecord>(metadataPath);
            // Reimport can add source information to the catalog without rewriting the content object.
            metadata.Repository ??= package.Repository;
            metadata.AssetName ??= package.AssetName;
            if (JsonSerializer.Serialize(metadata, SafeFiles.Json) != JsonSerializer.Serialize(package, SafeFiles.Json))
                throw new PlatformException("Cached package metadata differs from the catalog. Its contents were preserved.");

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["archive.zip"] = package.Sha256,
                ["metadata.json"] = SafeFiles.Hash(metadataPath)
            };
            foreach (var file in package.Files) files.Add("payload/" + file.Key, file.Value);
            CheckTree(directory, files, "Cached package");
            SafeFiles.Write(path, new PackageRemovalIntent { Sha256 = package.Sha256, Files = files }, overwrite: false);
            checkpoint?.Invoke("package-removal-intent");
        }

        catalog.Packages.Remove(package);
        try { SafeFiles.Write(catalogPath, catalog); }
        catch { catalog.Packages.Add(package); throw; }
        checkpoint?.Invoke("package-removal-committed");
        RecoverPackageRemoval();
    }

    private void RecoverPackageRemoval()
    {
        var path = PackageRemovalPath;
        if (SafeFiles.Attributes(path) is null) return;
        var intent = SafeFiles.Read<PackageRemovalIntent>(path);
        ValidateFileMap(intent.Files, "pending package removal");
        if (intent.SchemaVersion != 1 || !Regex.IsMatch(intent.Sha256 ?? "", "^[a-f0-9]{64}$") ||
            !intent.Files.TryGetValue("archive.zip", out var hash) || hash != intent.Sha256 ||
            !intent.Files.ContainsKey("metadata.json") ||
            intent.Files.Keys.Any(file => file is not ("archive.zip" or "metadata.json") && !file.StartsWith("payload/", StringComparison.Ordinal)))
            throw new PlatformException("Invalid pending library removal. Cached files were preserved.");

        // The catalog is the commit point. An interrupted delete can leave a subset of the original files.
        if (!File.Exists(catalogPath))
            throw new PlatformException("The library catalog is missing during package removal. Cached files were preserved.");
        if (!Catalog().Packages.Any(package => package.Sha256 == intent.Sha256))
        {
            var directory = SafeFiles.Under(store, intent.Sha256!);
            var attributes = SafeFiles.Attributes(directory);
            if (attributes is not null && (attributes.Value & FileAttributes.Directory) == 0)
                throw new PlatformException("The pending package removal points to a file. Its contents were preserved.");
            if (SafeFiles.Inventory(directory).Any(file => !intent.Files.TryGetValue(file.Key, out var digest) || file.Value != digest))
                throw new PlatformException("Files changed during package removal. Preserve the cache changes before retrying.");
            SafeFiles.DeleteTree(directory);
        }
        File.Delete(path);
    }

    private sealed class PackageRemovalIntent
    {
        [JsonRequired] public int SchemaVersion { get; set; } = 1;
        [JsonRequired] public string Sha256 { get; set; } = "";
        [JsonRequired] public Dictionary<string, string> Files { get; set; } = [];
    }
}
