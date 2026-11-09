using System.Text.Json.Serialization;

namespace Sevo.Mod.Core;

public sealed record Modpack(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Name,
    [property: JsonRequired] ModpackEntry[] Mods)
{
    public static Modpack Read(string path)
    {
        var pack = SafeFiles.Read<Modpack>(Path.GetFullPath(path));
        pack.Validate();
        return pack;
    }

    public void Save(string path)
    {
        Validate();
        SafeFiles.Write(Path.GetFullPath(path), this);
    }

    internal void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(Name) || Name.Length > 120 ||
            Mods is not { Length: > 0 and <= 256 })
            throw new PlatformException("A modpack needs schema version 1, a name, and 1 to 256 mods.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Mods)
        {
            if (entry == null) throw new PlatformException("The modpack contains an empty mod entry.");
            SafeFiles.Id(entry.Id);
            if (!seen.Add(entry.Id)) throw new PlatformException($"The modpack lists {entry.Id} more than once.");
            PackageVersion.Parse(entry.Version);
            GitHubReleaseClient.ValidateRepository(entry.Repository);
            GitHubReleaseClient.ValidateTag(entry.Tag);
            if (string.IsNullOrEmpty(entry.Asset) || SafeFiles.Relative(entry.Asset) != entry.Asset ||
                entry.Asset.Contains('/') || !entry.Asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new PlatformException($"Invalid ZIP asset for {entry.Id}.");
            if (entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(Uri.IsHexDigit))
                throw new PlatformException($"Invalid SHA-256 for {entry.Id}.");
        }
    }
}

public sealed record ModpackEntry(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Version,
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Tag,
    [property: JsonRequired] string Asset,
    [property: JsonRequired] string Sha256,
    [property: JsonRequired] bool Enabled);

public sealed partial class PlatformService
{
    public ProfileRecord ApplyModpack(string profileId, string expectedGeneration, Modpack pack)
    {
        pack.Validate();
        return Mutate(profileId, (profile, catalog) =>
        {
            if (profile.Generation != expectedGeneration)
                throw new PlatformException("The target profile changed since preview. Review the modpack again.");
            foreach (var entry in pack.Mods)
            {
                var package = FindCachedPackage(catalog, entry.Id, entry.Version);
                if (!package.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new PlatformException($"The library ZIP for {entry.Id} {entry.Version} does not match this modpack.");
                InstallSelection(profile, package, entry.Enabled);
            }
        });
    }
}
