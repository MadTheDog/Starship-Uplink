using System.Text.RegularExpressions;
using Sevo.Mod.Core;

namespace Sevo.Mod.Launcher;

public sealed partial class MainForm
{
    private const string SupportedGameBuild = "26451593";
    private const string SupportedGameVersion = "26w45A";

    private static string? InstalledSteamBuild(string directory)
    {
        var game = new DirectoryInfo(directory);
        var common = game.Parent;
        if (common?.Parent is not { } steamApps ||
            !common.Name.Equals("common", StringComparison.OrdinalIgnoreCase) ||
            !steamApps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var manifest = File.ReadAllText(Path.Combine(steamApps.FullName, "appmanifest_711980.acf"));
            string Field(string name) => Regex.Match(manifest, "^\\s*\"" + name + "\"[ \\t]+\"([^\"\\r\\n]+)\"", RegexOptions.Multiline).Groups[1].Value;
            if (Field("appid") != "711980" || !Field("installdir").Equals(game.Name, StringComparison.OrdinalIgnoreCase))
                return null;
            var build = Field("buildid");
            return build.Length > 0 && build.All(char.IsAsciiDigit) ? build : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GameBuildLabel(string? build) => build switch
    {
        SupportedGameBuild => $"{SupportedGameVersion} (Steam build {build}, supported)",
        null => "version unverified",
        _ => $"Steam build {build} (unsupported)"
    };

    private void ShowGameBuild(string directory)
    {
        var build = InstalledSteamBuild(directory);
        string running;
        try { running = new GameProcessInspector().IsRunning(directory) ? " • Running" : ""; }
        catch (PlatformException) { running = " • Running state unverified"; }
        target.Text = $"{GameBuildLabel(build)}{running} • {directory}";
    }

    private static string? GameBuildWarning(string directory)
    {
        var build = InstalledSteamBuild(directory);
        return build == SupportedGameBuild ? null :
            $"Detected game version: {GameBuildLabel(build)}. The supported baseline is {SupportedGameVersion} (Steam build {SupportedGameBuild}).";
    }
}
