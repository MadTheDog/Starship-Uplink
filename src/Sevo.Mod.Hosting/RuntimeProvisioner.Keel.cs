using System.Reflection;
using System.Text.Json;
using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

public enum ProfileKeelState { NotInstalled, Managed, External, Unverified, UpdatePending }

public sealed record ProfileKeelStatus(ProfileKeelState State, Version? Version = null);

public sealed partial class RuntimeProvisioner
{
    internal const string PendingKeelName = ".sevo-keel-update.json";
    private static readonly string[] KeelPaths = KeelUpdater.Files.Select(name => KeelUpdater.PluginDirectory + name).ToArray();

    public static Version? GetManagedKeelVersion(string gameDirectory) =>
        InspectKeel(gameDirectory) is { State: ProfileKeelState.Managed } status ? status.Version : null;

    public static Version? GetBundledKeelVersion(string frameworkDirectory) =>
        ReadKeelVersion(KeelUpdater.Files.Select(name => SafePath.Child(frameworkDirectory, name)).ToArray());

    public static ProfileKeelStatus InspectKeel(string gameDirectory)
    {
        var game = Path.GetFullPath(gameDirectory);
        if (File.Exists(SafePath.Child(game, PendingKeelName))) return new(ProfileKeelState.UpdatePending);
        var manifest = ReadManifest(game, OwnerName);
        var paths = KeelPaths.Select(path => SafePath.Child(game, path)).ToArray();
        var present = paths.Select(File.Exists).ToArray();
        if (manifest == null && present.All(exists => !exists))
            return new(ProfileKeelState.NotInstalled);
        if (!present.All(exists => exists)) return new(ProfileKeelState.Unverified);

        var version = ReadKeelVersion(paths);
        if (version == null) return new(ProfileKeelState.Unverified);
        var entries = manifest == null ? null : ManagedKeel(manifest);
        if (entries == null) return new(ProfileKeelState.External, version);
        return paths.Where((path, i) => HashFile(path) != entries[i].Sha256).Any()
            ? new(ProfileKeelState.Unverified, version)
            : new(ProfileKeelState.Managed, version);
    }

    private static OwnedFile[]? ManagedKeel(RuntimeManifest manifest)
    {
        var entries = KeelPaths.Select(path => manifest.Files.SingleOrDefault(entry => entry.Path == path)).ToArray();
        return entries.All(entry => entry != null && !manifest.IsExternal(entry.Path)) ? entries.Select(entry => entry!).ToArray() : null;
    }

    private static Version? ReadKeelVersion(string[] paths)
    {
        try
        {
            var runtime = AssemblyName.GetAssemblyName(paths[0]);
            var sdk = AssemblyName.GetAssemblyName(paths[1]);
            if (runtime.Name + ".dll" != KeelUpdater.Files[0] || sdk.Name + ".dll" != KeelUpdater.Files[1] ||
                runtime.Version is not { Build: >= 0, Revision: 0 } version || version != sdk.Version) return null;
            return Version.Parse(version.ToString(3));
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    internal void UpdateKeel(string gameDirectory, string stateDirectory, Version version, byte[][] payload)
    {
        var game = ValidateGame(gameDirectory);
        var state = ValidateState(stateDirectory);
        using var gameGuard = LockGame(game);
        using var guard = Acquire(state);
        PlatformService.AssertNoGameCleanup(game);
        var claim = PlatformService.ReadCleanupClaim(SafePath.Child(game, "BepInEx/sevo-profile-owner.json"));
        if (!Path.TrimEndingDirectorySeparator(claim.WorkspaceRoot).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
            !state.Equals(SafePath.Child(root, "runtime/" + claim.ProfileId), StringComparison.OrdinalIgnoreCase))
            throw new PlatformException("Open this game in the Uplink workspace and profile that own it before updating Keel.");
        CheckNotRunning(game);
        Recover(game);
        var owner = ReadManifest(game, OwnerName) ?? throw new PlatformException("Prepare this profile before updating Keel.");
        var entries = ManagedKeel(owner) ?? throw new PlatformException("This profile uses a manually installed Keel. Update that installation manually.");
        Verify(game, owner, false);
        var installed = ReadKeelVersion(entries.Select(entry => SafePath.Child(game, entry.Path)).ToArray());
        if (installed == null || version <= installed)
            throw new PlatformException("Keel could not be identified or this release is no longer newer than the installed version.");
        KeelUpdater.ValidatePayload(payload, version);
        WriteProfileMarker(game, state);
        var pending = SafePath.Child(game, PendingKeelName);
        AtomicJson(pending, new KeelUpdateJournal(version, entries.Select(entry => entry.Sha256).ToArray(), payload));
        RecoverKeel(game);
    }

    private sealed record KeelUpdateJournal(Version Version, string[] PreviousHashes, byte[][] Payload);

    // The durable journal lets Prepare/Play finish a partially replaced pair before using it.
    private static void RecoverKeel(string game)
    {
        var pending = SafePath.Child(game, PendingKeelName);
        if (!File.Exists(pending)) return;
        var owner = ReadManifest(game, OwnerName) ?? throw new PlatformException("Pending Keel update has no runtime ownership record.");
        var entries = ManagedKeel(owner) ?? throw new PlatformException("Pending Keel update does not own both Keel DLLs.");
        using var stream = File.OpenRead(pending);
        if (stream.Length > 3L * KeelUpdater.MaxDllBytes) throw new PlatformException("The Keel recovery record exceeds its size limit.");
        var journal = JsonSerializer.Deserialize<KeelUpdateJournal>(stream, Json);
        stream.Dispose();
        if (journal is not { Version: { Build: >= 0, Revision: -1 }, PreviousHashes: { Length: 2 }, Payload: not null } ||
            journal.PreviousHashes.Any(hash => hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)))
            throw new PlatformException("The pending Keel update record is invalid.");
        KeelUpdater.ValidatePayload(journal.Payload, journal.Version);
        var hashes = journal.Payload.Select(Hash).ToArray();
        var recorded = entries.Select(entry => entry.Sha256).ToArray();
        bool committed = recorded.SequenceEqual(hashes);
        if (!committed && !recorded.SequenceEqual(journal.PreviousHashes))
            throw new PlatformException("Keel ownership changed during the pending update.");
        Verify(game, new RuntimeManifest { Files = owner.Files.Where(entry => !FrameworkFile(entry.Path)).ToList() }, false);

        var paths = KeelPaths.Select(path => SafePath.Child(game, path)).ToArray();
        var live = paths.Select(HashFile).ToArray();
        for (int i = 0; i < paths.Length; i++)
            if (live[i] != hashes[i] && (committed || live[i] != journal.PreviousHashes[i]))
                throw new PlatformException("A Keel DLL changed during the pending update; preserving it: " + KeelPaths[i]);

        for (int i = 0; i < paths.Length; i++)
        {
            if (live[i] != hashes[i])
            {
                var temp = paths[i] + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        output.Write(journal.Payload[i]);
                        output.Flush(true);
                    }
                    File.Move(temp, paths[i], overwrite: true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            entries[i].Sha256 = hashes[i];
        }
        Verify(game, owner, false);
        if (!committed) AtomicJson(SafePath.Child(game, OwnerName), owner);
        File.Delete(pending);
    }
}
