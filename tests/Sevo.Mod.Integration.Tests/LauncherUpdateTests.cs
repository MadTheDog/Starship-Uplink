using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

internal static class LauncherUpdateTests
{
    const string ArchiveName = "Starship-Uplink-0.2.0-win-x64";
    const string Exe = "Starship Uplink.exe";
    const string Core = "Sevo.Mod.Core.dll";
    const string Sums = "SHA256SUMS.txt";
    const string ReleasePage = "https://github.com/MadTheDog/Starship-Uplink/releases/tag/v0.2.0";
    const string Assets = "https://github.com/MadTheDog/Starship-Uplink/releases/download/v0.2.0/";

    public static void Register(List<(string Name, Action Run)> tests, string root)
    {
        tests.Add(("Uplink selects a newer stable Windows release with official SHA-256", () =>
        {
            using var http = new HttpClient(new ReleaseHandler(Archive(Package("new")), useSidecar: true));
            var offer = LauncherUpdater.CheckAsync(http, new Version(0, 1, 2)).GetAwaiter().GetResult();
            Check(offer?.Version == new Version(0, 2, 0) && offer.ReleaseUrl.ToString() == ReleasePage,
                "The stable Windows release with an official checksum was not selected.");
            Check(LauncherUpdater.CheckAsync(http, new Version(0, 2, 0, 0)).GetAwaiter().GetResult() is null,
                "An already installed release was offered again.");
            using var noSha = new HttpClient(new ReleaseHandler(Archive(Package("new")), provideSha: false));
            Refuses(() => LauncherUpdater.CheckAsync(noSha, new Version(0, 1, 2)).GetAwaiter().GetResult());
        }));

        tests.Add(("Uplink refuses unsafe or unverified update ZIPs before replacing files", () =>
        {
            foreach (string invalidEntry in new[] { "../outside.txt", "data/workspace.json", "STARSHIP UPLINK.EXE" })
            {
                var f = Fixture(root);
                var archive = Archive(Package("new"), invalidEntry);
                Refuses(() => Stage(f.App, f.Workspace, archive));
                CheckOld(f.App);
                Check(!File.Exists(Path.Combine(f.App, "outside.txt")), "A traversal entry escaped the staging path.");
            }
            var fixture = Fixture(root);
            Refuses(() => Stage(fixture.App, fixture.Workspace, Archive(Package("new")), badDigest: true));
            CheckOld(fixture.App);

            var badInventory = Package("new");
            badInventory[Core] = Bytes("New bytes written after SHA256SUMS.txt was generated");
            Refuses(() => Stage(fixture.App, fixture.Workspace, Archive(badInventory)));
            CheckOld(fixture.App);
        }));

        tests.Add(("Uplink refuses changed managed files and unowned destination collisions", () =>
        {
            var f = Fixture(root);
            File.WriteAllText(Path.Combine(f.App, Core), "locally edited");
            Refuses(() => Stage(f.App, f.Workspace, Archive(Package("new"))));
            Check(File.ReadAllText(Path.Combine(f.App, Core)) == "locally edited", "A locally edited managed file changed.");

            f = Fixture(root);
            File.WriteAllText(Path.Combine(f.App, "README.md"), "user-owned README");
            Refuses(() => Stage(f.App, f.Workspace, Archive(Package("new", readme: true))));
            Check(File.ReadAllText(Path.Combine(f.App, "README.md")) == "user-owned README", "An unowned collision changed.");
        }));

        tests.Add(("Uplink applies a verified package while preserving workspace and unowned files", () =>
        {
            var f = Fixture(root);
            File.WriteAllText(Path.Combine(f.App, "keep.txt"), "user-owned file");
            File.WriteAllText(Path.Combine(f.Workspace, "profile.json"), "user workspace");
            var planPath = Stage(f.App, f.Workspace, Archive(Package("new", readme: true)), useSidecar: true);
            CheckOld(f.App);
            var applied = LauncherUpdater.Apply(planPath);
            Check(applied.Failure is null, "A successful update reported failure.");
            var restart = applied.Restart;
            Check(restart.FileName == Path.Combine(f.App, Exe) && restart.ArgumentList.SequenceEqual(new[] { "--workspace", f.Workspace }),
                "Restart does not point to the original package and workspace.");
            CheckNew(f.App);
            Check(File.ReadAllText(Path.Combine(f.App, "README.md")) == "new README.md", "A newly managed file was omitted.");
            Check(File.ReadAllText(Path.Combine(f.App, "keep.txt")) == "user-owned file", "An unrelated file changed.");
            Check(File.ReadAllText(Path.Combine(f.Workspace, "profile.json")) == "user workspace", "Workspace bytes changed.");
            Check(LauncherUpdater.GetRecoveryStartInfo(f.App) is null, "Committed update remains pending.");
        }));

        tests.Add(("Uplink resumes an interrupted replacement using its staged payload and durable phase", () =>
        {
            var f = Fixture(root);
            var planPath = Stage(f.App, f.Workspace, Archive(Package("new")));
            using var plan = JsonDocument.Parse(File.ReadAllText(planPath));
            var transaction = Path.Combine(Path.GetDirectoryName(planPath)!, plan.RootElement.GetProperty("Transaction").GetString()!);
            File.WriteAllText(Path.Combine(transaction, "phase"), "applying");
            File.WriteAllText(Path.Combine(f.App, Core), "new " + Core);
            var recovery = LauncherUpdater.GetRecoveryStartInfo(f.App);
            Check(recovery is not null && recovery.ArgumentList.SequenceEqual(new[] { "--apply-launcher-update", planPath }),
                "The pending transaction has no recovery entry point.");
            LauncherUpdater.Apply(planPath);
            CheckNew(f.App);
            Check(LauncherUpdater.GetRecoveryStartInfo(f.App) is null, "Recovered update remains pending.");
        }));

        if (OperatingSystem.IsWindows())
            tests.Add(("Uplink rolls back an ordinary Windows replacement failure", () =>
            {
                var f = Fixture(root);
                var planPath = Stage(f.App, f.Workspace, Archive(Package("new")));
                LauncherUpdater.UpdateResult rolledBack;
                using (File.Open(Path.Combine(f.App, Exe), FileMode.Open, FileAccess.Read, FileShare.Read))
                    rolledBack = LauncherUpdater.Apply(planPath);
                Check(rolledBack.Failure is not null, "The failed update did not report a rollback.");
                Check(rolledBack.Restart.FileName == Path.Combine(f.App, Exe) &&
                    rolledBack.Restart.ArgumentList.SequenceEqual(new[] { "--workspace", f.Workspace }),
                    "Rollback did not reopen the prior launcher with the same workspace.");
                CheckOld(f.App);
                Check(LauncherUpdater.GetRecoveryStartInfo(f.App) is null, "Rolled-back update remains pending.");
            }));
    }

    public static void RegisterKeelOverride(List<(string Name, Action Run)> tests, string root, string officialKeelZip)
    {
        tests.Add(("Uplink upgrades a verified alternate Keel bundle and refuses unofficial edits", () =>
        {
            byte[] keelArchive = File.ReadAllBytes(officialKeelZip);
            var version = Version.Parse(Path.GetFileName(officialKeelZip).Split('-')[2]);
            var payload = KeelUpdater.ReadArchive(officialKeelZip, Digest(keelArchive), version);
            var next = Package("new");
            next.Remove(Sums);
            for (int i = 0; i < KeelUpdater.Files.Length; i++)
                next["framework/" + KeelUpdater.Files[i]] = payload[i];
            next.Add(Sums, Bytes(string.Join('\n', next.Select(file => Digest(file.Value) + "  " + file.Key)) + "\n"));

            foreach (bool unofficial in new[] { false, true })
            {
                var f = Fixture(root);
                var paths = KeelUpdater.Files.Select(file => Path.Combine(f.App, "framework", file)).ToArray();
                for (int i = 0; i < paths.Length; i++)
                    File.WriteAllBytes(paths[i], unofficial && i == 0 ? [..payload[i], 42] : payload[i]);
                var oldInventory = File.ReadAllBytes(Path.Combine(f.App, Sums));
                using var http = new HttpClient(new ReleaseHandler(Archive(next), keelZip: keelArchive, keelVersion: version));
                var offer = LauncherUpdater.CheckAsync(http, new Version(0, 1, 7)).GetAwaiter().GetResult()!;
                string StageOverride() => LauncherUpdater.StageAsync(http, offer, f.App, f.Workspace).GetAwaiter().GetResult().ArgumentList[1];

                if (unofficial)
                {
                    Refuses(() => StageOverride());
                    Check(File.ReadAllBytes(Path.Combine(f.App, Sums)).SequenceEqual(oldInventory), "Unofficial DLL changed the inventory.");
                    Check(File.ReadAllText(Path.Combine(f.App, Core)) == "old " + Core, "Unofficial DLL allowed replacement.");
                    continue;
                }
                var plan = StageOverride();
                Check(LauncherUpdater.GetRecoveryStartInfo(f.App) != null, "Override plan cannot be resumed.");
                if (OperatingSystem.IsWindows())
                {
                    LauncherUpdater.UpdateResult failed;
                    using (File.Open(Path.Combine(f.App, Exe), FileMode.Open, FileAccess.Read, FileShare.Read))
                        failed = LauncherUpdater.Apply(plan);
                    Check(failed.Failure != null && File.ReadAllBytes(Path.Combine(f.App, Sums)).SequenceEqual(oldInventory),
                        "Rollback did not restore the original launcher inventory.");
                    Check(paths.Select(File.ReadAllBytes).Zip(payload).All(pair => pair.First.SequenceEqual(pair.Second)),
                        "Rollback changed the user's Keel pair.");
                    plan = StageOverride();
                }
                var result = LauncherUpdater.Apply(plan);
                Check(result.Failure == null && File.ReadAllText(Path.Combine(f.App, Core)) == "new " + Core,
                    "Upgrade with official override failed.");
                Check(result.Restart.ArgumentList[1] == f.Workspace, "The upgrade lost the workspace.");
            }
        }));
    }

    static string Stage(string app, string workspace, byte[] archive, bool useSidecar = false, bool badDigest = false)
    {
        using var http = new HttpClient(new ReleaseHandler(archive, useSidecar, badDigest: badDigest));
        var offer = LauncherUpdater.CheckAsync(http, new Version(0, 1, 2)).GetAwaiter().GetResult()!;
        return LauncherUpdater.StageAsync(http, offer, app, workspace).GetAwaiter().GetResult().ArgumentList[1];
    }

    static (string App, string Workspace) Fixture(string root)
    {
        string app = Path.Combine(root, "uplink-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(app, "data", "profile with spaces");
        Directory.CreateDirectory(workspace);
        foreach (var (path, contents) in Package("old"))
        {
            string destination = Path.Combine(app, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, contents);
        }
        return (app, workspace);
    }

    static Dictionary<string, byte[]> Package(string version, bool readme = false)
    {
        var files = new[] { Exe, Core, "Sevo.Mod.Hosting.dll", "Starship Uplink.dll", "Starship Uplink.deps.json",
            "Starship Uplink.runtimeconfig.json", "cli/Sevo.Mod.Cli.exe", "cli/Sevo.Mod.Cli.dll",
            "cli/Sevo.Mod.Cli.deps.json", "cli/Sevo.Mod.Cli.runtimeconfig.json", "cli/Sevo.Mod.Core.dll",
            "framework/Sevo.Framework.Runtime.dll", "framework/Sevo.Framework.Sdk.dll" }
            .ToDictionary(path => path, path => Bytes(version + " " + path));
        if (readme) files.Add("README.md", Bytes(version + " README.md"));
        files.Add(Sums, Bytes(string.Join('\n', files.Select(file => Digest(file.Value) + "  " + file.Key)) + "\n"));
        return files;
    }

    static byte[] Archive(Dictionary<string, byte[]> files, string? additionalEntry = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, contents) in files)
            {
                using var entry = zip.CreateEntry(ArchiveName + "/" + path).Open();
                entry.Write(contents);
            }
            if (additionalEntry is not null)
            {
                using var entry = zip.CreateEntry(ArchiveName + "/" + additionalEntry).Open();
                entry.Write(Bytes("unowned bytes"));
            }
        }
        return stream.ToArray();
    }

    static void CheckOld(string app) => CheckPackage(app, "old");
    static void CheckNew(string app) => CheckPackage(app, "new");
    static void CheckPackage(string app, string version)
    {
        foreach (string path in new[] { Exe, Core, "Starship Uplink.dll", "framework/Sevo.Framework.Runtime.dll" })
            Check(File.ReadAllText(Path.Combine(app, path)) == version + " " + path, "Unexpected managed bytes at " + path);
    }

    static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Refuses(Action action)
    {
        try { action(); }
        catch (PlatformException) { return; }
        throw new Exception("Expected the updater to refuse the operation.");
    }

    sealed class ReleaseHandler(byte[] archive, bool useSidecar = false, bool provideSha = true, bool badDigest = false, byte[]? keelZip = null, Version? keelVersion = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string digest = badDigest ? new string('a', 64) : Digest(archive);
            string zip = ArchiveName + ".zip";
            string sidecar = ArchiveName + ".sha256.txt";
            string url = request.RequestUri!.AbsoluteUri;
            HttpContent content;
            if (url == "https://api.github.com/repos/MadTheDog/Starship-Uplink/releases/latest")
            {
                var assets = new List<object>
                {
                    new { name = zip, browser_download_url = Assets + zip, digest = provideSha && !useSidecar ? "sha256:" + digest : null }
                };
                if (provideSha && useSidecar)
                    assets.Add(new { name = sidecar, browser_download_url = Assets + sidecar, digest = (string?)null });
                content = new StringContent(JsonSerializer.Serialize(new { tag_name = "v0.2.0", html_url = ReleasePage, prerelease = false, assets }));
            }
            else if (url == Assets + zip) content = new ByteArrayContent(archive);
            else if (url == Assets + sidecar && provideSha && useSidecar) content = new StringContent(digest + "  " + zip + "\n");
            else if (keelZip != null && url == $"https://api.github.com/repos/MadTheDog/Starship-Keel/releases/tags/v{keelVersion}")
            {
                var tag = "v" + keelVersion;
                var name = $"Starship-Keel-{keelVersion}-BepInEx.zip";
                content = new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = tag, html_url = $"https://github.com/MadTheDog/Starship-Keel/releases/tag/{tag}",
                    draft = false, prerelease = false,
                    assets = new[] { new { name, browser_download_url = $"https://github.com/MadTheDog/Starship-Keel/releases/download/{tag}/{name}", digest = "sha256:" + Digest(keelZip) } }
                }));
            }
            else if (keelZip != null && url == $"https://github.com/MadTheDog/Starship-Keel/releases/download/v{keelVersion}/Starship-Keel-{keelVersion}-BepInEx.zip")
                content = new ByteArrayContent(keelZip);
            else throw new InvalidOperationException("Unexpected GitHub request: " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }
}
