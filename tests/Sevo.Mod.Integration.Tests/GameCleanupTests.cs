using System.IO.Compression;
using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

static class GameCleanupTests
{
    public static void Add(List<(string Name, Action Run)> tests, string root)
    {
        tests.Add(("game cleanup releases a live profile and archives exact deployment bytes", () =>
        {
            var f = new Fixture(root, "clean-live");
            f.PrepareRuntime(externalLoader: true);
            var manual = f.WriteGame("BepInEx/plugins/Manual.dll", "manual plugin");
            var config = f.WriteGame("BepInEx/config/BepInEx.cfg", "user configuration");
            var save = f.WriteGame("Saved/fixture.world", "saved data");
            var cleaner = new GameCleaner(f.Workspace, f.Processes);
            var plan = cleaner.Inspect(f.Game);
            Check(!plan.IsOrphaned && plan.ProfileId == f.ProfileId && plan.OriginalWorkspaceRoot == f.Workspace, "wrong profile was inspected");
            var claim = File.ReadAllBytes(f.Claim);
            var result = cleaner.Clean(plan);
            Check(result.BackupDirectory == plan.BackupDirectory, "clean used a different backup from the confirmed plan");
            Check(!File.Exists(f.Claim) && !Directory.Exists(f.Managed), "game claim or managed deployment survived cleanup");
            Check(File.ReadAllBytes(Path.Combine(result.BackupDirectory, "sevo-profile-owner.json")).SequenceEqual(claim), "claim bytes were changed");
            Check(File.ReadAllText(Path.Combine(result.BackupDirectory, "SevoManaged/cleanup-fixture/Managed.dll")) == "managed payload", "deployment was not backed up");
            Check(File.ReadAllText(manual) == "manual plugin" && File.ReadAllText(config) == "user configuration" && File.ReadAllText(save) == "saved data", "unmanaged data was changed");
            Check(File.ReadAllText(Path.Combine(f.Game, "winhttp.dll")) == "loader", "external loader was changed");
            Check(!File.Exists(Path.Combine(f.Game, "BepInEx/plugins/SevoPlatform/Sevo.Framework.Runtime.dll")), "owned Keel remained installed");
            Check(f.Service.ListProfiles().Count == 0 && f.Service.ListPackages().Count == 1, "profile or cached packages were not handled correctly");
            Check(f.Service.CreateProfile("replacement", f.Game).Id != f.ProfileId, "clean game could not be claimed again");
        }));

        tests.Add(("orphan cleanup archives the original unverified deployment after workspace loss", () =>
        {
            var f = new Fixture(root, "clean-orphan");
            f.PrepareRuntime(externalLoader: false);
            f.WriteGame("BepInEx/plugins/SevoManaged/cleanup-fixture/Managed.dll", "original orphan bytes");
            var claim = File.ReadAllBytes(f.Claim);
            Directory.Move(f.Workspace, f.Workspace + "-lost");
            var replacement = Path.Combine(f.Target, "replacement-workspace");
            var cleaner = new GameCleaner(replacement, f.Processes);
            var plan = cleaner.Inspect(f.Game);
            Check(plan.IsOrphaned && plan.OriginalWorkspaceRoot == f.Workspace, "lost workspace was not recognized");
            var backup = cleaner.Clean(plan).BackupDirectory;
            Check(File.ReadAllBytes(Path.Combine(backup, "sevo-profile-owner.json")).SequenceEqual(claim), "original orphan claim was altered");
            Check(File.ReadAllText(Path.Combine(backup, "SevoManaged/cleanup-fixture/Managed.dll")) == "original orphan bytes", "orphan bytes were lost");
            Check(!Directory.Exists(f.Workspace) && !File.Exists(f.Claim), "orphan cleanup restored old workspace or left the claim");
            Check(!File.Exists(Path.Combine(f.Game, "winhttp.dll")), "verified owned loader remained in orphaned game");
            Check(File.Exists(Path.Combine(f.Game, "BepInEx/config/BepInEx.cfg")), "orphan cleanup removed configuration");
            Check(new PlatformService(replacement, f.Processes).CreateProfile("new owner", f.Game).Id != f.ProfileId, "new workspace cannot claim cleaned game");
        }));

        tests.Add(("cleanup refuses a live foreign workspace and an invalid marker", () =>
        {
            var f = new Fixture(root, "clean-foreign");
            var cleaner = new GameCleaner(Path.Combine(f.Target, "other-workspace"), f.Processes);
            Refuses(() => cleaner.Inspect(f.Game));
            File.WriteAllText(f.Claim, "{\"schemaVersion\":1,\"workspaceRoot\":\"relative\",\"profileId\":\"bad\"}");
            Refuses(() => cleaner.Inspect(f.Game));
            Check(File.Exists(f.Claim) && Directory.Exists(f.Managed), "refusal changed the claim or deployment");
        }));

        tests.Add(("cleanup rechecks closed game and ownership contents after confirmation", () =>
        {
            var f = new Fixture(root, "clean-recheck");
            var cleaner = new GameCleaner(f.Workspace, f.Processes);
            var plan = cleaner.Inspect(f.Game);
            f.Processes.Running = true;
            Refuses(() => cleaner.Clean(plan));
            f.Processes.Running = false;
            f.WriteGame("BepInEx/plugins/SevoManaged/cleanup-fixture/Managed.dll", "external edit");
            Refuses(() => cleaner.Clean(plan));
            Check(File.Exists(f.Claim) && !Directory.Exists(plan.BackupDirectory), "failed preflight started cleanup");
            Check(f.Service.ListProfiles().Single().Id == f.ProfileId, "failed preflight removed the profile");
        }));

        tests.Add(("cleanup refuses changed owned runtime before moving the deployment", () =>
        {
            var f = new Fixture(root, "clean-runtime-edit");
            f.PrepareRuntime(externalLoader: false);
            var cleaner = new GameCleaner(f.Workspace, f.Processes);
            var plan = cleaner.Inspect(f.Game);
            var proxy = f.WriteGame("winhttp.dll", "changed runtime");
            Refuses(() => cleaner.Clean(plan));
            Check(File.ReadAllText(proxy) == "changed runtime" && Directory.Exists(f.Managed) && File.Exists(f.Claim), "failed runtime verification changed game files");
        }));

        tests.Add(("a launch handoff allows nested validation and excludes cleanup", () =>
        {
            var f = new Fixture(root, "clean-launch-lock");
            f.PrepareRuntime(externalLoader: false);
            var cleaner = new GameCleaner(f.Workspace, f.Processes);
            var plan = cleaner.Inspect(f.Game);
            using var launch = RuntimeProvisioner.LockGame(f.Game);
            new RuntimeProvisioner(f.Workspace, f.Processes).ValidatePrepared(f.Game, Path.Combine(f.Workspace, "runtime", f.ProfileId));
            Exception? failure = null;
            var other = new Thread(() =>
            {
                try { cleaner.Clean(plan); }
                catch (Exception ex) { failure = ex; }
            });
            other.Start();
            other.Join();
            Check(failure is InvalidOperationException, "cleanup was not excluded while a launch was pending");
            Check(File.Exists(f.Claim) && Directory.Exists(f.Managed), "the pending launch's deployment changed");
        }));

        tests.Add(("game cleanup preserves manually installed Keel", () =>
        {
            var f = new Fixture(root, "clean-manual-keel");
            f.PrepareRuntime(externalLoader: true, manualKeel: true);
            var cleaner = new GameCleaner(f.Workspace, f.Processes);
            cleaner.Clean(cleaner.Inspect(f.Game));
            Check(File.ReadAllText(Path.Combine(f.Game, "BepInEx/plugins/SevoPlatform/Sevo.Framework.Runtime.dll")) == "keel", "cleanup changed a manual Keel DLL");
            Check(File.ReadAllText(Path.Combine(f.Game, "BepInEx/plugins/SevoPlatform/Sevo.Framework.Sdk.dll")) == "sdk", "cleanup changed a manual SDK DLL");
        }));

        tests.Add(("cleanup refuses unresolved deployment and runtime journals", () =>
        {
            var f = new Fixture(root, "clean-pending");
            var cleaner = new GameCleaner(f.Workspace, f.Processes);
            var transaction = f.WriteGame("BepInEx/SevoTransactions/" + f.ProfileId + "/old/Mod.dll", "unresolved");
            Refuses(() => cleaner.Inspect(f.Game));
            File.Delete(transaction);
            Directory.Delete(Path.GetDirectoryName(transaction)!);
            var pending = f.WriteGame(".sevo-runtime-pending.json", "unresolved");
            Refuses(() => cleaner.Inspect(f.Game));
            Check(File.ReadAllText(pending) == "unresolved" && File.Exists(f.Claim) && Directory.Exists(f.Managed), "failed preflight touched a pending operation");
        }));

        tests.Add(("cleanup resumes after interruptions during archive and ownership release", () =>
        {
            foreach (var stop in new[] { "deployment-archived", "runtime-file-archived", "profile-released", "claim-archived" })
            {
                var f = new Fixture(root, "clean-resume-" + stop);
                f.PrepareRuntime(externalLoader: false);
                var cleaner = new GameCleaner(f.Workspace, f.Processes, point =>
                {
                    if (point == stop) throw new SimulatedInterruption();
                });
                var plan = cleaner.Inspect(f.Game);
                try { cleaner.Clean(plan); throw new Exception("cleanup did not reach its interruption point"); }
                catch (SimulatedInterruption) { }
                Refuses(() => f.Service.CreateProfile("too soon", f.Game));
                var fresh = new GameCleaner(f.Workspace, f.Processes);
                var pending = fresh.Inspect(f.Game);
                Check(pending.IsResuming && pending.BackupDirectory == plan.BackupDirectory, "retry did not recover the same backup");
                fresh.Clean(pending);
                Check(!File.Exists(f.Claim) && f.Service.ListProfiles().Count == 0, "retry left stale ownership");
                Check(File.ReadAllText(Path.Combine(plan.BackupDirectory, "SevoManaged/cleanup-fixture/Managed.dll")) == "managed payload", "retry lost archived bytes");
                Check(File.ReadAllText(Path.Combine(plan.BackupDirectory, "runtime/winhttp.dll")) == "loader", "retry lost archived owned runtime");
            }
        }));
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Refuses(Action operation)
    {
        try { operation(); }
        catch (Exception ex) when (ex is PlatformException or InvalidOperationException) { return; }
        throw new Exception("expected a safe refusal");
    }

    private sealed class SimulatedInterruption : Exception;

    private sealed class ProcessInspector(string game) : IGameProcessInspector
    {
        public bool Running { get; set; }
        public bool IsRunning(string gameDirectory)
        {
            Check(gameDirectory == game, "cleanup inspected the wrong game installation");
            return Running;
        }
    }

    private sealed class Fixture
    {
        public string Target { get; }
        public string Game { get; }
        public string Workspace { get; }
        public string ProfileId { get; }
        public string Claim => Path.Combine(Game, "BepInEx/sevo-profile-owner.json");
        public string Managed => Path.Combine(Game, "BepInEx/plugins/SevoManaged");
        public ProcessInspector Processes { get; }
        public PlatformService Service { get; }

        public Fixture(string root, string name)
        {
            Target = Path.Combine(root, name);
            Game = Path.Combine(Target, "game");
            Workspace = Path.Combine(Target, "workspace");
            Processes = new ProcessInspector(Game);
            WriteGame("Starship EVO.exe", "inert executable");
            WriteGame("Starship EVO_Data/Managed/Assembly-CSharp.dll", "inert assembly");
            var package = Path.Combine(Target, "package.zip");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                Entry(zip, "sevo.mod.json", """{"schemaVersion":1,"id":"cleanup-fixture","name":"Cleanup fixture","version":"1.0.0","payloadRoot":"payload"}""");
                Entry(zip, "payload/Managed.dll", "managed payload");
            }
            Service = new PlatformService(Workspace, Processes);
            Service.ImportPackage(package);
            ProfileId = Service.CreateProfile("cleanup fixture", Game).Id;
            Service.Install(ProfileId, "cleanup-fixture");
        }

        public string WriteGame(string path, string value)
        {
            var file = Path.Combine(Game, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, value);
            return file;
        }

        public void PrepareRuntime(bool externalLoader, bool manualKeel = false)
        {
            var loader = Path.Combine(Target, "loader.zip");
            using (var zip = ZipFile.Open(loader, ZipArchiveMode.Create))
            {
                Entry(zip, "winhttp.dll", "loader");
                Entry(zip, "doorstop_config.ini", "configuration");
                Entry(zip, "BepInEx/core/BepInEx.dll", "core");
                Entry(zip, "BepInEx/core/BepInEx.Preloader.dll", "preloader");
            }
            if (externalLoader) ZipFile.ExtractToDirectory(loader, Game);
            var framework = Path.Combine(Target, "framework");
            Directory.CreateDirectory(framework);
            File.WriteAllText(Path.Combine(framework, "Sevo.Framework.Runtime.dll"), "keel");
            File.WriteAllText(Path.Combine(framework, "Sevo.Framework.Sdk.dll"), "sdk");
            if (manualKeel)
            {
                WriteGame("BepInEx/plugins/SevoPlatform/Sevo.Framework.Runtime.dll", "keel");
                WriteGame("BepInEx/plugins/SevoPlatform/Sevo.Framework.Sdk.dll", "sdk");
            }
            new RuntimeProvisioner(Workspace, Processes).Prepare(Game, Path.Combine(Workspace, "runtime", ProfileId), loader, framework);
        }

        private static void Entry(ZipArchive zip, string path, string value)
        {
            using var writer = new StreamWriter(zip.CreateEntry(path).Open());
            writer.Write(value);
        }
    }
}
