using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

namespace Sevo.Mod.Launcher;

public sealed partial class MainForm
{
    private readonly Button keelButton;
    private KeelUpdateOffer? latestKeel;
    private Version? managedKeelVersion;

    private void RefreshKeelButton(string? game)
    {
        managedKeelVersion = null;
        if (game != null)
        {
            try { managedKeelVersion = RuntimeProvisioner.GetManagedKeelVersion(game); }
            catch (Exception ex) when (ex is PlatformException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A broken or unavailable runtime should not block the rest of the profile UI.
            }
        }
        ShowKeelOffer();
    }

    private void ShowKeelOffer()
    {
        keelButton.Visible = managedKeelVersion != null;
        keelButton.Text = latestKeel != null && managedKeelVersion != null && latestKeel.Version > managedKeelVersion
            ? $"Keel {latestKeel.Version} update available"
            : "Check Keel update";
    }

    private async Task NoticeKeelUpdateAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var offer = await KeelUpdater.CheckAsync(http, new Version(0, 0, 0), timeout.Token);
            if (!CanUseControls) return;
            // A manual check can finish before the background request.
            if (latestKeel == null) latestKeel = offer;
            ShowKeelOffer();
        }
        catch (Exception ex) when (ex is PlatformException or InvalidOperationException or IOException or
            HttpRequestException or OperationCanceledException)
        {
            // The explicit check can retry without interrupting other launcher actions.
        }
    }

    private async Task UpdateKeelAsync()
    {
        var profile = Profile();
        var installedVersion = RuntimeProvisioner.GetManagedKeelVersion(profile.GameDirectory)
            ?? throw new InvalidOperationException("Prepare this profile with Uplink-managed Keel before updating it.");
        Write("Checking for Keel updates…");
        var offer = await KeelUpdater.CheckAsync(http, new Version(0, 0, 0));
        if (!CanUseControls) return;
        latestKeel = offer;
        ShowKeelOffer();
        if (offer == null || offer.Version <= installedVersion)
        {
            Write($"Keel {installedVersion} is up to date for {profile.Name}.");
            return;
        }

        if (MessageBox.Show(this, $"Update Starship Keel from {installedVersion} to {offer.Version}{(offer.IsPreview ? " (preview)" : "")} for {profile.Name}?\n\n" +
            $"{profile.GameDirectory}\n\nClose Starship EVO before updating. The new version will be used on the next launch.",
            "Update Starship Keel", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        RuntimeProvisioner.AssertNotRunning(profile.GameDirectory);
        Write($"Downloading and verifying Keel {offer.Version}…");
        await KeelUpdater.ApplyAsync(http, offer, new RuntimeProvisioner(dataRoot), profile.GameDirectory,
            State(profile), Path.Combine(dataRoot, "runtime-downloads"));
        Write($"Updated Keel to {offer.Version} for {profile.Name}. The update takes effect on the next game launch.");
    }
}
