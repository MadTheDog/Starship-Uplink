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
}
