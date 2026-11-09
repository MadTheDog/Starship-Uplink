using System.Diagnostics;
using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

namespace Sevo.Mod.Launcher;

public sealed partial class MainForm
{
    private static Version UplinkVersion => Version.Parse(typeof(MainForm).Assembly.GetName().Version!.ToString(3));

    private string? ChooseGameFolder(string description, string? initialDirectory = null)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        var initial = initialDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "Starship EVO");
        if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private async Task NoticeLauncherUpdateAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var update = await LauncherUpdater.CheckAsync(http, UplinkVersion, timeout.Token);
            if (CanUseControls && update != null) status.Text = $"Uplink {update.Version} is available";
        }
        catch (Exception ex) when (ex is PlatformException or InvalidOperationException or IOException or HttpRequestException
            or OperationCanceledException or System.Text.Json.JsonException)
        {
            // A failed background check should never block managing mods. The button retries it.
        }
    }

    private async Task UpdateLauncherAsync()
    {
        Write("Checking for Uplink updates…");
        var update = await LauncherUpdater.CheckAsync(http, UplinkVersion);
        if (!CanUseControls) return;
        if (update == null)
        {
            status.Text = "Changes apply after restart";
            Write($"Uplink {UplinkVersion} is up to date.");
            return;
        }

        status.Text = $"Uplink {update.Version} is available";
        if (MessageBox.Show(this, $"Install Uplink {update.Version} and restart with your current workspace?",
            "Update Uplink", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        var installation = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (!installation.Equals(Path.TrimEndingDirectorySeparator(appRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Open the packaged Uplink executable directly to update it. This launcher is using an alternate payload folder.");
        Write($"Downloading and verifying Uplink {update.Version}…");
        var start = await LauncherUpdater.StageAsync(http, update, installation, dataRoot);
        if (!CanUseControls) return;
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("Uplink could not start the updater.");
        Close();
    }

    private async Task CleanGameAsync()
    {
        var selected = profiles.SelectedItem is ProfileChoice ? Profile().GameDirectory : null;
        var game = ChooseGameFolder("Choose the Starship EVO game folder to clean.", selected);
        if (game == null) return;

        var cleaner = new GameCleaner(dataRoot);
        var plan = await Task.Run(() => cleaner.Inspect(game));
        if (!CanUseControls) return;
        string action = plan.IsResuming ? "Finish the interrupted cleanup for this game?"
            : plan.IsOrphaned ? "Archive the old Uplink deployment and release this game folder?"
            : "Archive this profile's Uplink deployment and release the game folder?";
        var message = $"{action}\n\n{plan.GameDirectory}\n\n" +
            "Uplink keeps your saved games, configuration, package library, and manually installed mods and runtime files.\n\n" +
            $"Deployment backup\n{plan.BackupDirectory}";
        if (plan.IsOrphaned) message += $"\n\nPrevious workspace\n{plan.OriginalWorkspaceRoot}";
        if (MessageBox.Show(this, message, "Confirm game cleanup", MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        var result = await Task.Run(() => cleaner.Clean(plan));
        if (!CanUseControls) return;
        RefreshViews();
        Write($"Game folder released. Deployment backup at {result.BackupDirectory}");
    }
}
