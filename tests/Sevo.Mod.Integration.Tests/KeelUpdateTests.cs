using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

internal static class KeelUpdateTests
{
    const string Releases = "https://github.com/MadTheDog/Starship-Keel/releases/";

    public static void Register(List<(string Name, Action Run)> tests)
    {
        tests.Add(("Keel offers the newest published preview with an official checksum and ignores drafts", () =>
        {
            using var http = new HttpClient(new ReleaseHandler());
            var offer = KeelUpdater.CheckAsync(http, new Version(0, 1, 2)).GetAwaiter().GetResult();
            if (offer?.Version != new Version(0, 1, 5) || !offer.IsPreview)
                throw new Exception("A newer official Keel runtime was not offered.");
            foreach (var current in new[] { new Version(0, 1, 5, 0), new Version(0, 2, 0) })
                if (KeelUpdater.CheckAsync(http, current).GetAwaiter().GetResult() != null)
                    throw new Exception("An equal or older release was offered.");
            using var noChecksum = new HttpClient(new ReleaseHandler(includeChecksum: false));
            try { KeelUpdater.CheckAsync(noChecksum, new Version(0, 1, 2)).GetAwaiter().GetResult(); }
            catch (PlatformException) { return; }
            throw new Exception("An unverified Keel release was offered.");
        }));
    }

    public static void RegisterArtifacts(List<(string Name, Action Run)> tests, string oldArchive, string newArchive,
        string checksumFile, Func<string, (string Game, string State, string Zip, string Framework)> fixture)
    {
        tests.Add(("Keel updates the selected managed profile and recovers a partially replaced DLL pair", () =>
        {
            var f = fixture("keel-release-update");
            var workspace = Path.Combine(Path.GetDirectoryName(f.Game)!, "workspace");
            var profile = new PlatformService(workspace).CreateProfile("Keel update fixture", f.Game);
            var state = Path.Combine(workspace, "runtime", profile.Id);
            var provisioner = new RuntimeProvisioner(workspace);
            var oldZip = ZipFile.OpenRead(oldArchive);
            using (oldZip)
                foreach (var file in KeelUpdater.Files)
                    oldZip.GetEntry(KeelUpdater.PluginDirectory + file)!.ExtractToFile(Path.Combine(f.Framework, file), overwrite: true);
            provisioner.Prepare(f.Game, state, f.Zip, f.Framework);
            Check(RuntimeProvisioner.GetManagedKeelVersion(f.Game) == new Version(0, 1, 0), "The installed release was not identified.");

            var config = Path.Combine(f.Game, "BepInEx/config/BepInEx.cfg");
            File.AppendAllText(config, "# user setting\n");
            var beforeConfig = File.ReadAllBytes(config);
            var mod = Path.Combine(f.Game, "BepInEx/plugins/manual-mod.dll");
            File.WriteAllText(mod, "manual mod bytes");

            var archive = File.ReadAllBytes(newArchive);
            var newVersion = Version.Parse(Path.GetFileName(newArchive).Split('-')[2]);
            var checksum = File.ReadAllBytes(checksumFile);
            using var badHttp = new HttpClient(new ReleaseHandler(archive: [1, 2, 3], checksum: checksum, version: newVersion.ToString()));
            var offer = KeelUpdater.CheckAsync(badHttp, new Version(0, 1, 0)).GetAwaiter().GetResult()!;
            try
            {
                KeelUpdater.ApplyAsync(badHttp, offer, provisioner, f.Game, state, Path.Combine(workspace, "downloads")).GetAwaiter().GetResult();
                throw new Exception("A corrupt download was installed.");
            }
            catch (PlatformException) { }
            Check(RuntimeProvisioner.GetManagedKeelVersion(f.Game) == new Version(0, 1, 0), "A corrupt download changed the game.");

            using var http = new HttpClient(new ReleaseHandler(archive: archive, checksum: checksum, version: newVersion.ToString()));
            var download = Path.Combine(workspace, "downloads");
            var sdk = Path.Combine(f.Game, KeelUpdater.PluginDirectory, KeelUpdater.Files[1]);
            if (OperatingSystem.IsWindows())
            {
                using (File.Open(sdk, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        KeelUpdater.ApplyAsync(http, offer, provisioner, f.Game, state, download).GetAwaiter().GetResult();
                        throw new Exception("Expected Windows to refuse replacing a locked DLL.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    Check(File.Exists(Path.Combine(f.Game, RuntimeProvisioner.PendingKeelName)), "No recovery record was retained.");
                    Check(RuntimeProvisioner.GetManagedKeelVersion(f.Game) == null, "A partial update was advertised as a complete version.");
                }
                provisioner.ValidatePrepared(f.Game, state);
            }
            else KeelUpdater.ApplyAsync(http, offer, provisioner, f.Game, state, download).GetAwaiter().GetResult();

            Check(RuntimeProvisioner.GetManagedKeelVersion(f.Game) == newVersion, "The new Keel release was not committed.");
            Check(!File.Exists(Path.Combine(f.Game, RuntimeProvisioner.PendingKeelName)), "The committed update still appears pending.");
            provisioner.Prepare(f.Game, state, f.Zip, f.Framework);
            Check(RuntimeProvisioner.GetManagedKeelVersion(f.Game) == newVersion, "Prepare downgraded the updated Keel.");
            Check(File.ReadAllBytes(config).SequenceEqual(beforeConfig) && File.ReadAllText(mod) == "manual mod bytes",
                "The update changed user configuration or an unrelated plugin.");

            provisioner.Remove(f.Game, state);
            ZipFile.ExtractToDirectory(oldArchive, f.Game);
            provisioner.Prepare(f.Game, state, f.Zip, f.Framework);
            Check(RuntimeProvisioner.GetManagedKeelVersion(f.Game) == null, "Manually installed Keel was advertised as managed.");
            try
            {
                KeelUpdater.ApplyAsync(http, offer, provisioner, f.Game, state, download).GetAwaiter().GetResult();
                throw new Exception("A manual Keel installation was overwritten.");
            }
            catch (PlatformException) { }
            using var manual = ZipFile.OpenRead(oldArchive);
            foreach (var file in KeelUpdater.Files)
            {
                using var input = manual.GetEntry(KeelUpdater.PluginDirectory + file)!.Open();
                using var live = File.OpenRead(Path.Combine(f.Game, KeelUpdater.PluginDirectory, file));
                Check(SHA256.HashData(input).SequenceEqual(SHA256.HashData(live)), "A manually installed DLL changed.");
            }
        }));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    sealed class ReleaseHandler(bool includeChecksum = true, byte[]? archive = null, byte[]? checksum = null,
        string version = "0.1.5") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var address = request.RequestUri?.AbsoluteUri;
            var tag = "v" + version;
            var basename = $"Starship-Keel-{version}-BepInEx";
            if (address == Releases + "download/" + tag + "/" + basename + ".zip" && archive != null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(archive) });
            if (address == Releases + "download/" + tag + "/" + basename + ".sha256.txt" && checksum != null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(checksum) });
            if (address != "https://api.github.com/repos/MadTheDog/Starship-Keel/releases?per_page=30&page=1")
                throw new Exception("Unexpected release request: " + request.RequestUri);
            var names = includeChecksum ? new[] { basename + ".zip", basename + ".sha256.txt", $"Starship-Keel-{version}.zip" } : [basename + ".zip"];
            var body = JsonSerializer.Serialize(new[] {
                new { tag_name = "v2.0.0", html_url = Releases + "tag/v2.0.0", prerelease = false, draft = true, assets = Array.Empty<object>() },
                new { tag_name = tag, html_url = Releases + "tag/" + tag, prerelease = true, draft = false,
                    assets = names.Select(name => (object)new { name, browser_download_url = Releases + "download/" + tag + "/" + name }).ToArray() }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) });
        }
    }
}
