namespace Sevo.Mod.Launcher;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var root = AppContext.BaseDirectory;
        string? workspace = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--root") root = Path.GetFullPath(args[i + 1]);
            if (args[i] == "--workspace")
            {
                if (!Path.IsPathFullyQualified(args[i + 1]))
                {
                    MessageBox.Show("Use an absolute path for --workspace.", "Workspace could not open", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                workspace = Path.GetFullPath(args[i + 1]);
            }
        }
        Application.Run(new MainForm(root, workspace));
    }
}
