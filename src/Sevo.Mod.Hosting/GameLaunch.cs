using System.Diagnostics;
using System.Text.Json;

namespace Sevo.Mod.Hosting;

public sealed record LaunchTicket(ProcessStartInfo StartInfo, string Session);
public static class GameLaunch
{
    public static LaunchTicket Create(string gameDirectory, string stateDirectory) =>
        Create(gameDirectory, stateDirectory, vanilla: false);

    public static LaunchTicket CreateVanilla(string gameDirectory, string stateDirectory) =>
        Create(gameDirectory, stateDirectory, vanilla: true);

    private static LaunchTicket Create(string gameDirectory, string stateDirectory, bool vanilla)
    {
        var game = Path.GetFullPath(gameDirectory); var state = Path.GetFullPath(stateDirectory);
        SafePath.Check(game); SafePath.Check(state); Directory.CreateDirectory(state);
        var executable = SafePath.Child(game, "Starship EVO.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("Starship EVO executable is missing.");
        var session = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = game, UseShellExecute = false };
        if (vanilla)
        {
            start.Environment.Remove("SEVO_PLATFORM_STATE_DIR");
            start.Environment.Remove("SEVO_PLATFORM_SESSION");
            start.Environment["DOORSTOP_DISABLE"] = "TRUE";
            start.ArgumentList.Add("--doorstop-enabled");
            start.ArgumentList.Add("false");
        }
        else
        {
            start.Environment["SEVO_PLATFORM_STATE_DIR"] = state;
            start.Environment["SEVO_PLATFORM_SESSION"] = session;
        }
        start.ArgumentList.Add("-logFile"); start.ArgumentList.Add(SafePath.Child(state, "game-" + session + ".log"));
        return new(start, session);
    }
    public static string? ReadSessionStatus(string stateDirectory, string session, int processId)
    {
        var path = SafePath.Child(stateDirectory, "runtime-status.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
        try
        {
            var text = File.ReadAllText(path); using var document = JsonDocument.Parse(text); var value = document.RootElement;
            if (!value.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 || !value.TryGetProperty("session", out var token) || token.GetString() != session || !value.TryGetProperty("processId", out var pid) || pid.GetInt32() != processId) return null;
            return text;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException) { return null; }
    }
}
