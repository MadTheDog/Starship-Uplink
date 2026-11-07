namespace Sevo.Mod.Core;

public sealed partial class PlatformService
{
    internal const string CleanupJournal = "BepInEx/sevo-cleanup-pending.json";

    internal IDisposable LockForCleanup() => Lock();

    internal static void AssertNoGameCleanup(string game)
    {
        var journal = SafeFiles.Under(game, CleanupJournal);
        if (SafeFiles.Attributes(journal).HasValue)
            throw new PlatformException("Game cleanup is pending. Resume it with Clean game before changing this installation.");
    }

    internal static Ownership ReadCleanupClaim(string marker)
    {
        var claim = SafeFiles.Read<Ownership>(marker);
        if (claim.SchemaVersion != 1 || !Guid.TryParseExact(claim.ProfileId, "N", out _))
            throw new PlatformException("The Uplink profile ownership marker is invalid. Its files have been preserved.");
        ValidateAbsolutePath(claim.WorkspaceRoot, "Uplink profile ownership marker");
        SafeFiles.Ancestors(claim.WorkspaceRoot);
        if (SafeFiles.Attributes(claim.WorkspaceRoot) is FileAttributes attributes && (attributes & FileAttributes.Directory) == 0)
            throw new PlatformException("The original Uplink workspace path is a file.");
        return claim;
    }

    internal ProfileRecord? InspectCleanupProfile(string game, Ownership claim)
    {
        AssertNoProfileTransaction(root);
        var original = Path.GetFullPath(claim.WorkspaceRoot);
        var local = SamePath(original, root);
        if (!local) AssertNoProfileTransaction(original);
        var profiles = local ? Profiles() : ReadProfiles(Path.Combine(original, "profiles.json"));
        var matching = profiles.Items.SingleOrDefault(profile => profile.Id == claim.ProfileId);
        if ((matching != null && !SamePath(matching.GameDirectory, game)) ||
            profiles.Items.Any(profile => profile.Id != claim.ProfileId && SamePath(profile.GameDirectory, game)) ||
            (!local && Profiles().Items.Any(profile => SamePath(profile.GameDirectory, game))))
            throw new PlatformException("The Uplink claim and profile catalog disagree about this game. Review the ownership records before cleanup.");
        if (!local && matching != null)
            throw new PlatformException("This game is owned by another Uplink workspace. Open that workspace to clean the game.");

        var transactions = SafeFiles.Under(game, "BepInEx/SevoTransactions/" + claim.ProfileId);
        if (SafeFiles.Attributes(transactions) is FileAttributes attributes &&
            ((attributes & FileAttributes.Directory) == 0 || Directory.EnumerateFileSystemEntries(transactions).Any()))
            throw new PlatformException("This game has a pending or unrecognized deployment transaction. Resolve it before cleanup.");
        return matching;
    }

    internal void VerifyCleanupDeployment(ProfileRecord profile, Dictionary<string, string> deployed)
    {
        if (!SafeFiles.Equal(deployed, Expected(profile, Catalog())))
            throw new PlatformException("The managed deployment differs from the profile inventory. Preserve and resolve the external changes before cleanup.");
    }

    internal void ReleaseCleanupProfile(ProfileRecord profile)
    {
        var profiles = Profiles();
        var current = Find(profiles, profile.Id);
        if (current.Generation != profile.Generation || !SamePath(current.GameDirectory, profile.GameDirectory))
            throw new PlatformException("The profile changed during cleanup.");
        profiles.Items.Remove(current);
        SafeFiles.Write(profilesPath, profiles);
    }

    private static void AssertNoProfileTransaction(string workspace)
    {
        foreach (var name in new[] { "operation.json", "profile-create.json" })
        {
            var path = SafeFiles.Under(workspace, name);
            if (SafeFiles.Attributes(path).HasValue)
                throw new PlatformException("The Uplink workspace has a pending profile operation. Resolve it before cleanup.");
        }
    }

    internal static bool SamePath(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
}
