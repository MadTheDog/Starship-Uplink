using System.Text.Json.Serialization;
using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

public sealed class GameCleanupPlan
{
    internal GameCleanupPlan(string workspace, string game, string backup, GameCleanupIntent intent, bool resuming)
    {
        Workspace = workspace;
        GameDirectory = game;
        BackupDirectory = backup;
        Intent = intent;
        IsResuming = resuming;
    }

    internal string Workspace { get; }
    internal GameCleanupIntent Intent { get; }
    public string GameDirectory { get; }
    public string ProfileId => Intent.ProfileId;
    public string OriginalWorkspaceRoot => Intent.OriginalWorkspaceRoot;
    public bool IsOrphaned => Intent.ProfileGeneration == null;
    public string BackupDirectory { get; }
    public bool IsResuming { get; }
}

public sealed record GameCleanupResult(string BackupDirectory);

/// <summary>Archives a claimed game's Uplink deployment and releases its profile.</summary>
public sealed class GameCleaner
{
    private const string ClaimPath = "BepInEx/sevo-profile-owner.json";
    private const string ManagedPath = "BepInEx/plugins/SevoManaged";
    private readonly string workspace;
    private readonly IGameProcessInspector processes;
    private readonly PlatformService platform;
    private readonly RuntimeProvisioner runtime;
    private readonly Action<string>? checkpoint;

    public GameCleaner(string workspaceRoot, IGameProcessInspector? processes = null, Action<string>? cleanupCheckpoint = null)
    {
        workspace = Path.GetFullPath(workspaceRoot);
        this.processes = processes ?? new GameProcessInspector();
        platform = new PlatformService(workspace, this.processes);
        runtime = new RuntimeProvisioner(workspace, this.processes);
        checkpoint = cleanupCheckpoint;
    }

    public GameCleanupPlan Inspect(string gameDirectory)
    {
        var game = RuntimeProvisioner.ValidateGame(gameDirectory);
        using var gameGuard = RuntimeProvisioner.LockGame(game);
        using var guard = platform.LockForCleanup();
        var state = ReadState(game, Guid.NewGuid().ToString("N"));
        return new GameCleanupPlan(workspace, game, state.Backup, state.Intent, state.Resuming);
    }

    public GameCleanupResult Clean(GameCleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!PlatformService.SamePath(plan.Workspace, workspace))
            throw new PlatformException("Use the Uplink workspace that inspected this cleanup plan.");
        var game = RuntimeProvisioner.ValidateGame(plan.GameDirectory);
        using var gameGuard = RuntimeProvisioner.LockGame(game);
        using var guard = platform.LockForCleanup();
        CheckStopped(game);
        using var runtimeGuard = plan.IsOrphaned || !PlatformService.SamePath(plan.OriginalWorkspaceRoot, workspace)
            ? null : runtime.LockCleanupRuntime(plan.ProfileId);
        var state = ReadState(game, plan.Intent.BackupId);
        if (!SameIntent(plan.Intent, state.Intent))
            throw new PlatformException("The cleanup plan changed. Inspect this installation again before cleaning it.");
        var journal = SafeFiles.Under(game, PlatformService.CleanupJournal);
        if (!state.Resuming) SafeFiles.Write(journal, state.Intent, overwrite: false);
        SafeFiles.Create(state.Backup);
        CheckStopped(game);

        if (state.ManagedInGame)
            Directory.Move(SafeFiles.Under(game, ManagedPath), SafeFiles.Under(state.Backup, "SevoManaged"));
        checkpoint?.Invoke("deployment-archived");
        foreach (var file in state.RuntimeFiles)
        {
            Archive(file);
            checkpoint?.Invoke("runtime-file-archived");
        }
        if (state.RuntimeOwner != null) Archive(state.RuntimeOwner);

        // Validate the completed archive before releasing either ownership record.
        state = ReadState(game, plan.Intent.BackupId);
        if (state.Profile != null) platform.ReleaseCleanupProfile(state.Profile);
        checkpoint?.Invoke("profile-released");
        Archive(state.Claim);
        checkpoint?.Invoke("claim-archived");
        File.Delete(journal);
        return new GameCleanupResult(state.Backup);
    }

    private CleanupState ReadState(string game, string proposedBackup)
    {
        CheckStopped(game);
        var journal = SafeFiles.Under(game, PlatformService.CleanupJournal);
        var pending = RegularFileExists(journal) ? SafeFiles.Read<GameCleanupIntent>(journal) : null;
        if (pending != null &&
            (pending.SchemaVersion != 1 || !Guid.TryParseExact(pending.BackupId, "N", out _) ||
             pending.ManagedFiles == null || pending.RuntimeFiles == null))
            throw new PlatformException("The pending game cleanup record is invalid. Its files have been preserved.");
        if (RegularFileExists(SafeFiles.Under(game, RuntimeProvisioner.PendingName)))
            throw new PlatformException("The game has a pending runtime operation. Resolve it before cleanup.");

        var backup = SafeFiles.Under(game, "BepInEx/SevoCleanupBackups/" + (pending?.BackupId ?? proposedBackup));
        if (IsDirectory(backup) && pending == null)
            throw new PlatformException("The selected cleanup backup already exists. Inspect the game again.");
        var archived = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var claim = LocateFile(game, backup, ClaimPath, "sevo-profile-owner.json", archived)
            ?? throw new PlatformException("The game has no valid Uplink claim to clean. Review any remaining files manually.");
        var owner = PlatformService.ReadCleanupClaim(claim.CurrentPath);
        var profile = platform.InspectCleanupProfile(game, owner);

        var managed = SafeFiles.Under(game, ManagedPath);
        var managedBackup = SafeFiles.Under(backup, "SevoManaged");
        var managedInGame = IsDirectory(managed);
        var managedArchived = IsDirectory(managedBackup);
        if (managedInGame && managedArchived)
            throw new PlatformException("Both the installed and archived managed deployments exist. Review them before resuming cleanup.");
        var managedFiles = SafeFiles.Inventory(managedArchived ? managedBackup : managed);
        if (managedArchived)
            foreach (var entry in managedFiles) archived.Add("SevoManaged/" + entry.Key, entry.Value);
        if (profile != null) platform.VerifyCleanupDeployment(profile, managedFiles);

        var runtimeOwner = LocateFile(game, backup, RuntimeProvisioner.OwnerName, "runtime/" + RuntimeProvisioner.OwnerName, archived);
        var runtimeFiles = new List<CleanupFile>();
        var runtimeInventory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (runtimeOwner != null)
        {
            foreach (var entry in RuntimeProvisioner.ReadCleanupFiles(game, runtimeOwner.CurrentPath))
            {
                var file = LocateFile(game, backup, entry.Path, "runtime/" + entry.Path, archived);
                if (file == null) continue; // An already absent owned file requires no move.
                if (file.Hash != entry.Sha256)
                    throw new PlatformException("An owned runtime file was modified and has been preserved: " + entry.Path);
                runtimeInventory.Add(entry.Path, file.Hash);
                runtimeFiles.Add(file);
            }
        }

        var intent = new GameCleanupIntent
        {
            BackupId = pending?.BackupId ?? proposedBackup,
            GameDirectory = game,
            OriginalWorkspaceRoot = Path.GetFullPath(owner.WorkspaceRoot),
            ProfileId = owner.ProfileId,
            ProfileGeneration = pending == null ? profile?.Generation : pending.ProfileGeneration,
            ClaimHash = claim.Hash,
            HasManagedDirectory = managedInGame || managedArchived,
            ManagedFiles = managedFiles,
            RuntimeOwnerHash = runtimeOwner?.Hash,
            RuntimeFiles = runtimeInventory
        };
        if (!SafeFiles.Equal(SafeFiles.Inventory(backup), archived))
            throw new PlatformException("Unexpected files are present in the cleanup backup. They have been preserved.");
        if (pending != null)
        {
            var runtimeInGame = runtimeOwner is { Archived: false } || runtimeFiles.Any(file => !file.Archived);
            var deploymentInGame = managedInGame || runtimeInGame;
            if (!SameIntent(pending, intent) ||
                profile != null && profile.Generation != pending.ProfileGeneration ||
                profile == null && pending.ProfileGeneration != null && deploymentInGame ||
                claim.Archived && (deploymentInGame || profile != null) ||
                runtimeOwner is { Archived: true } && runtimeInGame)
                throw new PlatformException("The pending cleanup records or deployment changed. The remaining files have been preserved.");
        }
        return new CleanupState(intent, backup, pending != null, profile, managedInGame, claim, runtimeOwner, runtimeFiles);
    }

    private void CheckStopped(string game)
    {
        if (processes.IsRunning(game))
            throw new PlatformException("Close this Starship EVO installation before cleaning the game.");
    }

    private static bool SameIntent(GameCleanupIntent first, GameCleanupIntent second) =>
        first.BackupId == second.BackupId && first.ProfileId == second.ProfileId &&
        first.ProfileGeneration == second.ProfileGeneration && first.ClaimHash == second.ClaimHash &&
        string.Equals(first.GameDirectory, second.GameDirectory, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.OriginalWorkspaceRoot, second.OriginalWorkspaceRoot, StringComparison.OrdinalIgnoreCase) &&
        first.HasManagedDirectory == second.HasManagedDirectory && SafeFiles.Equal(first.ManagedFiles, second.ManagedFiles) &&
        first.RuntimeOwnerHash == second.RuntimeOwnerHash && SafeFiles.Equal(first.RuntimeFiles, second.RuntimeFiles);

    private static CleanupFile? LocateFile(string game, string backup, string sourceRelative, string archiveRelative, Dictionary<string, string> archive)
    {
        var source = SafeFiles.Under(game, sourceRelative);
        var destination = SafeFiles.Under(backup, archiveRelative);
        var sourceExists = RegularFileExists(source);
        var archived = RegularFileExists(destination);
        if (sourceExists && archived) throw new PlatformException("A cleanup file exists in both the game and the backup: " + sourceRelative);
        if (!sourceExists && !archived) return null;
        var hash = SafeFiles.Hash(archived ? destination : source);
        if (archived) archive.Add(archiveRelative, hash);
        return new CleanupFile(source, destination, hash, archived);
    }

    private static bool RegularFileExists(string path)
    {
        if (SafeFiles.Attributes(path) is not FileAttributes attributes) return false;
        if ((attributes & FileAttributes.Directory) != 0) throw new PlatformException("Expected a file at this cleanup path: " + path);
        return true;
    }

    private static bool IsDirectory(string path)
    {
        if (SafeFiles.Attributes(path) is not FileAttributes attributes) return false;
        if ((attributes & FileAttributes.Directory) == 0) throw new PlatformException("Expected a directory at this cleanup path: " + path);
        return true;
    }

    private static void Archive(CleanupFile file)
    {
        if (file.Archived) return;
        if (SafeFiles.Hash(file.Source) != file.Hash)
            throw new PlatformException("A cleanup file changed before it could be archived: " + file.Source);
        SafeFiles.Create(Path.GetDirectoryName(file.Destination)!);
        SafeFiles.Ancestors(file.Destination);
        File.Move(file.Source, file.Destination);
    }

    private sealed record CleanupFile(string Source, string Destination, string Hash, bool Archived)
    {
        public string CurrentPath => Archived ? Destination : Source;
    }

    private sealed record CleanupState(GameCleanupIntent Intent, string Backup, bool Resuming, ProfileRecord? Profile,
        bool ManagedInGame, CleanupFile Claim, CleanupFile? RuntimeOwner, List<CleanupFile> RuntimeFiles);
}

internal sealed class GameCleanupIntent
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public string BackupId { get; set; } = "";
    [JsonRequired] public string GameDirectory { get; set; } = "";
    [JsonRequired] public string OriginalWorkspaceRoot { get; set; } = "";
    [JsonRequired] public string ProfileId { get; set; } = "";
    [JsonRequired] public string? ProfileGeneration { get; set; }
    [JsonRequired] public string ClaimHash { get; set; } = "";
    [JsonRequired] public bool HasManagedDirectory { get; set; }
    [JsonRequired] public Dictionary<string, string> ManagedFiles { get; set; } = [];
    [JsonRequired] public string? RuntimeOwnerHash { get; set; }
    [JsonRequired] public Dictionary<string, string> RuntimeFiles { get; set; } = [];
}
