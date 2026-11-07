using System.Diagnostics;
using System.Net.Http;
using Sevo.Mod.Core;
using Sevo.Mod.Hosting;

namespace Sevo.Mod.Launcher;

public sealed partial class MainForm : Form
{
    private readonly string appRoot, dataRoot;
    private readonly PlatformService service;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, AccessibleName = "Active mod profile" };
    private readonly DataGridView library = Grid("Package library"), installed = Grid("Installed mods");
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "Activity and diagnostics" };
    private readonly Label target = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Create a profile to choose a game installation." };
    private readonly Label status = new() { AutoSize = true, Text = "Changes apply after restart", Margin = new Padding(10, 10, 0, 0) };
    private readonly FlowLayoutPanel toolbar = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
    private bool busy, refreshing, refreshPending, closed;
    private bool CanUseControls => !closed && !IsDisposed && !Disposing;
    private sealed record ProfileChoice(string Id, string Name) { public override string ToString() => Name; }

    public MainForm(string root, string? workspace = null)
    {
        appRoot = Path.GetFullPath(root); dataRoot = workspace == null ? Path.Combine(appRoot, "data") : Path.GetFullPath(workspace); service = new PlatformService(dataRoot);
        Text = "Starship Uplink"; MinimumSize = new Size(800, 540);
        var available = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
        Size = new Size(Math.Min(1180, available.Width - 30), Math.Min(840, available.Height - 45));
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(245, 247, 250);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StarshipUplink/" + UplinkVersion);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoSize = true };
        header.Controls.Add(new Label { Text = "Starship Uplink", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), AutoSize = true }); header.Controls.Add(status);
        header.Controls.Add(Button("Update Uplink", UpdateLauncherAsync));
        var profileBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoSize = true };
        profileBar.Controls.Add(new Label { Text = "Profile", AutoSize = true, Margin = new Padding(0, 8, 12, 0) }); profileBar.Controls.Add(profiles);
        profileBar.Controls.Add(Button("New profile", NewProfileAsync)); profileBar.Controls.Add(Button("Prepare game", PrepareAsync)); profileBar.Controls.Add(Button("Play", PlayAsync)); profileBar.Controls.Add(Button("Validate", ValidateAsync)); profileBar.Controls.Add(Button("Clean game", CleanGameAsync));
        toolbar.Controls.Add(Button("Import ZIP", ImportAsync)); toolbar.Controls.Add(Button("Add from GitHub", AddGitHubAsync));
        toolbar.Controls.Add(Button("Remove from library", RemoveFromLibraryAsync));
        toolbar.Controls.Add(Button("Install selected", InstallAsync)); toolbar.Controls.Add(Button("Enable / disable", ToggleAsync));
        toolbar.Controls.Add(Button("Check mod update", UpdateAsync)); toolbar.Controls.Add(Button("Pin / unpin", PinAsync));
        toolbar.Controls.Add(Button("Roll back", RollbackAsync)); toolbar.Controls.Add(Button("Remove mod", RemoveAsync)); toolbar.Controls.Add(Button("Remove runtime", RemoveRuntimeAsync)); toolbar.Controls.Add(Button("Open game folder", OpenGameAsync)); toolbar.Controls.Add(Button("Refresh", () => { RefreshViews(); return Task.CompletedTask; }));
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.Controls.Add(Panel("Package library", library), 0, 0); split.Controls.Add(Panel("Installed mods", installed), 1, 0);
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(profileBar, 0, 1); layout.Controls.Add(target, 0, 2); layout.Controls.Add(toolbar, 0, 3); layout.Controls.Add(split, 0, 4); layout.Controls.Add(Panel("Activity", log), 0, 5); Controls.Add(layout);
        profiles.SelectedIndexChanged += async (_, _) =>
        {
            if (refreshing || !CanUseControls) return;
            if (busy) { refreshPending = true; return; }
            await RunAsync(() => Task.CompletedTask);
        };
        Shown += async (_, _) =>
        {
            await RunAsync(() =>
            {
                Write("Import a mod ZIP or add its GitHub repository. Profiles select plugins; they do not isolate game saves.");
                return Task.CompletedTask;
            }, true);
            if (CanUseControls) await NoticeLauncherUpdateAsync();
        };
        FormClosed += (_, _) => { closed = true; http.Dispose(); };
    }
    private static DataGridView Grid(string name) => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, AccessibleName = name, BackgroundColor = Color.White, BorderStyle = BorderStyle.None };
    private static Control Panel(string title, Control content)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 32, 8, 8) }; panel.Controls.Add(content);
        panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 10, FontStyle.Bold) }); return panel;
    }
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32, Margin = new Padding(0, 0, 8, 8) };
        button.Click += async (_, _) => await RunAsync(action); return button;
    }
    private async Task RunAsync(Func<Task> action, bool refreshProfiles = false)
    {
        if (busy || !CanUseControls) return; busy = true; profiles.Enabled = false; UseWaitCursor = true;
        try { await action(); if (CanUseControls) { RefreshViews(refreshProfiles); refreshPending = false; } }
        catch (Exception ex)
        {
            if (CanUseControls) { Write(ex.Message); MessageBox.Show(this, ex.Message, "Action could not complete", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        finally
        {
            busy = false;
            if (CanUseControls)
            {
                profiles.Enabled = true; UseWaitCursor = false;
                if (refreshPending) { refreshPending = false; await RunAsync(() => Task.CompletedTask); }
            }
        }
    }
    private void Write(string message)
    {
        if (!CanUseControls) return;
        var separator = log.TextLength == 0 ? "" : Environment.NewLine;
        log.AppendText($"{separator}[{DateTime.Now:HH:mm:ss}] {message.TrimEnd('\r', '\n')}");
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.ScrollToCaret();
    }
    private ProfileRecord Profile() => profiles.SelectedItem is ProfileChoice choice ? service.GetProfile(choice.Id) : throw new InvalidOperationException("Create or select a profile first.");
    private string InstalledId() => installed.CurrentRow?.Cells["PackageId"].Value as string ?? throw new InvalidOperationException("Select an installed mod first.");
    private void RefreshViews(bool refreshProfiles = true)
    {
        if (!CanUseControls) return;
        refreshing = true;
        try
        {
            if (refreshProfiles)
            {
                var id = (profiles.SelectedItem as ProfileChoice)?.Id; profiles.Items.Clear();
                foreach (var item in service.ListProfiles()) profiles.Items.Add(new ProfileChoice(item.Id, item.Name));
                profiles.SelectedItem = profiles.Items.Cast<ProfileChoice>().FirstOrDefault(x => x.Id == id);
                if (profiles.SelectedIndex < 0 && profiles.Items.Count > 0) profiles.SelectedIndex = 0;
            }
            var packages = service.ListPackages();
            library.DataSource = packages.Select(x => new { x.Id, x.Name, x.Version, Repository = x.Repository ?? "Local ZIP" }).ToList();
            if (profiles.SelectedItem is ProfileChoice choice)
            {
                var profile = service.GetProfile(choice.Id); target.Text = "Game: " + profile.GameDirectory;
                var names = packages.ToDictionary(x => (x.Id, x.Version), x => x.Name);
                installed.DataSource = profile.Mods.Select(x => new
                {
                    Name = names.GetValueOrDefault((x.PackageId, x.Version), x.PackageId),
                    x.PackageId,
                    x.Version,
                    State = x.Enabled ? "Enabled" : "Disabled",
                    Pinned = x.Pinned ? "Yes" : "No",
                    Previous = x.PreviousVersion ?? "—"
                }).ToList();
                installed.Columns["PackageId"].Visible = false;
                installed.Columns["Name"].HeaderText = "Mod name";
                installed.Columns["Name"].FillWeight = 200;
            }
            else
            {
                installed.DataSource = null;
                target.Text = "Create a profile to choose a game installation.";
            }
        }
        finally { refreshing = false; }
    }
}
