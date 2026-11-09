namespace Sevo.Mod.Launcher;

public sealed partial class MainForm
{
    private Button BuildGameTools()
    {
        var button = new Button { Text = "Game tools ▾", AutoSize = true, Height = 32,
            Margin = new Padding(0, 0, 8, 8), AccessibleName = "Game tools" };
        var menu = new ContextMenuStrip();
        foreach (var (label, action) in new (string, Func<Task>)[]
        {
            ("Validate profile", ValidateAsync), ("Open game folder", OpenGameAsync),
            ("Remove runtime", RemoveRuntimeAsync), ("Clean game", CleanGameAsync)
        })
            menu.Items.Add(label, null, async (_, _) => await RunAsync(action));
        button.Click += (_, _) => menu.Show(button, new Point(0, button.Height));
        button.Disposed += (_, _) => menu.Dispose();
        return button;
    }

    private Control BuildWorkspace()
    {
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AccessibleName = "Workspace sections", Padding = new Padding(0, 4, 0, 0) };
        var surface = new Panel { Dock = DockStyle.Fill };
        var packInfo = new Label { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0),
            Text = "Export the selected profile as a shareable modpack.\n\n" +
                "Import a modpack to add its exact mod versions and enabled states to the selected profile." };
        var libraryActions = new Label { AutoSize = true, Padding = new Padding(0, 7, 6, 0) };
        var installedActions = new Label { AutoSize = true, Padding = new Padding(0, 7, 6, 0) };
        TrackPicks(library, libraryActions);
        TrackPicks(installed, installedActions);

        var sections = new[]
        {
            WorkspacePage("Installed mods", installed, installedActions,
                Button("Enable", () => ChangeEnabledAsync(true)), Button("Disable", () => ChangeEnabledAsync(false)),
                Button("Review updates", UpdateAsync), Button("Pin", () => ChangePinnedAsync(true)),
                Button("Unpin", () => ChangePinnedAsync(false)), Button("Roll back", RollbackAsync),
                Button("Remove selected", RemoveAsync)),
            WorkspacePage("Package library", library, libraryActions, Button("Import ZIP", ImportAsync),
                Button("Add from GitHub", AddGitHubAsync), Button("Install selected", InstallAsync),
                Button("Remove from library", RemoveFromLibraryAsync)),
            WorkspacePage("Modpacks", packInfo, Button("Export profile", ExportModpackAsync),
                Button("Import modpack", ImportModpackAsync))
        };

        var items = new[] { "Installed mods", "Package library", "Modpacks" }.Select(text =>
            new RadioButton { Text = text, Appearance = Appearance.Button, Width = 147, Height = 34,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(7, 0, 0, 0) }).ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var page = sections[i];
            page.Visible = false;
            surface.Controls.Add(page);
            navigation.Controls.Add(item);
            item.CheckedChanged += (_, _) => page.Visible = item.Checked;
        }
        var refresh = Button("Refresh workspace", () => { RefreshViews(); return Task.CompletedTask; });
        refresh.Margin = new Padding(3, 12, 3, 3);
        navigation.Controls.Add(refresh);
        items[0].Checked = true;
        host.Controls.Add(navigation, 0, 0);
        host.Controls.Add(surface, 1, 0);
        return host;
    }

    private Control WorkspacePage(string title, Control content, params Control[] actions)
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        toolbar.Controls.AddRange(actions);
        page.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 6, 3, 8) }, 0, 0);
        page.Controls.Add(toolbar, 0, 1);
        page.Controls.Add(content, 0, 2);
        return page;
    }

    private static IEnumerable<DataGridViewRow> PickedRows(DataGridView grid) =>
        grid.Rows.Cast<DataGridViewRow>().Where(row => row.Cells["Pick"].Value is true);

    private static void ClearPicks(DataGridView grid)
    {
        foreach (DataGridViewRow row in grid.Rows)
            if (row.Cells["Pick"].Value is true) row.Cells["Pick"].Value = false;
    }

    private static void TrackPicks(DataGridView grid, Label label)
    {
        label.Text = "0 selected";
        grid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex == grid.Columns["Pick"].Index)
                label.Text = $"{PickedRows(grid).Count()} selected";
        };
    }

    private string[] PickedModIds()
    {
        var ids = PickedRows(installed).Select(row => (string)row.Cells["PackageId"].Value).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("Tick at least one installed mod.");
        return ids;
    }
}
