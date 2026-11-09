using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

/// <summary>Retains the original asset filename without sharing destinations between release downloads.</summary>
public static class ReleaseDownloads
{
    public static Task<string> DownloadAsync(GitHubReleaseClient client, ReleaseAsset asset, string downloadsDirectory, CancellationToken cancellationToken = default)
    {
        var directory = SafePath.Child(downloadsDirectory, Guid.NewGuid().ToString("N"));
        return client.DownloadAsync(asset, directory, cancellationToken);
    }

    public static async Task<PackageRecord> ImportPackageAsync(GitHubReleaseClient client, ReleaseAsset asset,
        string downloadsDirectory, PlatformService service, string repository, string? expectedId = null, string? expectedVersion = null)
    {
        var path = await DownloadAsync(client, asset, downloadsDirectory).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => service.ImportPackage(path, new PackageImportOptions
            {
                Repository = repository,
                AssetName = asset.Name,
                ExpectedId = expectedId,
                ExpectedVersion = expectedVersion
            })).ConfigureAwait(false);
        }
        finally
        {
            SafePath.Check(path);
            File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
