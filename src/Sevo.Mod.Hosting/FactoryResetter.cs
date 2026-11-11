using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

/// <summary>Explicit destructive recovery, independent of old runtime inventories and hashes.</summary>
public sealed class FactoryResetter
{
    private readonly string workspace;
    private readonly IGameProcessInspector processes;
    private readonly Action<string>? checkpoint;
    private static readonly string[] Targets =
    [
        "BepInEx", "doorstop_libs", "winhttp.dll", "doorstop_config.ini", ".doorstop_version",
        ".sevo-runtime-owned.json", ".sevo-runtime-pending.json", ".sevo-keel-update.json"
    ];

    public FactoryResetter(string workspace, IGameProcessInspector? processes = null, Action<string>? checkpoint = null)
    {
        this.workspace = Path.GetFullPath(workspace);
        this.processes = processes ?? new GameProcessInspector();
        this.checkpoint = checkpoint;
    }

    public RuntimePreparation Reset(string profileId, string loaderZip, string frameworkDirectory)
    {
        var platform = new PlatformService(workspace, processes);
        var selected = platform.ListProfilesForReset().SingleOrDefault(profile => profile.Id == profileId)
            ?? throw new PlatformException("Select a profile before resetting its game installation.");
        var game = RuntimeProvisioner.ValidateGame(selected.GameDirectory);
        using var gameGuard = RuntimeProvisioner.LockGame(game);
        using var platformGuard = platform.LockForCleanup();
        var profile = platform.InspectFactoryResetProfile(profileId);
        if (!PlatformService.SamePath(profile.GameDirectory, game)) throw new PlatformException("The selected profile's game directory changed. Select it again.");
        if (processes.IsRunning(game)) throw new InvalidOperationException("Close this Starship EVO installation before resetting it.");
        SafePath.Check(workspace);
        var state = SafePath.Child(workspace, "runtime/" + profile.Id);
        var targets = Targets.Select(relative => Path.Combine(game, relative)).ToArray();
        foreach (var target in targets.Append(state))
        {
            // Never erase an overlapping workspace, source payload or game installation.
            foreach (var preserve in new[] { workspace, game, Path.GetFullPath(loaderZip), Path.GetFullPath(frameworkDirectory) })
                if (Inside(preserve, target)) throw new InvalidOperationException("Reset files overlap the game, Uplink workspace or replacement payload. Use a separate Uplink folder.");
        }
        RuntimeProvisioner.ValidateResetPayload(loaderZip, frameworkDirectory);
        platform.CheckFactoryResetOperations(profile);
        var runtime = new RuntimeProvisioner(workspace, processes);
        using (runtime.LockCleanupRuntime(profile.Id))
        {
            // Fixed path and fixed deletion targets; no path comes from an old manifest.
            var journal = SafePath.Child(game, PlatformService.FactoryResetJournal);
            File.WriteAllText(journal, "Factory reset incomplete; retry Reset to factory settings.");
            checkpoint?.Invoke("reset-started");
            foreach (var target in targets) Delete(target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(state))
                if (Path.GetFileName(entry) != ".runtime.lock") Delete(entry);
            platform.ClearFactoryResetProfile(profile);
            checkpoint?.Invoke("loader-removed");
            platform.ClaimFactoryResetProfile(profile);
        }
        var result = runtime.Prepare(game, state, loaderZip, frameworkDirectory, factoryReset: true);
        checkpoint?.Invoke("runtime-installed");
        runtime.ValidatePrepared(game, state, factoryReset: true);
        File.Delete(SafePath.Child(game, PlatformService.FactoryResetJournal));
        return result;
    }

    private static bool Inside(string path, string parent) => PlatformService.SamePath(path, parent) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void Delete(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        // Detach links, including junctions, without visiting their targets.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
            else File.Delete(path);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Delete(entry);
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            Directory.Delete(path);
        }
        else
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }
}
