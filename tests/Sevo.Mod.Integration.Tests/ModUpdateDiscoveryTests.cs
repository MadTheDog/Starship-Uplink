using System.Net;
using System.Text.Json;
using Sevo.Mod.Core;

internal static class ModUpdateDiscoveryTests
{
    public static void Verify()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var packages = new[]
        {
            Package("older", "Owner/Shared", "1.0.0"),
            Package("current", "owner/shared", "2.0.0", new string('a', 64)),
            Package("ambiguous", "Owner/Ambiguous", "1.0.0"),
            Package("failed", "Owner/Missing", "1.0.0"),
            Package("local", null, "1.0.0")
        };
        var results = ModUpdateDiscovery.CheckAsync(new GitHubReleaseClient(http), packages).GetAwaiter().GetResult();
        if (results["older"].State != ModUpdateState.Available || results["current"].State != ModUpdateState.Current ||
            results["ambiguous"].State != ModUpdateState.ReviewRelease || results["failed"].State != ModUpdateState.CheckFailed ||
            results["local"].State != ModUpdateState.NoSource || fixture.Requests != 3)
            throw new Exception("Unexpected discovery results or duplicate metadata requests.");
    }

    private static PackageRecord Package(string id, string? repository, string version, string? digest = null) =>
        new() { Id = id, Repository = repository, Version = version, AssetName = "mod.zip", Sha256 = digest ?? new string('b', 64) };

    private sealed class Fixture : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var uri = request.RequestUri!;
            if (uri.Host != "api.github.com" || !uri.AbsolutePath.EndsWith("/releases/latest"))
                throw new Exception("An update check attempted to download files.");
            var repo = string.Join("/", uri.Segments.Skip(2).Take(2).Select(segment => segment.TrimEnd('/')));
            var response = new HttpResponseMessage(repo == "Owner/Missing" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { RequestMessage = request };
            var names = repo == "Owner/Ambiguous" ? new[] { "windows.zip", "other.zip" } : ["mod.zip"];
            response.Content = new StringContent(JsonSerializer.Serialize(new
            {
                tag_name = "v2.0.0", html_url = $"https://github.com/{repo}/releases/tag/v2.0.0", prerelease = false,
                assets = names.Select(name => new { name, browser_download_url = $"https://github.com/{repo}/releases/download/v2.0.0/{name}",
                    digest = "sha256:" + new string('a', 64) }).ToArray()
            }));
            return Task.FromResult(response);
        }
    }
}
