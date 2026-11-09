using System.Reflection;
using Sevo.Mod.Core;

namespace Sevo.Mod.Hosting;

public static partial class LauncherUpdater
{
    private static Version ReadBundledKeelVersion(string root)
    {
        try
        {
            var identities = BundledKeelPaths.Select(path => AssemblyName.GetAssemblyName(Child(root, path))).ToArray();
            var version = identities[0].Version;
            if (version is not { Build: >= 0, Revision: 0 } || identities[1].Version != version ||
                identities.Where((identity, index) => identity.Name + ".dll" != KeelUpdater.Files[index]).Any())
                throw new PlatformException("The bundled Keel runtime and SDK do not identify the same release.");
            return new Version(version.ToString(3));
        }
        catch (BadImageFormatException ex)
        {
            throw new PlatformException("A bundled Keel DLL is invalid. Uplink preserved the installation.", ex);
        }
    }

    private static async Task<Version> VerifyBundledKeelAsync(HttpClient http, string root, string staging,
        IReadOnlyDictionary<string, string> actual, CancellationToken token)
    {
        var version = ReadBundledKeelVersion(root);
        var offer = await KeelUpdater.GetExactAsync(http, version, token).ConfigureAwait(false);
        var files = await KeelUpdater.DownloadPayloadAsync(http, offer, staging, token).ConfigureAwait(false);
        for (int i = 0; i < BundledKeelPaths.Length; i++)
            if (actual[BundledKeelPaths[i]] != Digest(files[i]))
                throw new PlatformException($"The changed Keel files do not match the published Keel {version} release. Uplink preserved the installation.");
        return version;
    }
}
