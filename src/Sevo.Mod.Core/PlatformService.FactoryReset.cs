using System.Text.Json;

namespace Sevo.Mod.Core;

public sealed partial class PlatformService
{
    internal const string FactoryResetJournal = ".sevo-factory-reset.json";

    // Recovery must remain reachable when ordinary deployment recovery cannot run.
    public IReadOnlyList<ProfileRecord> ListProfilesForReset()
    {
        using var gate = Lock();
        return Profiles().Items;
    }

    internal ProfileRecord InspectFactoryResetProfile(string id) => Find(Profiles(), id);

    internal void CheckFactoryResetOperations(ProfileRecord profile)
    {
        foreach (var (path, field) in new[] { (journalPath, "before"), (creationPath, "profile") })
        {
            SafeFiles.Ancestors(path);
            if (!File.Exists(path)) continue;
            // Inspect only identity, never hashes or the state of the old deployment.
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var record = json.RootElement.GetProperty(field);
            if (record.GetProperty("id").GetString() != profile.Id ||
                !SamePath(record.GetProperty("gameDirectory").GetString()!, profile.GameDirectory))
                throw new PlatformException("Another profile has an unfinished operation. Finish that operation before resetting this installation.");
        }
    }

    internal void ClearFactoryResetProfile(ProfileRecord profile)
    {
        CheckFactoryResetOperations(profile);
        var profiles = Profiles();
        var current = Find(profiles, profile.Id);
        current.Mods.Clear();
        current.Generation = Guid.NewGuid().ToString("N");
        SafeFiles.Write(profilesPath, profiles);
        File.Delete(journalPath);
        File.Delete(creationPath);
    }

    internal void ClaimFactoryResetProfile(ProfileRecord profile)
    {
        SafeFiles.Create(Path.GetDirectoryName(Marker(profile))!);
        SafeFiles.Write(Marker(profile), new Ownership { ProfileId = profile.Id, WorkspaceRoot = root });
    }
}
