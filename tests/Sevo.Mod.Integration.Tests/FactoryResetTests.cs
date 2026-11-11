using System.IO.Compression;
using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

internal static class FactoryResetTests
{
    internal static void Add(List<(string Name, Action Run)> tests, string root,
        Func<string, (string Game, string State, string Zip, string Framework)> fixture)
    {
        foreach (var damage in new[] { "deleted-bepinex", "modified-runtime", "corrupt-records", "interrupted-deployment" })
            tests.Add(($"factory reset recovers {damage} and preserves game/settings/other profiles/library", () =>
            {
                var f = fixture("factory-" + damage);
                var workspace = Path.Combine(root, "factory-store-" + damage);
                var service = new PlatformService(workspace);
                var profile = service.CreateProfile("My mods", f.Game);
                var other = fixture("factory-other-" + damage);
                var otherProfile = service.CreateProfile("Other", other.Game);
                var package = CreatePackage(workspace);
                var imported = service.ImportPackage(package);
                service.Install(profile.Id, imported.Id, imported.Version);
                service.Install(otherProfile.Id, imported.Id, imported.Version);
                var state = Path.Combine(workspace, "runtime", profile.Id);
                new RuntimeProvisioner(workspace).Prepare(f.Game, state, f.Zip, f.Framework);
                Write(f.Game, "BepInEx/plugins/manual/plugin.dll", "manual mod");
                Write(f.Game, "BepInEx/config/manual.cfg", "mod settings");
                Write(f.Game, "doorstop_libs/dependency.dll", "old dependency");
                Write(f.Game, "settings.json", "game settings");
                Write(f.Game, "Saves/world.sav", "precious save");
                Write(state, "allocations.json", "old mod identities");
                var protectedFiles = new[] { "Starship EVO.exe", "Starship EVO_Data/Managed/Assembly-CSharp.dll", "settings.json", "Saves/world.sav" }
                    .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(f.Game, path)));
                if (damage == "deleted-bepinex") Directory.Delete(Path.Combine(f.Game, "BepInEx"), true);
                if (damage == "modified-runtime") Write(f.Game, "BepInEx/core/BepInEx.dll", "wrong hash");
                if (damage == "corrupt-records")
                    foreach (var path in new[] { ".sevo-runtime-owned.json", ".sevo-runtime-pending.json", ".sevo-keel-update.json", "BepInEx/sevo-profile-owner.json", "BepInEx/sevo-cleanup-pending.json" }) Write(f.Game, path, "{broken");
                if (damage == "interrupted-deployment")
                {
                    var interrupted = new PlatformService(workspace, new Stopped(), point =>
                    {
                        if (point == "intent-written") throw new IOException("simulated interruption");
                    });
                    try { interrupted.Remove(profile.Id, imported.Id); } catch (IOException) { }
                    Require(File.Exists(Path.Combine(workspace, "operation.json")), "fixture did not interrupt deployment");
                    Directory.Delete(Path.Combine(f.Game, "BepInEx"), true);
                }

                var reset = new FactoryResetter(workspace).Reset(profile.Id, f.Zip, f.Framework);
                Require(!reset.ReusedLoader, "reset reused the damaged loader");
                new RuntimeProvisioner(workspace).ValidatePrepared(f.Game, state);
                var after = service.GetProfile(profile.Id);
                Require(after.Mods.Count == 0 && after.Name == "My mods", "profile was not reset");
                Require(service.ValidateProfile(profile.Id).Count == 0, "reset profile is not usable");
                Require(service.GetProfile(otherProfile.Id).Mods.Count == 1, "other profile changed");
                Require(service.ValidateProfile(otherProfile.Id).Count == 0, "other deployment changed");
                Require(service.ListPackages().Count == 1, "shared package library was erased");
                Require(!File.Exists(Path.Combine(f.Game, "BepInEx/plugins/manual/plugin.dll")), "manual mod survived");
                Require(!File.Exists(Path.Combine(f.Game, "BepInEx/config/manual.cfg")), "mod settings survived");
                Require(!Directory.Exists(Path.Combine(f.Game, "doorstop_libs")), "old loader dependency survived");
                Require(!File.Exists(Path.Combine(state, "allocations.json")), "old runtime state survived");
                foreach (var file in protectedFiles) Require(File.ReadAllBytes(Path.Combine(f.Game, file.Key)).SequenceEqual(file.Value), "game/save/settings changed: " + file.Key);
                new FactoryResetter(workspace).Reset(profile.Id, f.Zip, f.Framework);
                new RuntimeProvisioner(workspace).ValidatePrepared(f.Game, state);
            }));

        tests.Add(("factory reset retries after interrupted deletion and blocks normal preparation", () =>
        {
            var f = fixture("factory-interrupt");
            var workspace = Path.Combine(root, "factory-interrupt-store");
            var service = new PlatformService(workspace);
            var profile = service.CreateProfile("Reset", f.Game);
            Write(f.Game, "BepInEx/plugins/manual.dll", "old mod");
            bool interrupted = false;
            try { new FactoryResetter(workspace, checkpoint: point => { if (point == "loader-removed") throw new IOException("interrupted"); }).Reset(profile.Id, f.Zip, f.Framework); }
            catch (IOException) { interrupted = true; }
            Require(interrupted, "reset did not reach deletion checkpoint");
            ExpectFailure(() => new RuntimeProvisioner(workspace).Prepare(f.Game, Path.Combine(workspace, "runtime", profile.Id), f.Zip, f.Framework));
            new FactoryResetter(workspace).Reset(profile.Id, f.Zip, f.Framework);
            Require(service.ValidateProfile(profile.Id).Count == 0, "retry failed to restore profile");
        }));

        tests.Add(("factory reset keeps recovery pending when replacement verification fails", () =>
        {
            var f = fixture("factory-verification");
            var workspace = Path.Combine(root, "factory-verification-store");
            var profile = new PlatformService(workspace).CreateProfile("Reset", f.Game);
            var reset = new FactoryResetter(workspace, checkpoint: point =>
            {
                if (point == "runtime-installed")
                    File.Delete(Path.Combine(f.Game, "BepInEx/core/BepInEx.dll"));
            });
            ExpectFailure(() => reset.Reset(profile.Id, f.Zip, f.Framework));
            Require(File.Exists(Path.Combine(f.Game, ".sevo-factory-reset.json")), "reset cleared its recovery marker before verification");
            new FactoryResetter(workspace).Reset(profile.Id, f.Zip, f.Framework);
        }));

        tests.Add(("factory reset preserves an existing root changelog and leaves runtime preparable", () =>
        {
            var f = fixture("factory-changelog");
            var workspace = Path.Combine(root, "factory-changelog-store");
            var profile = new PlatformService(workspace).CreateProfile("Reset", f.Game);
            using (var archive = ZipFile.Open(f.Zip, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(archive.CreateEntry("changelog.txt").Open()))
                writer.Write("BepInEx release notes");
            Write(f.Game, "changelog.txt", "Existing installation changelog");

            var reset = new FactoryResetter(workspace).Reset(profile.Id, f.Zip, f.Framework);
            Require(File.ReadAllText(Path.Combine(f.Game, "changelog.txt")) == "Existing installation changelog",
                "reset changed the existing changelog");
            new RuntimeProvisioner(workspace).Prepare(f.Game, reset.StateDirectory, f.Zip, f.Framework);
        }));

        tests.Add(("factory reset validates replacement before deletion", () =>
        {
            var f = fixture("factory-invalid"); var workspace = Path.Combine(root, "factory-invalid-store");
            var profile = new PlatformService(workspace).CreateProfile("Reset", f.Game);
            Write(f.Game, "BepInEx/plugins/manual.dll", "keep until payload valid");
            File.Delete(Path.Combine(f.Framework, "Sevo.Framework.Sdk.dll"));
            ExpectFailure(() => new FactoryResetter(workspace).Reset(profile.Id, f.Zip, f.Framework));
            Require(File.ReadAllText(Path.Combine(f.Game, "BepInEx/plugins/manual.dll")) == "keep until payload valid", "deletion preceded preflight");
        }));

        tests.Add(("factory reset refuses a running game without deleting mods", () =>
        {
            var f = fixture("factory-running"); var workspace = Path.Combine(root, "factory-running-store");
            var profile = new PlatformService(workspace).CreateProfile("Reset", f.Game);
            Write(f.Game, "BepInEx/plugins/manual.dll", "keep");
            ExpectFailure(() => new FactoryResetter(workspace, new Running()).Reset(profile.Id, f.Zip, f.Framework));
            Require(File.Exists(Path.Combine(f.Game, "BepInEx/plugins/manual.dll")), "running game's mod removed");
        }));

        tests.Add(("factory reset deletes read-only mods without following junctions", () =>
        {
            var f = fixture("factory-links"); var workspace = Path.Combine(root, "factory-links-store");
            var profile = new PlatformService(workspace).CreateProfile("Reset", f.Game);
            Write(f.Game, "BepInEx/plugins/readonly.dll", "mod");
            File.SetAttributes(Path.Combine(f.Game, "BepInEx/plugins/readonly.dll"), FileAttributes.ReadOnly);
            var outside = Path.Combine(root, "outside-reset"); Write(outside, "save.txt", "untouched");
            var link = Path.Combine(f.Game, "BepInEx/plugins/linked");
            if (OperatingSystem.IsWindows())
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                { Arguments = $"/c mklink /J \"{link}\" \"{outside}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
                process!.WaitForExit(); Require(process.ExitCode == 0, "junction fixture creation failed");
            }
            else Directory.CreateSymbolicLink(link, outside);
            new FactoryResetter(workspace).Reset(profile.Id, f.Zip, f.Framework);
            Require(File.ReadAllText(Path.Combine(outside, "save.txt")) == "untouched", "reset followed a link");
        }));
    }

    private static string CreatePackage(string root)
    {
        var path = Path.Combine(root, "sample.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("sevo.mod.json").Open()))
            writer.Write("""{"schemaVersion":1,"id":"factory.sample","name":"Sample","version":"1.0.0","payloadRoot":"payload"}""");
        using (var writer = new StreamWriter(zip.CreateEntry("payload/sample.dll").Open())) writer.Write("sample mod");
        return path;
    }
    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; } catch (PlatformException) { return; }
        throw new Exception("Expected refusal");
    }
    private sealed class Stopped : IGameProcessInspector { public bool IsRunning(string game) => false; }
    private sealed class Running : IGameProcessInspector { public bool IsRunning(string game) => true; }
}
