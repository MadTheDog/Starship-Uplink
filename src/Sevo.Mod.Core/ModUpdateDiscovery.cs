namespace Sevo.Mod.Core;

public enum ModUpdateState { NoSource, NoZip, Current, Available, ReviewRelease, CheckFailed }

public sealed record ModUpdateStatus(ModUpdateState State, string? ReleaseTag = null, string? Detail = null);

public static class ModUpdateDiscovery
{
    public static async Task<IReadOnlyDictionary<string, ModUpdateStatus>> CheckAsync(
        GitHubReleaseClient client, IEnumerable<PackageRecord> packages, CancellationToken token = default)
    {
        var results = new Dictionary<string, ModUpdateStatus>(StringComparer.Ordinal);
        var releases = new Dictionary<string, (ReleaseInfo? Release, string? Error)>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            if (package.Repository == null)
            {
                results.Add(package.Id, new(ModUpdateState.NoSource));
                continue;
            }
            if (!releases.TryGetValue(package.Repository, out var lookup))
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                try { lookup = (await client.GetLatestAsync(package.Repository, requestTimeout.Token).ConfigureAwait(false), null); }
                catch (Exception ex) when (ex is PlatformException or HttpRequestException or IOException ||
                    ex is OperationCanceledException && !token.IsCancellationRequested)
                {
                    lookup = (null, ex is OperationCanceledException ? "GitHub release check timed out." : ex.Message);
                }
                releases.Add(package.Repository, lookup);
            }
            results.Add(package.Id, lookup.Release == null ? new(ModUpdateState.CheckFailed, Detail: lookup.Error) :
                Classify(package, lookup.Release));
        }
        return results;
    }

    private static ModUpdateStatus Classify(PackageRecord package, ReleaseInfo release)
    {
        var zips = release.Assets.Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (zips.Length == 0) return new(ModUpdateState.NoZip, release.TagName);

        var known = zips.Where(asset => asset.Name == package.AssetName).ToArray();
        var candidate = known.Length == 1 ? known[0] : zips.Length == 1 ? zips[0] : null;
        if (candidate?.Sha256 is { } digest && package.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase))
            return new(ModUpdateState.Current, release.TagName);
        if (candidate == null) return new(ModUpdateState.ReviewRelease, release.TagName);

        int comparison;
        try { comparison = PackageVersion.Parse(release.TagName).CompareTo(PackageVersion.Parse(package.Version)); }
        catch (PlatformException) { return new(ModUpdateState.ReviewRelease, release.TagName); }
        if (comparison > 0) return new(ModUpdateState.Available, release.TagName);
        if (comparison == 0 && candidate.Sha256 != null)
            return new(ModUpdateState.ReviewRelease, release.TagName);
        return new(ModUpdateState.Current, release.TagName);
    }
}
