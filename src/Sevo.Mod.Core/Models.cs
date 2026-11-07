using System.Diagnostics;
using System.Text.Json.Serialization;
namespace Sevo.Mod.Core;

public sealed class PlatformException : Exception
{
    public PlatformException(string message) : base(message) { }
    public PlatformException(string message, Exception inner) : base(message, inner) { }
}
public sealed class PackageImportOptions
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Repository { get; set; }
    public string? AssetName { get; set; }
}
public sealed class PackageRecord
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Version { get; set; } = "";
    [JsonRequired] public string Sha256 { get; set; } = "";
    public string? Repository { get; set; }
    public string? AssetName { get; set; }
    [JsonRequired] public string PayloadDirectory { get; set; } = "";
    [JsonRequired] public Dictionary<string, string> Dependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonRequired] public List<string> Conflicts { get; set; } = [];
    [JsonRequired] public List<int> FixedBrickIds { get; set; } = [];
    [JsonRequired] public List<int> FixedShapeIds { get; set; } = [];
    [JsonRequired] public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class ProfileRecord
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string GameDirectory { get; set; } = "";
    [JsonRequired] public List<ProfileModRecord> Mods { get; set; } = [];
    [JsonRequired] public string Generation { get; set; } = "";
}
public sealed class ProfileModRecord
{
    [JsonRequired] public string PackageId { get; set; } = "";
    [JsonRequired] public string Version { get; set; } = "";
    [JsonRequired] public bool Enabled { get; set; }
    [JsonRequired] public bool Pinned { get; set; }
    public string? PreviousVersion { get; set; }
}
public interface IGameProcessInspector { bool IsRunning(string gameDirectory); }
public sealed class GameProcessInspector : IGameProcessInspector
{
    public bool IsRunning(string gameDirectory)
    {
        var expected = Path.GetFullPath(Path.Combine(gameDirectory, "Starship EVO.exe"));
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path != null && string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    if (string.Equals(p.ProcessName, "Starship EVO", StringComparison.OrdinalIgnoreCase))
                        throw new PlatformException("Cannot inspect the running Starship EVO executable; close the game before changing mods.");
                }
                catch (InvalidOperationException) { } // Process exited during enumeration.
            }
        }
        return false;
    }
}
internal sealed class Manifest
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string PayloadRoot { get; set; } = "";
    public string? Repository { get; set; }
    public string? AssetName { get; set; }
    public Dictionary<string,string> Dependencies { get; set; } = [];
    public List<string> Conflicts { get; set; } = [];
    public List<int> FixedBrickIds { get; set; } = [];
    public List<int> FixedShapeIds { get; set; } = [];
}
internal sealed class Catalog { [JsonRequired] public int SchemaVersion { get; set; } = 1; [JsonRequired] public List<PackageRecord> Packages { get; set; } = []; }
internal sealed class Profiles { [JsonRequired] public int SchemaVersion { get; set; } = 1; [JsonRequired] public List<ProfileRecord> Items { get; set; } = []; }
internal sealed class OperationIntent
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public string TransactionId { get; set; } = "";
    [JsonRequired] public ProfileRecord Before { get; set; } = new();
    [JsonRequired] public ProfileRecord After { get; set; } = new();
    [JsonRequired] public Dictionary<string,string> OldFiles { get; set; } = [];
    [JsonRequired] public Dictionary<string,string> NewFiles { get; set; } = [];
}
internal sealed class ProfileCreationIntent
{
    [JsonRequired] public int SchemaVersion { get; set; }=1;
    [JsonRequired] public ProfileRecord Profile { get; set; }=new();
    [JsonRequired] public List<string> CreatedDirectories { get; set; }=[];
}
