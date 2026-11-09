using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

public sealed class LauncherUpdateOffer
{
    internal LauncherUpdateOffer(Version version, Uri releaseUrl, ReleaseAsset archive, ReleaseAsset? checksum)
        => (Version, ReleaseUrl, Archive, Checksum) = (version, releaseUrl, archive, checksum);

    public Version Version { get; }
    public Uri ReleaseUrl { get; }
    internal ReleaseAsset Archive { get; }
    internal ReleaseAsset? Checksum { get; }
}

public static partial class LauncherUpdater
{
    const string Repository = "MadTheDog/Starship-Uplink";
    const string ReleasesUrl = "https://github.com/" + Repository + "/releases/";
    const string Executable = "Starship Uplink.exe";
    const string ApplyOption = "--apply-launcher-update";

    public static async Task<LauncherUpdateOffer?> CheckAsync(HttpClient http, Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        var client = new GitHubReleaseClient(http);
        var release = await client.GetLatestAsync(Repository, cancellationToken).ConfigureAwait(false);
        string tag = release.TagName;
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Build < 0 ||
            version.Revision >= 0 || tag != "v" + version.ToString(3))
            throw new PlatformException("The latest Uplink release does not have a stable vX.Y.Z tag.");
        if (version <= new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build)))
            return null;

        string releaseUrl = ReleasesUrl + "tag/" + tag;
        if (!release.PageUrl.Equals(releaseUrl, StringComparison.OrdinalIgnoreCase))
            throw new PlatformException("The Uplink release URL does not match its version tag.");
        string baseName = $"Starship-Uplink-{version}-win-x64";
        var archive = SelectAsset(release, tag, baseName + ".zip") ??
            throw new PlatformException("The Uplink release is missing its official Windows x64 ZIP.");
        var checksum = archive.Sha256 is null ? SelectAsset(release, tag, baseName + ".sha256.txt") : null;
        if (archive.Sha256 is null && checksum is null)
            throw new PlatformException("The Uplink release has no official SHA-256 for its ZIP.");
        return new LauncherUpdateOffer(version, new Uri(releaseUrl), archive, checksum);
    }

    static ReleaseAsset? SelectAsset(ReleaseInfo release, string tag, string fileName)
    {
        var assets = release.Assets.Where(asset => asset.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (assets.Length == 0) return null;
        if (assets.Length != 1 || assets[0].Name != fileName ||
            !assets[0].DownloadUrl.Equals(ReleasesUrl + "download/" + tag + "/" + fileName, StringComparison.OrdinalIgnoreCase))
            throw new PlatformException("The official Uplink release asset is ambiguous or its URL is unexpected: " + fileName);
        return assets[0];
    }

    public static async Task<ProcessStartInfo> StageAsync(HttpClient http, LauncherUpdateOffer offer, string appRoot,
        string workspace, CancellationToken cancellationToken = default)
    {
        appRoot = NormalizeAbsolute(appRoot);
        workspace = NormalizeAbsolute(workspace);
        Dictionary<string, string> current;
        Dictionary<string, string>? oldKeel = null;
        try { current = ReadInventory(appRoot); }
        catch (PlatformException)
        {
            // Only the two bundled Keel files may differ. Verify that pair against its published release.
            current = ReadInventory(appRoot, inspectKeel: true);
            oldKeel = BundledKeelPaths.ToDictionary(path => path, path => current[path], StringComparer.Ordinal);
        }
        string updateRoot = OpenUpdateRoot(appRoot, create: true)!;
        string planPath = Child(updateRoot, PlanFile);
        if (Attributes(planPath) is not null)
            throw new PlatformException("An Uplink update is already pending. Close Uplink and resume that update first.");
        string transaction = Guid.NewGuid().ToString("N");
        string transactionRoot = Child(updateRoot, transaction);
        if (Attributes(transactionRoot) is not null) throw new PlatformException("The Uplink update staging path already exists.");
        Directory.CreateDirectory(transactionRoot);

        Version? overriddenKeel = oldKeel == null ? null :
            await VerifyBundledKeelAsync(http, appRoot, transactionRoot, oldKeel, cancellationToken).ConfigureAwait(false);
        string replacement = Child(transactionRoot, ReplacementDirectory);
        var client = new GitHubReleaseClient(http);
        var archive = offer.Archive;
        if (offer.Checksum is not null)
        {
            string checksumPath = await ReleaseDownloads.DownloadAsync(client, offer.Checksum, transactionRoot, cancellationToken).ConfigureAwait(false);
            string sha;
            try { sha = ReadReleaseChecksum(checksumPath, archive.Name); }
            finally { DeleteDownload(checksumPath); }
            archive = new ReleaseAsset { Name = archive.Name, DownloadUrl = archive.DownloadUrl, Sha256 = sha };
        }

        string archivePath = await ReleaseDownloads.DownloadAsync(client, archive, transactionRoot, cancellationToken).ConfigureAwait(false);
        try { ExtractArchive(archivePath, archive.Name[..^4], replacement, archive.Sha256!, cancellationToken); }
        finally { DeleteDownload(archivePath); }
        var next = ReadInventory(replacement, exactTree: true);
        if (overriddenKeel != null && ReadBundledKeelVersion(replacement) < overriddenKeel)
            throw new PlatformException($"This Uplink release bundles an older Keel than {overriddenKeel}. The installation was preserved.");
        ValidateLive(appRoot, current, next, LiveState.Before);

        string backup = Child(transactionRoot, BackupDirectory);
        foreach (string relative in current.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDurable(Child(appRoot, relative), Child(backup, relative));
        }
        ReadInventory(backup, exactTree: true, current[InventoryFile], oldKeel: oldKeel);
        ValidateLive(appRoot, current, next, LiveState.Before);

        using var parent = Process.GetCurrentProcess();
        var plan = new UpdatePlan(transaction, workspace, parent.Id, parent.StartTime.ToUniversalTime().Ticks,
            current[InventoryFile], next[InventoryFile], oldKeel);
        WriteDurable(Child(transactionRoot, RecoveryScript),
            "@echo off\r\n\"%~dp0backup\\Starship Uplink.exe\" --apply-launcher-update \"%~dp0..\\pending.json\"\r\n");
        cancellationToken.ThrowIfCancellationRequested();
        SavePlan(planPath, plan, transactionRoot);
        return HelperStartInfo(backup, planPath);
    }

    static void DeleteDownload(string path)
    {
        RequireFile(path, MaximumDownloadBytes);
        File.Delete(path);
        Directory.Delete(Path.GetDirectoryName(path)!);
    }

    public static IDisposable AcquireLauncherLock(string appRoot) => AcquireLauncherLock(appRoot, TimeSpan.Zero);

    static IDisposable AcquireLauncherLock(string appRoot, TimeSpan wait)
    {
        string path = NormalizeAbsolute(appRoot).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        string name = (OperatingSystem.IsWindows() ? @"Global\" : "") + "Starship-Uplink-" + hash;
        var mutex = new Mutex(false, name);
        try
        {
            try
            {
                if (!mutex.WaitOne(wait)) throw new PlatformException("Uplink is already running from this installation.");
            }
            catch (AbandonedMutexException) { /* The previous process exited before releasing the lock. */ }
            return new LauncherLock(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    sealed class LauncherLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    public static ProcessStartInfo? GetRecoveryStartInfo(string appRoot)
    {
        appRoot = NormalizeAbsolute(appRoot);
        string? updateRoot = OpenUpdateRoot(appRoot, create: false);
        if (updateRoot is null) return null;
        string planPath = Child(updateRoot, PlanFile);
        if (Attributes(planPath) is null) return null;
        var plan = LoadPlan(planPath);
        string backup = Child(updateRoot, plan.Transaction + "/" + BackupDirectory);
        ReadInventory(backup, exactTree: true, plan.OldInventory, oldKeel: plan.OldKeel);
        return HelperStartInfo(backup, planPath);
    }

    static ProcessStartInfo HelperStartInfo(string backup, string planPath)
    {
        var info = new ProcessStartInfo(Child(backup, Executable)) { WorkingDirectory = backup, UseShellExecute = false };
        info.ArgumentList.Add(ApplyOption);
        info.ArgumentList.Add(planPath);
        return info;
    }

    public static void ApplyAndRestart(string planPath)
    {
        var location = LocatePlan(planPath);
        var plan = LoadPlan(location.PlanPath);
        string backup = Child(location.UpdateRoot, plan.Transaction + "/" + BackupDirectory);
        string executable = Child(backup, Executable);
        if (!string.Equals(Environment.ProcessPath, executable, StringComparison.OrdinalIgnoreCase))
            throw new PlatformException("Run the staged Uplink recovery helper to apply this update.");

        WaitForParent(plan, Child(location.AppRoot, Executable));
        UpdateResult result;
        using (AcquireLauncherLock(location.AppRoot, TimeSpan.FromSeconds(10)))
        {
            // The recovery plan can change while this helper waits for another launcher to exit.
            if (LoadPlan(location.PlanPath) != plan) throw new PlatformException("The Uplink update plan changed while waiting.");
            result = Apply(location.PlanPath);
        }
        try
        {
            using var started = Process.Start(result.Restart) ?? throw new InvalidOperationException("The new process did not start.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            string status = result.Failure is null ? "Uplink was updated" : "Uplink restored the previous version";
            throw new PlatformException($"{status}, but could not restart. Reopen Starship Uplink.exe with --workspace \"{plan.Workspace}\". {ex.Message}", ex);
        }
        if (result.Failure is not null) throw result.Failure;
    }

    static void WaitForParent(UpdatePlan plan, string appExecutable)
    {
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentId); }
        catch (ArgumentException) { return; } // The launcher already exited.
        using (parent)
        {
            try
            {
                if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartUtcTicks) return;
                if (!string.Equals(parent.MainModule?.FileName, appExecutable, StringComparison.OrdinalIgnoreCase))
                    throw new PlatformException("The process recorded by the Uplink updater is not this installation's launcher.");
                if (!parent.WaitForExit(60_000)) throw new PlatformException("Close the previous Uplink launcher to finish the update.");
            }
            catch (InvalidOperationException) when (parent.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (parent.HasExited) { }
        }
    }
}
