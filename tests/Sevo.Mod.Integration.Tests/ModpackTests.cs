using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

internal static class ModpackTests
{
    public static void Verify(string game, string root)
    {
        var files = new[]
        {
            Package("dependent", "Owner/Dependent", "build-42", new() { ["base-mod"] = ">=1.0.0" }),
            Package("base-mod", "Other/Base", "v1.0.0", new()),
            Package("optional", "Other/Base", "v1.0.0", new())
        };
        var source = new PlatformService(Path.Combine(root, "source"));
        foreach (var file in files)
        {
            var zip = Path.Combine(root, file.Entry.Asset);
            File.WriteAllBytes(zip, file.Zip);
            source.ImportPackage(zip, new() { Repository = file.Entry.Repository, AssetName = file.Entry.Asset });
        }
        var sourceProfile = source.CreateProfile("Shareable", game);
        source.InstallBatch(sourceProfile.Id, files.Select(file => new PackageSelection(file.Entry.Id, file.Entry.Version)).ToArray());
        sourceProfile = source.SetEnabled(sourceProfile.Id, "optional", false);
        using var http = new HttpClient(new GitHubFixture(files));
        var client = new GitHubReleaseClient(http);
        var pack = ModpackDownloads.ExportAsync(client, sourceProfile, source.ListPackages()).GetAwaiter().GetResult();
        var path = Path.Combine(root, "share.uplink-pack.json");
        pack.Save(path);
        pack = Modpack.Read(path);
        Check(pack.Mods.First().Tag == "build-42", "Export inferred a release tag from the mod version.");
        Check(pack.Mods.All(entry => entry.Id != "optional" || !entry.Enabled), "Export lost the disabled state.");

        var targetGame = Path.Combine(root, "target-game");
        Directory.CreateDirectory(Path.Combine(targetGame, "Starship EVO_Data", "Managed"));
        File.WriteAllText(Path.Combine(targetGame, "Starship EVO.exe"), "fixture");
        File.WriteAllText(Path.Combine(targetGame, "Starship EVO_Data", "Managed", "Assembly-CSharp.dll"), "fixture");
        var target = new PlatformService(Path.Combine(root, "target"));
        var profile = target.CreateProfile("Import target", targetGame);

        var incorrect = pack with { Mods = [pack.Mods[0], pack.Mods[1] with { Sha256 = new string('0', 64) }] };
        try
        {
            ModpackDownloads.ImportAsync(client, target, profile, incorrect, Path.Combine(root, "downloads")).GetAwaiter().GetResult();
            throw new Exception("Changed release bytes were accepted.");
        }
        catch (PlatformException) { }
        Check(target.GetProfile(profile.Id).Generation == profile.Generation && target.GetProfile(profile.Id).Mods.Count == 0,
            "The failed modpack changed the profile.");

        var applied = ModpackDownloads.ImportAsync(client, target, profile, pack, Path.Combine(root, "downloads")).GetAwaiter().GetResult();
        Check(applied.Mods.Count == 3 && applied.Mods.Single(mod => mod.PackageId == "dependent").Enabled
            && !applied.Mods.Single(mod => mod.PackageId == "optional").Enabled, "The imported profile did not match the pack.");
        Check(target.ValidateProfile(profile.Id).Count == 0, "The imported deployment did not validate.");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private static (ModpackEntry Entry, byte[] Zip) Package(string id, string repository, string tag, Dictionary<string, string> dependencies)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var manifest = new StreamWriter(zip.CreateEntry("sevo.mod.json").Open()))
                manifest.Write(JsonSerializer.Serialize(new { schemaVersion = 1, id, name = id,
                    version = "1.0.0", payloadRoot = "payload", dependencies }));
            using var dll = new StreamWriter(zip.CreateEntry("payload/Test.dll").Open());
            dll.Write(id);
        }
        var bytes = memory.ToArray();
        return (new(id, "1.0.0", repository, tag, id + ".zip", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), true), bytes);
    }

    private sealed class GitHubFixture((ModpackEntry Entry, byte[] Zip)[] files) : HttpMessageHandler
    {
        private static string AssetUrl(ModpackEntry entry) =>
            $"https://github.com/{entry.Repository}/releases/download/{entry.Tag}/{entry.Asset}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            foreach (var file in files)
                if (url == AssetUrl(file.Entry))
                    return Reply(request, new ByteArrayContent(file.Zip));

            foreach (var group in files.GroupBy(file => (file.Entry.Repository, file.Entry.Tag)))
            {
                var (repo, tag) = group.Key;
                var release = new
                {
                    tag_name = tag, html_url = $"https://github.com/{repo}/releases/tag/{tag}",
                    prerelease = false, draft = false,
                    assets = group.Select(file => new { name = file.Entry.Asset, browser_download_url = AssetUrl(file.Entry),
                        digest = "sha256:" + file.Entry.Sha256 }).ToArray()
                };
                var api = $"https://api.github.com/repos/{repo}/releases";
                if (url == api + "?per_page=30&page=1") return Reply(request, new StringContent(JsonSerializer.Serialize(new[] { release })));
                if (url == api + "/tags/" + tag) return Reply(request, new StringContent(JsonSerializer.Serialize(release)));
            }
            throw new Exception("Unexpected request " + url);
        }

        private static Task<HttpResponseMessage> Reply(HttpRequestMessage request, HttpContent content) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
    }
}
