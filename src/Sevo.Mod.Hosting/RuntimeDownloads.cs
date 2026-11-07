using System.Security.Cryptography;

namespace Sevo.Mod.Hosting;

public static class RuntimeDownloads
{
    public const string LoaderFileName = "BepInEx_win_x64_5.4.23.5.zip";
    public const string LoaderSha256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4";
    public static async Task<string> EnsureLoaderAsync(HttpClient client, string directory, string? bundledFile = null, CancellationToken cancellationToken = default)
    {
        SafePath.Check(Path.GetFullPath(directory)); Directory.CreateDirectory(directory);
        var target = SafePath.Child(directory, LoaderFileName);
        if (File.Exists(target)) { Verify(target); return target; }
        var temp = SafePath.Child(directory, LoaderFileName + ".download-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (bundledFile != null && File.Exists(bundledFile))
            {
                SafePath.Check(Path.GetFullPath(bundledFile)); Verify(bundledFile); File.Copy(bundledFile, temp, false);
            }
            else
            {
                using var response = await client.GetAsync("https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/" + LoaderFileName, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new InvalidOperationException("Loader download exceeds size limit.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920]; int count; long total = 0;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if ((total += count) > 16 * 1024 * 1024) throw new InvalidOperationException("Loader download exceeds size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            Verify(temp); File.Move(temp, target); return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Verify(string path)
    {
        using var source = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(source)).Equals(LoaderSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("BepInEx archive checksum does not match the verified upstream release. The file was preserved and was not installed.");
    }
}
