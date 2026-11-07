using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

public sealed record RuntimePreparation(string StateDirectory, int FilesInstalled);

/// <summary>Owns known bootstrap files separately from ordinary mod payloads.</summary>
public sealed class RuntimeProvisioner
{
    private readonly string root;
    private readonly IGameProcessInspector processes;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private const string OwnerName = ".sevo-runtime-owned.json", PendingName = ".sevo-runtime-pending.json";
    private const string ConfigPath = "BepInEx/config/BepInEx.cfg";
    public RuntimeProvisioner(string workspaceRoot, IGameProcessInspector? processes = null) { root = Path.GetFullPath(workspaceRoot); SafePath.Check(root); this.processes = processes ?? new GameProcessInspector(); }

    public RuntimePreparation Prepare(string gameDirectory, string stateDirectory, string loaderZip, string frameworkDirectory)
    {
        var game = ValidateGame(gameDirectory); var state = ValidateState(stateDirectory);
        using var guard = Acquire(state); CheckNotRunning(game); Recover(game);
        var desired = ReadPayload(loaderZip, frameworkDirectory);
        var files = desired.Select(x => new OwnedFile { Path = x.Key, Sha256 = Hash(x.Value), Preserve = x.Key == ConfigPath }).ToList();
        var manifest = new RuntimeManifest { Files = files };
        var old = ReadManifest(game, OwnerName);
        if (old != null)
        {
            Verify(game, old, false);
            if (!Equivalent(old, manifest)) throw new InvalidOperationException("A different managed runtime is installed. Remove the owned runtime before replacing it; configuration and mods will be preserved.");
            WriteProfileMarker(game, state); return new(state, files.Count);
        }
        foreach (var file in files)
        {
            var destination = SafePath.Child(game, file.Path);
            if (File.Exists(destination) && !(file.Preserve && ValidConfig(destination))) throw new InvalidOperationException($"Existing unmanaged runtime file would be overwritten: {file.Path}");
            if (Directory.Exists(destination)) throw new InvalidOperationException($"Runtime file path is a directory: {file.Path}");
        }
        WriteProfileMarker(game, state);
        AtomicJson(SafePath.Child(game, PendingName), manifest);
        try
        {
            foreach (var file in files)
            {
                var target = SafePath.Child(game, file.Path);
                if (file.Preserve && File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(desired[file.Path]); output.Flush(true);
            }
            AtomicJson(SafePath.Child(game, OwnerName), manifest);
            File.Delete(SafePath.Child(game, PendingName)); return new(state, files.Count);
        }
        catch { Recover(game); throw; }
    }

    public void Remove(string gameDirectory, string stateDirectory)
    {
        var game = ValidateGame(gameDirectory); var state = ValidateState(stateDirectory);
        using var guard = Acquire(state); CheckNotRunning(game); Recover(game);
        var manifest = ReadManifest(game, OwnerName) ?? throw new InvalidOperationException("No manager-owned runtime is installed.");
        Verify(game, manifest, true);
        foreach (var file in manifest.Files.Where(x => !x.Preserve)) File.Delete(SafePath.Child(game, file.Path));
        File.Delete(SafePath.Child(game, OwnerName));
        var marker = SafePath.Child(state, "sevo-runtime-profile.json"); if (File.Exists(marker)) File.Delete(marker);
    }

    public void ValidatePrepared(string gameDirectory, string stateDirectory)
    {
        var game = ValidateGame(gameDirectory); var state = ValidateState(stateDirectory);
        using var guard = Acquire(state); CheckNotRunning(game); Recover(game);
        var manifest = ReadManifest(game, OwnerName) ?? throw new InvalidOperationException("Prepare the runtime before launching this profile.");
        Verify(game, manifest, false); WriteProfileMarker(game, state);
    }

    private static Dictionary<string, byte[]> ReadPayload(string loaderZip, string frameworkDirectory)
    {
        SafePath.Check(Path.GetFullPath(loaderZip)); SafePath.Check(Path.GetFullPath(frameworkDirectory));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        using var zip = ZipFile.OpenRead(loaderZip);
        if (zip.Entries.Count > 256) throw new InvalidOperationException("Unexpected loader archive entry count.");
        foreach (var entry in zip.Entries)
        {
            var path = SafePath.Relative(entry.FullName.TrimEnd('/'));
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & 0x400) != 0) throw new InvalidOperationException("Archive links are refused.");
            if (entry.FullName.EndsWith('/')) continue;
            if (!Allowed(path) || path == ConfigPath || path.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Unexpected loader payload path: {path}");
            if (entry.Length > 16 * 1024 * 1024 || (expanded += entry.Length) > 64 * 1024 * 1024) throw new InvalidOperationException("Loader archive exceeds size limits.");
            using var input = entry.Open(); using var buffer = new MemoryStream(); var bytes = new byte[81920]; int read;
            while ((read = input.Read(bytes)) != 0) { if (buffer.Length + read > entry.Length) throw new InvalidOperationException("Archive expanded beyond its declared size."); buffer.Write(bytes, 0, read); }
            if (buffer.Length != entry.Length || !files.TryAdd(path, buffer.ToArray())) throw new InvalidOperationException("Truncated or duplicate loader entry.");
        }
        foreach (var required in new[] { "winhttp.dll", "doorstop_config.ini", "BepInEx/core/BepInEx.dll", "BepInEx/core/BepInEx.Preloader.dll" })
            if (!files.ContainsKey(required)) throw new InvalidOperationException($"Loader archive is missing {required}.");
        foreach (var name in new[] { "Sevo.Framework.Runtime.dll", "Sevo.Framework.Sdk.dll" })
        {
            var source = SafePath.Child(frameworkDirectory, name);
            if (!File.Exists(source)) throw new InvalidOperationException($"Framework build is missing {name}.");
            files.Add("BepInEx/plugins/SevoPlatform/" + name, File.ReadAllBytes(source));
        }
        files.Add(ConfigPath, Encoding.UTF8.GetBytes("[Chainloader]\nHideManagerGameObject = true\n")); return files;
    }
    private string ValidateState(string path)
    {
        var state = SafePath.Child(root, Path.GetRelativePath(root, Path.GetFullPath(path)));
        Directory.CreateDirectory(state); return state;
    }
    private static string ValidateGame(string path)
    {
        var game = Path.GetFullPath(path); SafePath.Check(game);
        if (!File.Exists(SafePath.Child(game, "Starship EVO.exe")) || !File.Exists(SafePath.Child(game, "Starship EVO_Data/Managed/Assembly-CSharp.dll"))) throw new InvalidOperationException("Choose a Starship EVO installation containing its executable and managed assembly.");
        return game;
    }
    private static FileStream Acquire(string state)
    {
        try { return new FileStream(SafePath.Child(state, ".runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("Another runtime operation is in progress.", ex); }
    }
    public static void AssertNotRunning(string game)
    {
        SafePath.Check(Path.GetFullPath(game));
        if (new GameProcessInspector().IsRunning(game)) throw new InvalidOperationException("Close this Starship EVO installation before changing its runtime.");
    }
    private void CheckNotRunning(string game)
    {
        if (processes.IsRunning(game)) throw new InvalidOperationException("Close this Starship EVO installation before changing its runtime.");
    }
    private static RuntimeManifest? ReadManifest(string game, string name)
    {
        var path = SafePath.Child(game, name); if (!File.Exists(path)) return null; RuntimeManifest value;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("Runtime record exceeds its size limit; preserving all files.");
        try
        {
            var text = File.ReadAllText(path); using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Runtime record must be an object.");
            var properties = document.RootElement.EnumerateObject().ToList();
            if (properties.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1)) throw new JsonException("Duplicate runtime record property.");
            var schema = properties.FirstOrDefault(x => x.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase));
            var entries = properties.FirstOrDefault(x => x.Name.Equals("files", StringComparison.OrdinalIgnoreCase));
            if (schema.Value.ValueKind != JsonValueKind.Number || !schema.Value.TryGetInt32(out var version) || version != 1 || entries.Value.ValueKind != JsonValueKind.Array) throw new JsonException("Runtime record requires explicit schemaVersion1 and files array.");
            value = JsonSerializer.Deserialize<RuntimeManifest>(text, Json) ?? throw new JsonException("Empty runtime record");
        }
        catch (JsonException ex) { throw new InvalidOperationException("Runtime ownership/recovery record is invalid; no files were changed.", ex); }
        if (value.SchemaVersion != 1 || value.Files == null || value.Files.Count is < 1 or > 256) throw new InvalidOperationException("Unsupported runtime record.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in value.Files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Sha256)) throw new InvalidOperationException("Runtime ownership entry requires a path and SHA256; preserving all files.");
            file.Path = SafePath.Relative(file.Path);
            if (!Allowed(file.Path) || !seen.Add(file.Path) || file.Sha256.Length != 64 || file.Sha256.Any(x => !Uri.IsHexDigit(x)) || file.Preserve != (file.Path == ConfigPath)) throw new InvalidOperationException("Invalid runtime ownership entry.");
            SafePath.Child(game, file.Path);
        }
        return value;
    }
    private static void Verify(string game, RuntimeManifest manifest, bool allowMissing)
    {
        foreach (var file in manifest.Files)
        {
            var path = SafePath.Child(game, file.Path);
            if (!File.Exists(path)) { if (!allowMissing) throw new InvalidOperationException("Managed runtime file is missing: " + file.Path); continue; }
            if (file.Preserve) { if (!ValidConfig(path)) throw new InvalidOperationException("Set [Chainloader] HideManagerGameObject = true in BepInEx.cfg before preparing the runtime."); }
            else if (Hash(File.ReadAllBytes(path)) != file.Sha256) throw new InvalidOperationException($"Runtime file was changed outside the manager; preserving it: {file.Path}");
        }
    }
    private static void Recover(string game)
    {
        var pending = ReadManifest(game, PendingName); if (pending == null) return;
        var complete = ReadManifest(game, OwnerName); Verify(game, pending, true);
        if (complete != null)
        {
            if (!Equivalent(complete, pending) || pending.Files.Any(x => !File.Exists(SafePath.Child(game, x.Path)))) throw new InvalidOperationException("Ambiguous runtime recovery state; preserving all files for inspection.");
        }
        else foreach (var file in pending.Files.Where(x => !x.Preserve)) File.Delete(SafePath.Child(game, file.Path));
        File.Delete(SafePath.Child(game, PendingName));
    }
    private static bool Equivalent(RuntimeManifest a, RuntimeManifest b) => a.Files.Count == b.Files.Count && a.Files.All(x => b.Files.Any(y => x.Path == y.Path && x.Sha256 == y.Sha256 && x.Preserve == y.Preserve));
    private static bool ValidConfig(string path)
    {
        bool chainloader = false;
        foreach (var line in File.ReadLines(path).Select(x => x.Trim()))
        {
            if (line.StartsWith('[')) chainloader = line.Equals("[Chainloader]", StringComparison.OrdinalIgnoreCase);
            if (chainloader && System.Text.RegularExpressions.Regex.IsMatch(line, @"^HideManagerGameObject\s*=\s*true\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }
    private static bool Allowed(string path) => path is "winhttp.dll" or "doorstop_config.ini" or ".doorstop_version" or "changelog.txt" or ConfigPath || path.StartsWith("BepInEx/core/", StringComparison.Ordinal) && !path[13..].Contains('/') || path is "BepInEx/plugins/SevoPlatform/Sevo.Framework.Runtime.dll" or "BepInEx/plugins/SevoPlatform/Sevo.Framework.Sdk.dll";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void AtomicJson<T>(string path, T value)
    {
        SafePath.Check(path); var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, value, Json); file.Flush(true); }
        try { File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void WriteProfileMarker(string game, string state)
    {
        var marker = SafePath.Child(state, "sevo-runtime-profile.json");
        if (File.Exists(marker))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.GetProperty("schemaVersion").GetInt32() != 1 || !Path.GetFullPath(json.RootElement.GetProperty("gameDirectory").GetString()!).Equals(game, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Runtime state belongs to another game installation.");
            return;
        }
        AtomicJson(marker, new { schemaVersion = 1, profileId = new DirectoryInfo(state).Name, gameDirectory = game });
    }
    public sealed class RuntimeManifest { public int SchemaVersion { get; set; } = 1; public List<OwnedFile> Files { get; set; } = []; }
    public sealed class OwnedFile { public string Path { get; set; } = ""; public string Sha256 { get; set; } = ""; public bool Preserve { get; set; } }
}
