using System.Diagnostics;
using Sevo.Mod.Hosting;

namespace Sevo.Mod.Launcher;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args is ["--apply-launcher-update", var plan])
            {
                LauncherUpdater.ApplyAndRestart(plan);
                return;
            }
            RunLauncher(args);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Uplink could not open", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static void RunLauncher(string[] args)
    {
        var root = AppContext.BaseDirectory;
        using var launcherLock = LauncherUpdater.AcquireLauncherLock(root);
        var recovery = LauncherUpdater.GetRecoveryStartInfo(root);
        if (recovery != null)
        {
            using var helper = Process.Start(recovery) ?? throw new InvalidOperationException("Uplink could not resume its update.");
            return;
        }
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
