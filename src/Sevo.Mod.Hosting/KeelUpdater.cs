using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

public sealed class KeelUpdateOffer
{
    internal KeelUpdateOffer(Version version, ReleaseAsset archive, ReleaseAsset? checksum, bool isPreview)
        => (Version, Archive, Checksum, IsPreview) = (version, archive, checksum, isPreview);
    public Version Version { get; }
    public bool IsPreview { get; }
    internal ReleaseAsset Archive { get; }
    internal ReleaseAsset? Checksum { get; }
}

public static class KeelUpdater
{
    const string Repository = "MadTheDog/Starship-Keel";
    const string Releases = "https://github.com/" + Repository + "/releases/";
    internal const string PluginDirectory = "BepInEx/plugins/SevoPlatform/";
    internal const int MaxDllBytes = 8 * 1024 * 1024;
    internal static readonly string[] Files = ["Sevo.Framework.Runtime.dll", "Sevo.Framework.Sdk.dll"];

    public static async Task<KeelUpdateOffer?> CheckAsync(HttpClient http, Version installed, CancellationToken token = default)
    {
        var releases = await new GitHubReleaseClient(http).GetPublishedAsync(Repository, token).ConfigureAwait(false);
        var newest = releases.Select(release => (Release: release, Version: ReleaseVersion(release.TagName)))
            .Where(candidate => candidate.Version != null).OrderByDescending(candidate => candidate.Version).FirstOrDefault();
        var (release, version) = newest;
        if (version == null || version <= new Version(installed.Major, installed.Minor, Math.Max(installed.Build, 0))) return null;
        string tag = release.TagName;
        if (release.PageUrl != Releases + "tag/" + tag)
            throw new PlatformException("The Keel release URL does not match its version tag.");

        ReleaseAsset? Asset(string name)
        {
            var matches = release.Assets.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length != 1 || matches[0].Name != name || matches[0].DownloadUrl != Releases + "download/" + tag + "/" + name)
                throw new PlatformException("The official Keel asset is ambiguous or its URL is unexpected: " + name);
            return matches[0];
        }

        var basename = $"Starship-Keel-{version}-BepInEx";
        var archive = Asset(basename + ".zip") ?? throw new PlatformException("The Keel release is missing its official BepInEx ZIP.");
        var checksum = archive.Sha256 == null ? Asset(basename + ".sha256.txt") : null;
        if (archive.Sha256 == null && checksum == null)
            throw new PlatformException("The Keel release has no official SHA-256 for its BepInEx ZIP.");
        return new KeelUpdateOffer(version, archive, checksum, release.Prerelease);
    }

    private static Version? ReleaseVersion(string tag) =>
        tag.StartsWith('v') && Version.TryParse(tag[1..], out var version) && version.Build >= 0 &&
        version.Revision < 0 && tag == "v" + version.ToString(3) ? version : null;

    public static async Task ApplyAsync(HttpClient http, KeelUpdateOffer offer, RuntimeProvisioner provisioner,
        string game, string state, string downloads, CancellationToken token = default)
    {
        var client = new GitHubReleaseClient(http);
        var archive = offer.Archive;
        if (offer.Checksum != null)
        {
            var checksum = await ReleaseDownloads.DownloadAsync(client, offer.Checksum, downloads, token).ConfigureAwait(false);
            using var stream = File.OpenRead(checksum);
            if (stream.Length > 1024) throw new PlatformException("The Keel checksum file exceeds its size limit.");
            using var reader = new StreamReader(stream);
            var line = (await reader.ReadToEndAsync(token).ConfigureAwait(false)).TrimEnd('\r', '\n');
            if (line.Length < 67 || !line[..64].All(Uri.IsHexDigit) || line[64..] != "  " + archive.Name)
                throw new PlatformException("The official checksum must name the Keel BepInEx ZIP.");
            archive = new ReleaseAsset { Name = archive.Name, DownloadUrl = archive.DownloadUrl, Sha256 = line[..64] };
        }
        var path = await ReleaseDownloads.DownloadAsync(client, archive, downloads, token).ConfigureAwait(false);
        // ZIP parsing and game mutation are synchronous. Keep them off the UI thread.
        await Task.Run(() =>
        {
            var payload = ReadArchive(path, archive.Sha256!, offer.Version);
            token.ThrowIfCancellationRequested();
            provisioner.UpdateKeel(game, state, offer.Version, payload);
        }, token).ConfigureAwait(false);
    }

    internal static byte[][] ReadArchive(string path, string digest, Version version)
    {
        SafePath.Check(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 2L * MaxDllBytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new PlatformException("The downloaded Keel ZIP is too large or failed SHA-256 verification.");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payload = new byte[Files.Length][];
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            bool directory = name.EndsWith('/');
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            var attributes = (FileAttributes)(entry.ExternalAttributes & 0xffff);
            if (!seen.Add(name) || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                (!directory && (attributes & FileAttributes.Directory) != 0) ||
                (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)))
                throw new PlatformException("The Keel ZIP contains a duplicate or special entry: " + name);
            if (directory && entry.Length == 0 && name is "BepInEx/" or "BepInEx/plugins/" or PluginDirectory)
                continue;
            var index = Array.FindIndex(Files, file => PluginDirectory + file == name);
            if (index < 0 || entry.Length is <= 0 or > MaxDllBytes)
                throw new PlatformException("The Keel ZIP contains an unexpected or oversized entry: " + name);
            using var input = entry.Open();
            var bytes = new byte[(int)entry.Length];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new PlatformException("The expanded Keel DLL exceeds its declared size.");
            payload[index] = bytes;
        }
        ValidatePayload(payload, version);
        return payload;
    }

    internal static void ValidatePayload(byte[][] payload, Version expected)
    {
        if (payload.Length != Files.Length) throw new PlatformException("The Keel payload is incomplete.");
        for (int i = 0; i < Files.Length; i++)
        {
            if (payload[i] is not { Length: > 0 and <= MaxDllBytes })
                throw new PlatformException("The Keel payload is missing " + Files[i] + " or exceeds its size limit.");
            using var stream = new MemoryStream(payload[i], writable: false);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var assembly = metadata.GetAssemblyDefinition();
            if (metadata.GetString(assembly.Name) + ".dll" != Files[i] || assembly.Version != new Version(expected.ToString(3) + ".0"))
                throw new PlatformException("The Keel DLL identity or version does not match the release: " + Files[i]);
        }
    }
}
