using Sevo.Mod.Hosting;

namespace Sevo.Mod.Launcher;

public sealed partial class MainForm
{
    private async Task FactoryResetAsync()
    {
        // Do not invoke ordinary recovery to reach the recovery command.
        var id = (profiles.SelectedItem as ProfileChoice)?.Id
            ?? throw new InvalidOperationException("Select the profile whose game installation you want to reset.");
        var profile = service.ListProfilesForReset().Single(item => item.Id == id);
        var message = $"Reset the mod installation for '{profile.Name}'?\n\n{profile.GameDirectory}\n\n" +
            "This permanently deletes ALL installed mods, including manual plugins, mod settings, BepInEx, loader dependencies and this profile's runtime data.\n\n" +
            "Uplink then installs a fresh verified BepInEx and its bundled Keel. Missing or modified mod files will be discarded.\n\n" +
            "The base game, saved games and game settings are kept. Other profiles and your downloaded package library are kept.\n\n" +
            "Close the game first. This cannot be undone.";
        var warning = GameBuildWarning(profile.GameDirectory);
        if (warning != null) message += "\n\n" + warning;
        if (MessageBox.Show(this, message, "Reset to factory settings", MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        Write("Downloading and verifying a fresh runtime before resetting " + profile.GameDirectory + "…");
        // A new cache avoids a corrupt cached archive preventing recovery.
        var downloads = Path.Combine(dataRoot, "runtime-downloads", "factory-reset", Guid.NewGuid().ToString("N"));
        var loader = await RuntimeDownloads.ResolveLoaderAsync(http, profile.GameDirectory, downloads,
            Path.Combine(appRoot, "runtime"), ignoreInstalled: true);
        var framework = Path.Combine(appRoot, "framework");
        try
        {
            Write("Resetting mods and reinstalling BepInEx and Keel…");
            var result = await Task.Run(() => new FactoryResetter(dataRoot).Reset(profile.Id, loader.ArchivePath, framework));
            Write($"Factory reset complete. Installed fresh BepInEx {loader.Version} and bundled Keel ({result.FilesInstalled} runtime files). " +
                "No mods are selected. Game files, saves and game settings were preserved. Choose Play modded to check runtime startup.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Sevo.Mod.Core.PlatformException)
        {
            throw new InvalidOperationException("Factory reset could not finish. Close the game, check folder permissions, then retry Game tools → Reset to factory settings. " + ex.Message, ex);
        }
    }
}
