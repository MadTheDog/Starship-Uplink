using System.Diagnostics;
using System.Security.Cryptography;
using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

public static class RuntimeDownloads
{
    public const string LoaderFileName = "BepInEx_win_x64_5.4.23.5.zip";
    public const string LoaderSha256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4";
    private const string Repository = "BepInEx/BepInEx";
    private static readonly Version LegacyVersion = new(5, 4, 23, 5);
    public sealed record VerifiedLoader(Version Version, string ArchivePath);

    public static async Task<VerifiedLoader> ResolveLoaderAsync(HttpClient http, string gameDirectory, string cacheDirectory,
        string bundledDirectory, CancellationToken token = default)
    {
        var core = SafePath.Child(gameDirectory, "BepInEx/core/BepInEx.dll");
        Version? installed = null;
        if (File.Exists(core))
        {
            var info = FileVersionInfo.GetVersionInfo(core);
            if (!Version.TryParse(info.FileVersion, out installed) || !Supported(installed))
                throw new PlatformException("The installed BepInEx is not an identifiable Windows BepInEx 5.4 release. Preserve that setup and use a clean game copy.");
        }

        // Keep the previously supported 5.4.23.5 profile usable with its verified offline cache.
        if (installed == LegacyVersion)
        {
            var bundled = SafePath.Child(bundledDirectory, LoaderFileName);
            var path = await EnsureLoaderAsync(http, cacheDirectory, bundled, token).ConfigureAwait(false);
            return new VerifiedLoader(LegacyVersion, path);
        }

        var client = new GitHubReleaseClient(http);
        var release = installed == null
            ? await client.GetLatestAsync(Repository, token).ConfigureAwait(false)
            : await client.GetByTagAsync(Repository, "v" + installed, token).ConfigureAwait(false);
        if (release.Prerelease || !release.TagName.StartsWith('v') ||
            !Version.TryParse(release.TagName[1..], out var version) || !Supported(version) ||
            release.TagName != "v" + version)
            throw new PlatformException("GitHub did not return a compatible stable BepInEx 5.4 release.");

        var name = $"BepInEx_win_x64_{version}.zip";
        var officialUrl = ReleaseUrl(version, name);
        var candidates = release.Assets.Where(asset => asset.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1 || candidates[0].Name != name || candidates[0].DownloadUrl != officialUrl ||
            candidates[0].Sha256 is not { } digest)
            throw new PlatformException($"BepInEx {version} has no unique official Windows x64 ZIP with a SHA-256 digest.");
        var archive = await EnsureArchiveAsync(http, cacheDirectory, SafePath.Child(bundledDirectory, name),
            name, officialUrl, digest, token).ConfigureAwait(false);
        return new VerifiedLoader(version, archive);
    }

    private static bool Supported(Version version) => version.Major == 5 && version.Minor == 4 && version.Revision >= 0;

    private static string ReleaseUrl(Version version, string fileName) =>
        $"https://github.com/{Repository}/releases/download/v{version}/{fileName}";

    public static Task<string> EnsureLoaderAsync(HttpClient client, string directory, string? bundledFile = null,
        CancellationToken cancellationToken = default) => EnsureArchiveAsync(client, directory, bundledFile,
            LoaderFileName, ReleaseUrl(LegacyVersion, LoaderFileName), LoaderSha256, cancellationToken);

    private static async Task<string> EnsureArchiveAsync(HttpClient client, string directory, string? bundledFile,
        string fileName, string url, string digest, CancellationToken cancellationToken)
    {
        SafePath.Check(Path.GetFullPath(directory)); Directory.CreateDirectory(directory);
        var target = SafePath.Child(directory, fileName);
        if (File.Exists(target)) { Verify(target, digest); return target; }
        var temp = SafePath.Child(directory, fileName + ".download-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (bundledFile != null && File.Exists(bundledFile))
            {
                SafePath.Check(Path.GetFullPath(bundledFile)); Verify(bundledFile, digest); File.Copy(bundledFile, temp, false);
            }
            else
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new InvalidOperationException("Loader download exceeds size limit.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920]; int count; long total = 0;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if ((total += count) > 16 * 1024 * 1024) throw new InvalidOperationException("Loader download exceeds size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            Verify(temp, digest);
            try { File.Move(temp, target); }
            catch (IOException) when (File.Exists(target)) { Verify(target, digest); }
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Verify(string path, string digest)
    {
        using var source = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(source)).Equals(digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("BepInEx archive checksum does not match the verified upstream release. The file was preserved and was not installed.");
    }
}
