using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

namespace Sevo.Mod.Launcher;

public sealed partial class MainForm
{
    private readonly Button keelButton;
    private readonly Label keelVersionLabel = new() { AutoSize = true, AccessibleName = "Keel version" };
    private KeelUpdateOffer? latestKeel;
    private Version? bundledKeel;
    private ProfileKeelStatus? profileKeel;

    private Control BuildVersionBox()
    {
        try { bundledKeel = RuntimeProvisioner.GetBundledKeelVersion(Path.Combine(appRoot, "framework")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Keep version information available even if the bundled files cannot be read.
        }

        var box = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true,
            BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(6, 2, 6, 2),
            Margin = new Padding(3, 0, 3, 5), AccessibleName = "Installed versions" };
        box.Controls.Add(new Label { Text = $"Uplink {UplinkVersion}", AutoSize = true,
            AccessibleName = "Uplink version", Margin = new Padding(3, 2, 16, 2) });
        keelVersionLabel.Margin = new Padding(3, 2, 3, 2);
        box.Controls.Add(keelVersionLabel);
        ShowKeelOffer();
        return box;
    }

    private void RefreshKeelButton(string? game)
    {
        profileKeel = null;
        if (game != null)
        {
            try { profileKeel = RuntimeProvisioner.InspectKeel(game); }
            catch (Exception ex) when (ex is PlatformException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                profileKeel = new(ProfileKeelState.Unverified);
            }
        }
        ShowKeelOffer();
    }

    private void ShowKeelOffer()
    {
        var installed = profileKeel?.Version;
        var managed = profileKeel?.State == ProfileKeelState.Managed;
        bool updateAvailable = managed && installed != null && latestKeel != null && latestKeel.Version > installed;
        keelButton.Enabled = managed;
        var label = profileKeel?.State switch
        {
            ProfileKeelState.Managed => $"Keel {installed}" + (updateAvailable ? $" · {latestKeel!.Version} available" : ""),
            ProfileKeelState.External => $"Keel {installed} · manually managed",
            ProfileKeelState.Unverified => installed == null ? "Keel · installation unverified" : $"Keel {installed} · unverified",
            ProfileKeelState.UpdatePending => "Keel · resume with Prepare game",
            ProfileKeelState.NotInstalled => "Keel · prepare game first",
            _ => "Keel · select a profile"
        };
        keelVersionLabel.Text = managed || bundledKeel == null ? label : $"{label} (bundled {bundledKeel})";
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
