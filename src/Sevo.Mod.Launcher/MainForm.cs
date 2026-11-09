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
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 226, AccessibleName = "Active mod profile" };
    private readonly DataGridView library = Grid("Package library"), installed = Grid("Installed mods");
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "Activity and diagnostics" };
    private readonly Label target = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Create a profile to choose a game installation." };
    private readonly Label status = new() { AutoSize = true, Text = "Changes apply after restart", Margin = new Padding(10, 10, 0, 0) };
    private bool busy, refreshing, refreshPending, closed;
    private bool CanUseControls => !closed && !IsDisposed && !Disposing;
    private sealed record ProfileChoice(string Id, string Name) { public override string ToString() => Name; }

    public MainForm(string root, string? workspace = null)
    {
        appRoot = Path.GetFullPath(root); dataRoot = workspace == null ? Path.Combine(appRoot, "data") : Path.GetFullPath(workspace); service = new PlatformService(dataRoot);
        Text = "Starship Uplink";
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(800, 540);
        using var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("LandingPort.ico")!;
        using var iconResource = new Icon(iconStream);
        var windowIcon = (Icon)iconResource.Clone();
        Icon = windowIcon;
        Disposed += (_, _) => windowIcon.Dispose();
        var available = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
        Size = new Size(Math.Min(1180, available.Width - 30), Math.Min(840, available.Height - 45));
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(245, 247, 250);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StarshipUplink/" + UplinkVersion);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        commands.Controls.Add(new Label { Text = "Starship Uplink", Font = new Font(Font.FontFamily, 22, FontStyle.Bold),
            AutoSize = true, Margin = new Padding(3, 0, 14, 3) });
        commands.Controls.Add(Button("Update Uplink", UpdateLauncherAsync));
        keelButton = Button("Update Keel", UpdateKeelAsync);
        commands.Controls.Add(keelButton);
        commands.Controls.Add(status);
        var profileBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoSize = true };
        profileBar.Controls.Add(new Label { Text = "Profile", AutoSize = true, Margin = new Padding(0, 8, 12, 0) }); profileBar.Controls.Add(profiles);
        profileBar.Controls.Add(Button("New profile", NewProfileAsync));
        profileBar.Controls.Add(Button("Prepare game", PrepareAsync));
        profileBar.Controls.Add(Button("Play", PlayAsync));
        profileBar.Controls.Add(BuildGameTools());
        layout.Controls.Add(commands, 0, 0);
        layout.Controls.Add(BuildVersionBox(), 0, 1);
        layout.Controls.Add(profileBar, 0, 2);
        layout.Controls.Add(target, 0, 3);
        layout.Controls.Add(BuildWorkspace(), 0, 4);
        layout.Controls.Add(BuildActivityPanel(), 0, 5);
        Controls.Add(layout);
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
            if (CanUseControls) await Task.WhenAll(NoticeLauncherUpdateAsync(), NoticeKeelUpdateAsync());
        };
        FormClosed += (_, _) => { closed = true; http.Dispose(); };
    }
    private static DataGridView Grid(string name)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, MultiSelect = false,
            RowHeadersVisible = false, AccessibleName = name, BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None, EditMode = DataGridViewEditMode.EditOnEnter
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Pick", HeaderText = "Select", Width = 66,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.ColumnAdded += (_, e) =>
        {
            if (e.Column.Name != "Pick") e.Column.ReadOnly = true;
        };
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        return grid;
    }
    private Control BuildActivityPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(3, 4, 3, 0), AccessibleName = "Activity panel" };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "Activity", AutoSize = true,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        log.Margin = Padding.Empty;
        panel.Controls.Add(log, 0, 1);
        return panel;
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
            ClearPicks(library);
            ClearPicks(installed);
            var packages = service.ListPackages();
            library.DataSource = packages.Select(x => new { x.Id, x.Name, x.Version, Repository = x.Repository ?? "Local ZIP" }).ToList();
            if (profiles.SelectedItem is ProfileChoice choice)
            {
                var profile = service.GetProfile(choice.Id); target.Text = "Game: " + profile.GameDirectory;
                RefreshKeelButton(profile.GameDirectory);
                var names = packages.ToDictionary(x => (x.Id, x.Version), x => x.Name);
                installed.DataSource = profile.Mods.Select(x => new
                {
                    Name = names.GetValueOrDefault((x.PackageId, x.Version), x.PackageId),
                    x.PackageId,
                    x.Version,
                    State = x.Enabled ? "Enabled" : "Disabled",
                    Update = UpdateStatus(profile.Id, x),
                    Pinned = x.Pinned ? "Yes" : "No",
                    Previous = x.PreviousVersion ?? "—"
                }).ToList();
                installed.Columns["PackageId"].Visible = false;
                installed.Columns["Name"].HeaderText = "Mod name";
                installed.Columns["Name"].FillWeight = 200;
            }
            else
            {
                RefreshKeelButton(null);
                installed.DataSource = null;
                target.Text = "Create a profile to choose a game installation.";
            }
        }
        finally { refreshing = false; }
    }
}
