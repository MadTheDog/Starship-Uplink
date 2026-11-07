using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

public static partial class LauncherUpdater
{
    const string InventoryFile = "SHA256SUMS.txt";
    const long MaximumDownloadBytes = 512L * 1024 * 1024; // GitHubReleaseClient's streaming bound.
    const long MaximumPackageBytes = 64L * 1024 * 1024;
    const int MaximumInventoryBytes = 16 * 1024;
    static readonly string[] RequiredFiles =
    [
        Executable, "Starship Uplink.dll", "Starship Uplink.deps.json", "Starship Uplink.runtimeconfig.json",
        "Sevo.Mod.Core.dll", "Sevo.Mod.Hosting.dll", "cli/Sevo.Mod.Cli.exe", "cli/Sevo.Mod.Cli.dll",
        "cli/Sevo.Mod.Cli.deps.json", "cli/Sevo.Mod.Cli.runtimeconfig.json", "cli/Sevo.Mod.Core.dll",
        "framework/Sevo.Framework.Runtime.dll", "framework/Sevo.Framework.Sdk.dll"
    ];
    static readonly HashSet<string> OwnedFiles = new(RequiredFiles.Concat(["README.md", "SOURCE-COMMIT.txt", "KEEL-RELEASE.txt"]),
        StringComparer.Ordinal);
    static readonly HashSet<string> PackageDirectories = new(["cli", "framework"], StringComparer.Ordinal);
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static string ReadReleaseChecksum(string path, string archiveName)
    {
        var line = ReadText(path, 1024).TrimEnd('\r', '\n');
        var (hash, fileName) = ParseChecksumLine(line);
        if (fileName != archiveName)
            throw new PlatformException("The official SHA-256 file must name the Uplink Windows ZIP.");
        return hash;
    }

    static (string Hash, string File) ParseChecksumLine(string line)
    {
        if (line.Length < 67 || line[64] != ' ' || line[65] != ' ' || !IsDigest(line[..64]))
            throw new PlatformException("An Uplink checksum line is malformed.");
        return (line[..64].ToLowerInvariant(), line[66..]);
    }

    static bool IsDigest(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    static Dictionary<string, string> ReadInventory(string root, bool exactTree = false, string? expectedInventory = null)
    {
        string inventoryPath = Child(root, InventoryFile);
        byte[] inventoryBytes = ReadBytes(inventoryPath, MaximumInventoryBytes);
        string inventoryHash = Digest(inventoryBytes);
        if (expectedInventory is not null && inventoryHash != expectedInventory)
            throw new PlatformException("The staged Uplink checksum inventory changed.");

        var inventory = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines = DecodeText(inventoryBytes).TrimEnd('\r', '\n').Split('\n');
        foreach (string line in lines)
        {
            var (hash, relative) = ParseChecksumLine(line.TrimEnd('\r'));
            if (!OwnedFiles.Contains(relative) || !inventory.TryAdd(relative, hash))
                throw new PlatformException("The Uplink checksum inventory contains an unexpected or duplicate package path: " + relative);
        }
        if (!RequiredFiles.All(inventory.ContainsKey))
            throw new PlatformException("The Uplink checksum inventory does not cover all launcher, CLI and Keel binaries.");
        long total = inventoryBytes.Length;
        foreach (var (relative, hash) in inventory)
        {
            string path = Child(root, relative);
            total += RequireFile(path, MaximumPackageBytes);
            if (total > MaximumPackageBytes) throw new PlatformException("The Uplink package exceeds 64 MiB.");
            if (HashFile(path) != hash) throw new PlatformException("A managed Uplink package file changed: " + relative);
        }
        inventory.Add(InventoryFile, inventoryHash);

        if (exactTree)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(root))
            {
                string name = Path.GetFileName(path);
                if (PackageDirectories.Contains(name))
                {
                    RequireDirectory(path);
                    foreach (string file in Directory.EnumerateFileSystemEntries(path))
                        if (!inventory.ContainsKey(name + "/" + Path.GetFileName(file)))
                            throw new PlatformException("The staged Uplink package contains an unlisted file: " + file);
                }
                else if (!inventory.ContainsKey(name))
                    throw new PlatformException("The staged Uplink package contains an unlisted file: " + name);
            }
        }
        return inventory;
    }

    static void ExtractArchive(string path, string directoryName, string root, string expectedSha, CancellationToken token)
    {
        RequireFile(path, MaximumPackageBytes);
        using var archiveBytes = File.OpenRead(path);
        if (archiveBytes.Length > MaximumPackageBytes) throw new PlatformException("The Uplink ZIP exceeds 64 MiB.");
        if (!Digest(archiveBytes).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new PlatformException("The downloaded Uplink ZIP changed before staging.");
        archiveBytes.Position = 0;
        try
        {
            using var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Read);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = directoryName + "/";
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                string name = entry.FullName;
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || !seen.Add(name))
                    throw new PlatformException("The Uplink ZIP has an unexpected or duplicate entry: " + name);
                string relative = name[prefix.Length..];
                bool directory = relative.Length == 0 || relative.EndsWith('/');
                int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                var dosAttributes = (FileAttributes)(entry.ExternalAttributes & 0xffff);
                if ((dosAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                    (!directory && (dosAttributes & FileAttributes.Directory) != 0) ||
                    (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)))
                    throw new PlatformException("The Uplink ZIP contains a link or special entry: " + name);
                if (directory)
                {
                    if (entry.Length != 0 || (relative.Length != 0 && !PackageDirectories.Contains(relative[..^1])))
                        throw new PlatformException("The Uplink ZIP has an unexpected directory: " + name);
                    continue;
                }
                if (relative != InventoryFile && !OwnedFiles.Contains(relative))
                    throw new PlatformException("The Uplink ZIP contains an unowned package path: " + relative);
                long limit = relative == InventoryFile ? MaximumInventoryBytes : MaximumPackageBytes;
                if (entry.Length > limit || entry.Length > MaximumPackageBytes - total)
                    throw new PlatformException("The expanded Uplink ZIP exceeds its size limit.");
                total += entry.Length;

                string destination = Child(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyBounded(input, output, entry.Length, token);
                output.Flush(flushToDisk: true);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new PlatformException("The Uplink release ZIP is invalid.", ex);
        }
    }

    static void CopyBounded(Stream input, Stream output, long length, CancellationToken token = default)
    {
        var buffer = new byte[81920];
        int read;
        long remaining = length;
        while ((read = input.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (read > remaining) throw new PlatformException("An Uplink package file exceeded its declared size.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        if (remaining != 0) throw new PlatformException("An Uplink package file was truncated.");
    }

    static string NormalizeAbsolute(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new PlatformException("Use an absolute Uplink installation or workspace path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    static string Child(string root, string relative)
    {
        string fullPath;
        try { fullPath = SafePath.Child(root, relative); }
        catch (InvalidOperationException ex) { throw new PlatformException(ex.Message, ex); }
        for (string? parent = fullPath; parent is not null; parent = Path.GetDirectoryName(parent))
            Attributes(parent);
        return fullPath;
    }

    static FileAttributes? Attributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new PlatformException("The Uplink updater refuses links, junctions and device paths: " + path);
            return attributes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    static void RequireDirectory(string path)
    {
        if (Attributes(path) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
            throw new PlatformException("An expected Uplink package directory is missing or occupied: " + path);
    }

    static long RequireFile(string path, long limit)
    {
        if (Attributes(path) is not { } attributes || (attributes & FileAttributes.Directory) != 0)
            throw new PlatformException("An expected Uplink package file is missing or occupied: " + path);
        long length = new FileInfo(path).Length;
        if (length > limit) throw new PlatformException("An Uplink package file exceeds its allowed size: " + path);
        return length;
    }

    static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaximumPackageBytes) throw new PlatformException("An Uplink package file exceeds its allowed size: " + path);
        return Digest(input);
    }

    static string Digest(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static byte[] ReadBytes(string path, int limit)
    {
        RequireFile(path, limit);
        using var input = File.OpenRead(path);
        using var output = new MemoryStream();
        if (input.Length > limit) throw new PlatformException("An Uplink record exceeds its allowed size: " + path);
        CopyBounded(input, output, input.Length);
        return output.ToArray();
    }

    static string DecodeText(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException ex) { throw new PlatformException("An Uplink record is not valid UTF-8.", ex); }
    }

    static string ReadText(string path, int limit) => DecodeText(ReadBytes(path, limit));
}
