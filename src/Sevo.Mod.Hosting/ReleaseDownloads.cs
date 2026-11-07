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
}
