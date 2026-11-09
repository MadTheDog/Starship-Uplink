namespace Sevo.Mod.Core;

public sealed record PackageSelection(string Id, string Version);
public sealed record PackageUpdate(string Id, string InstalledVersion, string NewVersion);

public sealed partial class PlatformService
{
    public ProfileRecord InstallBatch(string profileId, IReadOnlyList<PackageSelection> packages)
    {
        CheckSelection(packages.Select(package => package.Id));
        return Mutate(profileId, (profile, catalog) =>
        {
            foreach (var entry in packages)
                InstallSelection(profile, FindCachedPackage(catalog, entry.Id, entry.Version), enabled: true);
        });
    }

    private static void InstallSelection(ProfileRecord profile, PackageRecord package, bool enabled)
    {
        var mod = profile.Mods.SingleOrDefault(item => item.PackageId == package.Id);
        if (mod == null)
        {
            profile.Mods.Add(new ProfileModRecord { PackageId = package.Id, Version = package.Version, Enabled = enabled });
            return;
        }
        if (mod.Version != package.Version)
        {
            if (mod.Pinned) throw new PlatformException($"Unpin {package.Name} before changing its version.");
            mod.PreviousVersion = mod.Version;
            mod.Version = package.Version;
        }
        mod.Enabled = enabled;
    }

    public ProfileRecord SetEnabledBatch(string profileId, IReadOnlyList<string> ids, bool enabled) =>
        ChangeInstalled(profileId, ids, (_, mod) => mod.Enabled = enabled);

    public ProfileRecord SetPinnedBatch(string profileId, IReadOnlyList<string> ids, bool pinned) =>
        ChangeInstalled(profileId, ids, (_, mod) => mod.Pinned = pinned);

    public ProfileRecord RemoveBatch(string profileId, IReadOnlyList<string> ids) =>
        ChangeInstalled(profileId, ids, (profile, mod) => profile.Mods.Remove(mod));

    public ProfileRecord RollbackBatch(string profileId, IReadOnlyList<string> ids) =>
        ChangeInstalled(profileId, ids, (_, mod) => RollbackSelection(mod));

    private static void RollbackSelection(ProfileModRecord mod)
    {
        if (mod.Pinned) throw new PlatformException($"Unpin {mod.PackageId} before rolling back.");
        if (mod.PreviousVersion == null) throw new PlatformException($"No previous version is recorded for {mod.PackageId}.");
        (mod.Version, mod.PreviousVersion) = (mod.PreviousVersion, mod.Version);
    }

    private ProfileRecord ChangeInstalled(string profileId, IReadOnlyList<string> ids, Action<ProfileRecord, ProfileModRecord> change)
    {
        CheckSelection(ids);
        return Mutate(profileId, (profile, _) =>
        {
            foreach (var id in ids) change(profile, Mod(profile, id));
        });
    }

    private static void CheckSelection(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            SafeFiles.Id(id);
            if (!seen.Add(id)) throw new PlatformException($"Choose only one version of {id}.");
        }
        if (seen.Count == 0) throw new PlatformException("Select at least one mod.");
    }
}
