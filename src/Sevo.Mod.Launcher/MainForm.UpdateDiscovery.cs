using Sevo.Mod.Core;

namespace Sevo.Mod.Launcher;

public sealed partial class MainForm
{
    private string? checkedProfile;
    private readonly Dictionary<(string Id, string Version), ModUpdateStatus> updateStatuses = [];

    private async Task CheckAllUpdatesAsync()
    {
        var profile = Profile();
        if (profile.Mods.Count == 0)
        {
            Write("There are no installed mods to check in this profile.");
            return;
        }

        var library = service.ListPackages().ToDictionary(package => (package.Id, package.Version));
        var packages = profile.Mods.Select(mod => library[(mod.PackageId, mod.Version)]).ToArray();
        Write($"Checking GitHub releases for {packages.Length} installed mod(s)…");
        using var cancel = new CancellationTokenSource();
        FormClosingEventHandler onClose = (_, _) => cancel.Cancel();
        FormClosing += onClose;
        IReadOnlyDictionary<string, ModUpdateStatus> result;
        try { result = await ModUpdateDiscovery.CheckAsync(new GitHubReleaseClient(http), packages, cancel.Token); }
        finally { FormClosing -= onClose; }
        if (!CanUseControls) return;

        checkedProfile = profile.Id;
        updateStatuses.Clear();
        foreach (var package in packages)
        {
            var status = result[package.Id];
            updateStatuses.Add((package.Id, package.Version), status);
            if (status.Detail != null) Write($"{package.Name}: {status.Detail}");
        }
        var available = result.Values.Count(item => item.State == ModUpdateState.Available);
        var review = result.Values.Count(item => item.State == ModUpdateState.ReviewRelease);
        Write($"Update check complete. {available} available, {review} to review. Tick mods and use Review updates to choose what to install.");
    }

    private string UpdateStatus(string profileId, ProfileModRecord mod)
    {
        if (checkedProfile != profileId || !updateStatuses.TryGetValue((mod.PackageId, mod.Version), out var status))
            return "—";
        return status.State switch
        {
            ModUpdateState.Available => $"! {status.ReleaseTag} available" + (mod.Pinned ? " · pinned" : ""),
            ModUpdateState.ReviewRelease => "Review release" + (mod.Pinned ? " · pinned" : ""),
            ModUpdateState.Current => "Up to date",
            ModUpdateState.NoSource => "No GitHub source",
            ModUpdateState.NoZip => "No release ZIP",
            _ => "Check failed"
        };
    }
}
