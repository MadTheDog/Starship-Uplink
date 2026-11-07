namespace Sevo.Mod.Hosting;

internal static class SafePath
{
    public static string Relative(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.Contains(':')) throw new InvalidOperationException("Unsafe archive/relative path.");
        foreach (var part in normalized.Split('/'))
        {
            var stem = part.Split('.')[0];
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new InvalidOperationException("Unsafe archive/relative path component.");
        }
        return normalized;
    }
    public static string Child(string parent, string relative)
    {
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(prefix, Relative(relative)));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path escaped its selected root.");
        Check(full); return full;
    }
    public static void Check(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Links/junctions are refused in managed runtime paths: " + current);
    }
}
