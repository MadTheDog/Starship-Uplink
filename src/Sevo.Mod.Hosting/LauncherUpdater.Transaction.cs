using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sevo.Mod.Core;

[assembly: InternalsVisibleTo("Sevo.Mod.Integration.Tests")]

namespace Sevo.Mod.Hosting;

public static partial class LauncherUpdater
{
    const string UpdateDirectory = ".uplink-update";
    const string OwnerFile = "owner";
    const string OwnerValue = "Starship Uplink update storage v1";
    const string PlanFile = "pending.json";
    const string PhaseFile = "phase";
    const string BackupDirectory = "backup";
    const string ReplacementDirectory = "replacement";
    const string RecoveryScript = "Resume Uplink Update.cmd";
    const string Applying = "applying";
    const string RollingBack = "rolling-back";
    const string Committed = "committed";
    const string RolledBack = "rolled-back";

    sealed record UpdatePlan(string Transaction, string Workspace, int ParentId, long ParentStartUtcTicks,
        string OldInventory, string NewInventory, Dictionary<string, string>? OldKeel = null);
    internal sealed record UpdateResult(ProcessStartInfo Restart, PlatformException? Failure);
    enum LiveState { Before, Between, After }

    static string? OpenUpdateRoot(string appRoot, bool create)
    {
        RequireDirectory(appRoot);
        string updateRoot = Child(appRoot, UpdateDirectory);
        var attributes = Attributes(updateRoot);
        if (attributes is null)
        {
            if (!create) return null;
            Directory.CreateDirectory(updateRoot);
            WriteDurable(Child(updateRoot, OwnerFile), OwnerValue);
            return updateRoot;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            string marker = Child(updateRoot, OwnerFile);
            if (Attributes(marker) is { } markerAttributes && (markerAttributes & FileAttributes.Directory) == 0 &&
                RequireFile(marker, MaximumInventoryBytes) == OwnerValue.Length && ReadText(marker, OwnerValue.Length) == OwnerValue)
                return updateRoot;
        }
        if (!create) return null;
        throw new PlatformException("The .uplink-update path is occupied by files Uplink does not own.");
    }

    static (string PlanPath, string UpdateRoot, string AppRoot) LocatePlan(string planPath)
    {
        string fullPath = NormalizeAbsolute(planPath);
        string? updateRoot = Path.GetDirectoryName(fullPath);
        string? appRoot = updateRoot is null ? null : Path.GetDirectoryName(updateRoot);
        if (appRoot is null || Path.GetFileName(updateRoot) != UpdateDirectory || Path.GetFileName(fullPath) != PlanFile ||
            OpenUpdateRoot(appRoot, create: false) != updateRoot)
            throw new PlatformException("The Uplink update plan must be inside an owned .uplink-update folder.");
        return (Child(updateRoot!, PlanFile), updateRoot!, appRoot);
    }

    static UpdatePlan LoadPlan(string planPath)
    {
        UpdatePlan? plan;
        try { plan = JsonSerializer.Deserialize<UpdatePlan>(ReadBytes(planPath, MaximumInventoryBytes)); }
        catch (JsonException ex) { throw new PlatformException("The pending Uplink update plan is invalid.", ex); }
        if (plan is null || !Guid.TryParseExact(plan.Transaction, "N", out var transaction) || transaction.ToString("N") != plan.Transaction ||
            plan.Workspace is null || NormalizeAbsolute(plan.Workspace) != plan.Workspace || plan.ParentId <= 0 ||
            plan.ParentStartUtcTicks <= 0 || plan.ParentStartUtcTicks > DateTime.MaxValue.Ticks ||
            !IsDigest(plan.OldInventory) || !IsDigest(plan.NewInventory) ||
            plan.OldKeel is { } keel && (keel.Count != BundledKeelPaths.Length ||
                BundledKeelPaths.Any(path => !IsDigest(keel.GetValueOrDefault(path)))))
            throw new PlatformException("The pending Uplink update plan is incomplete or unsafe.");
        return plan;
    }

    static void SavePlan(string planPath, UpdatePlan plan, string transactionRoot)
        => WriteRecord(planPath, JsonSerializer.Serialize(plan), transactionRoot, overwrite: false);

    // Applying never modifies the immutable backup or replacement. A crash leaves each live
    // slot in one of two verifiable states, so replay can finish either the apply or rollback.
    internal static UpdateResult Apply(string planPath)
    {
        var (fullPath, updateRoot, appRoot) = LocatePlan(planPath);
        var plan = LoadPlan(fullPath);
        string transactionRoot = Child(updateRoot, plan.Transaction);
        string backup = Child(transactionRoot, BackupDirectory);
        string replacement = Child(transactionRoot, ReplacementDirectory);
        string phasePath = Child(transactionRoot, PhaseFile);
        string? phase = Attributes(phasePath) is null ? null : ReadText(phasePath, 32);
        if (phase is not (null or Applying or RollingBack or Committed or RolledBack))
            throw new PlatformException("The pending Uplink update phase is invalid. Recovery files were kept at " + transactionRoot);

        Dictionary<string, string> oldFiles, newFiles;
        try
        {
            oldFiles = ReadInventory(backup, exactTree: true, plan.OldInventory, oldKeel: plan.OldKeel);
            newFiles = ReadInventory(replacement, exactTree: true, plan.NewInventory);
            if (phase is null) ValidateLive(appRoot, oldFiles, newFiles, LiveState.Before);
        }
        catch (Exception ex) when (phase is null && IsFileFailure(ex))
        {
            File.Delete(fullPath);
            throw new PlatformException("Uplink left the installation untouched because the update no longer matches it. " + ex.Message, ex);
        }

        PlatformException? failure = null;
        if (phase is RollingBack)
        {
            Reconcile(appRoot, oldFiles, newFiles, backup, transactionRoot, forward: false);
            WriteRecord(phasePath, RolledBack, transactionRoot, overwrite: true);
            phase = RolledBack;
        }
        else if (phase is null or Applying)
        {
            WriteRecord(phasePath, Applying, transactionRoot, overwrite: true);
            try
            {
                Reconcile(appRoot, oldFiles, newFiles, replacement, transactionRoot, forward: true);
                WriteRecord(phasePath, Committed, transactionRoot, overwrite: true);
                phase = Committed;
            }
            catch (Exception applyError) when (IsFileFailure(applyError))
            {
                try
                {
                    WriteRecord(phasePath, RollingBack, transactionRoot, overwrite: true);
                    Reconcile(appRoot, oldFiles, newFiles, backup, transactionRoot, forward: false);
                    WriteRecord(phasePath, RolledBack, transactionRoot, overwrite: true);
                    phase = RolledBack;
                }
                catch (Exception rollbackError) when (IsFileFailure(rollbackError))
                {
                    throw new PlatformException("Uplink could not finish the update or restore the prior files. " +
                        "Close programs using Uplink and run 'Resume Uplink Update.cmd' in " + transactionRoot + ".",
                        new AggregateException(applyError, rollbackError));
                }
                failure = new PlatformException("Uplink restored and reopened the previous version after the update failed. " +
                    applyError.Message, applyError);
            }
        }

        ValidateLive(appRoot, oldFiles, newFiles, phase is Committed ? LiveState.After : LiveState.Before);
        File.Delete(fullPath);
        if (phase is RolledBack && failure is null)
            failure = new PlatformException("Uplink restored and reopened the previous version after an interrupted rollback.");
        var restart = new ProcessStartInfo(Child(appRoot, Executable)) { WorkingDirectory = appRoot, UseShellExecute = false };
        restart.ArgumentList.Add("--workspace");
        restart.ArgumentList.Add(plan.Workspace);
        return new UpdateResult(restart, failure);
    }

    static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or PlatformException;

    static IEnumerable<string> Slots(Dictionary<string, string> oldFiles, Dictionary<string, string> newFiles)
        => oldFiles.Keys.Union(newFiles.Keys, StringComparer.Ordinal).Where(path => path != InventoryFile)
            .Order(StringComparer.Ordinal).Append(InventoryFile);

    static string? LiveHash(string path)
    {
        if (Attributes(path) is null) return null;
        RequireFile(path, MaximumPackageBytes);
        return HashFile(path);
    }

    static void ValidateLive(string appRoot, Dictionary<string, string> oldFiles, Dictionary<string, string> newFiles, LiveState state)
    {
        foreach (string relative in Slots(oldFiles, newFiles))
        {
            string? actual = LiveHash(Child(appRoot, relative));
            bool valid = state != LiveState.After && actual == oldFiles.GetValueOrDefault(relative) ||
                state != LiveState.Before && actual == newFiles.GetValueOrDefault(relative);
            if (!valid) throw new PlatformException("A managed Uplink file changed or an unowned file occupies its destination: " + relative);
        }
    }

    static void Reconcile(string appRoot, Dictionary<string, string> oldFiles, Dictionary<string, string> newFiles,
        string sourceRoot, string transactionRoot, bool forward)
    {
        ValidateLive(appRoot, oldFiles, newFiles, LiveState.Between);
        var wanted = forward ? newFiles : oldFiles;
        var previous = forward ? oldFiles : newFiles;
        foreach (string relative in Slots(oldFiles, newFiles))
        {
            string live = Child(appRoot, relative);
            string? actual = LiveHash(live);
            string? desired = wanted.GetValueOrDefault(relative);
            if (actual == desired) continue;
            if (actual != previous.GetValueOrDefault(relative))
                throw new PlatformException("An Uplink file changed during the update: " + relative);
            if (desired is null)
            {
                File.Delete(live);
                continue;
            }
            string temp = Child(transactionRoot, "file-" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDurable(Child(sourceRoot, relative), temp);
                if (HashFile(temp) != desired) throw new PlatformException("A staged Uplink file changed: " + relative);
                if (LiveHash(Child(appRoot, relative)) != actual)
                    throw new PlatformException("An Uplink file changed during the update: " + relative);
                File.Move(temp, live, overwrite: actual is not null);
            }
            finally { File.Delete(temp); }
        }
        ValidateLive(appRoot, oldFiles, newFiles, forward ? LiveState.After : LiveState.Before);
    }

    static void CopyDurable(string source, string destination)
    {
        long length = RequireFile(source, MaximumPackageBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        CopyBounded(input, output, length);
        output.Flush(flushToDisk: true);
    }

    static void WriteDurable(string path, string text)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(StrictUtf8.GetBytes(text));
        output.Flush(flushToDisk: true);
    }

    static void WriteRecord(string path, string text, string transactionRoot, bool overwrite)
    {
        string temp = Child(transactionRoot, "record-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteDurable(temp, text);
            Attributes(path);
            File.Move(temp, path, overwrite);
        }
        finally { File.Delete(temp); }
    }
}
