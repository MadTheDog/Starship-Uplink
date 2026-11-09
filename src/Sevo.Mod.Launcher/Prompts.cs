namespace Sevo.Mod.Launcher;

internal static class Prompts
{
    public static string? Text(IWin32Window owner, string title, string label, string value = "")
    {
        using var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(520, 145), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var caption = new Label { Text = label, Left = 16, Top = 14, Width = 485, Height = 26 };
        var input = new TextBox { Text = value, Left = 16, Top = 43, Width = 485, AccessibleName = label };
        var ok = new Button { Text = "Continue", DialogResult = DialogResult.OK, Left = 303, Top = 96, Width = 95 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 406, Top = 96, Width = 95 };
        form.Controls.AddRange([caption, input, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
    }
    public static string? Choose(IWin32Window owner, string title, IEnumerable<string> choices)
    {
        using var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(650, 280), MinimizeBox = false, MaximizeBox = false };
        var list = new ListBox { Left = 12, Top = 12, Width = 626, Height = 210, AccessibleName = title };
        list.Items.AddRange(choices.Cast<object>().ToArray()); if (list.Items.Count > 0) list.SelectedIndex = 0;
        var ok = new Button { Text = "Select", DialogResult = DialogResult.OK, Left = 438, Top = 236, Width = 95 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 541, Top = 236, Width = 95 };
        form.Controls.AddRange([list, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? list.SelectedItem as string : null;
    }
    public static bool Review(IWin32Window owner, string title, string summary, IEnumerable<string> lines, string? accept)
    {
        using var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(680, 360), MinimizeBox = false, MinimumSize = new Size(460, 300) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = summary, AutoSize = true }, 0, 0);
        layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, Text = string.Join(Environment.NewLine, lines),
            AccessibleName = "Review details" }, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = accept == null ? "Close" : "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        actions.Controls.Add(cancel);
        if (accept != null) actions.Controls.Add(new Button { Text = accept, DialogResult = DialogResult.OK, AutoSize = true });
        layout.Controls.Add(actions, 0, 2);
        form.Controls.Add(layout);
        form.CancelButton = cancel;
        form.ActiveControl = cancel;
        return form.ShowDialog(owner) == DialogResult.OK;
    }
}
