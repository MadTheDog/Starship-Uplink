using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

internal static class LoaderDiscoveryTests
{
    public static void Register(List<(string Name, Action Run)> tests, string root)
    {
        tests.Add(("fresh preparation follows latest stable BepInEx 5.4 metadata and verifies its archive", () =>
        {
            const string version = "5.4.23.7"; // Deliberately newer than the development baseline.
            byte[] bytes = [1, 2, 3, 4];
            using var handler = new Releases(version, bytes);
            using var http = new HttpClient(handler);
            var cache = Path.Combine(root, "discovered-loader");
            var bundle = Path.Combine(root, "bundled-loader");
            var loader = RuntimeDownloads.ResolveLoaderAsync(http, root, cache, bundle).GetAwaiter().GetResult();
            Check(loader.Version == Version.Parse(version) && File.ReadAllBytes(loader.ArchivePath).SequenceEqual(bytes),
                "The current release was not used.");
            RuntimeDownloads.ResolveLoaderAsync(http, root, cache, bundle).GetAwaiter().GetResult();
            Check(handler.Downloads == 1 && handler.MetadataChecks == 2, "The cache skipped checking latest or redownloaded verified bytes.");

            File.WriteAllBytes(loader.ArchivePath, [7, 8, 9]);
            Refuses(() => RuntimeDownloads.ResolveLoaderAsync(http, root, cache, bundle).GetAwaiter().GetResult());
            Check(handler.Downloads == 1 && File.ReadAllBytes(loader.ArchivePath).SequenceEqual(new byte[] { 7, 8, 9 }),
                "Corrupt cache was trusted or overwritten.");
        }));

        tests.Add(("loader discovery refuses major version changes, missing hashes and tampered downloads", () =>
        {
            foreach (var (version, checksum, badDownload) in new[]
            {
                ("6.0.0.0", true, false), ("5.4.23.7", false, false), ("5.4.23.7", true, true)
            })
            {
                using var handler = new Releases(version, [1, 2, 3, 4], checksum, badDownload);
                using var http = new HttpClient(handler);
                var cache = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Refuses(() => RuntimeDownloads.ResolveLoaderAsync(http, root, cache, Path.Combine(root, "bundled-loader")).GetAwaiter().GetResult());
                Check(handler.Downloads == (badDownload ? 1 : 0) &&
                    (!Directory.Exists(cache) || !Directory.EnumerateFiles(cache).Any()), "An invalid candidate reached the loader cache.");
            }
        }));
    }

    private static void Refuses(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is PlatformException or InvalidOperationException) { return; }
        throw new Exception("Expected safe refusal.");
    }

    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }

    private sealed class Releases(string version, byte[] archive, bool checksum = true, bool badDownload = false) : HttpMessageHandler
    {
        public int Downloads { get; private set; }
        public int MetadataChecks { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var name = $"BepInEx_win_x64_{version}.zip";
            var url = $"https://github.com/BepInEx/BepInEx/releases/download/v{version}/{name}";
            HttpContent content;
            if (request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/BepInEx/BepInEx/releases/latest")
            {
                MetadataChecks++;
                content = new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = "v" + version, html_url = $"https://github.com/BepInEx/BepInEx/releases/tag/v{version}",
                    prerelease = false,
                    assets = new[] { new { name, browser_download_url = url,
                        digest = checksum ? "sha256:" + Convert.ToHexString(SHA256.HashData(archive)) : null } }
                }));
            }
            else if (request.RequestUri.AbsoluteUri == url)
            {
                Downloads++;
                content = new ByteArrayContent(badDownload ? [9, 9, 9] : archive);
            }
            else throw new Exception("Unexpected request: " + request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }
}
