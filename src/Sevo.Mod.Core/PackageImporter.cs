using System.IO.Compression;
using System.Text.RegularExpressions;
namespace Sevo.Mod.Core;

internal static class PackageImporter
{
    static readonly HashSet<string> PayloadExtensions=new(StringComparer.OrdinalIgnoreCase) { ".dll",".json",".bundle",".assets",".txt",".xml",".pdb",".config",".png",".jpg",".shader",".dat",".bin",".bytes",".mat",".wav",".ogg" };
    internal static PackageRecord Import(string archivePath,PackageImportOptions? options,string store,Catalog catalog)
    {
        SafeFiles.Ancestors(archivePath);
        if(!File.Exists(archivePath)||new FileInfo(archivePath).Length>512L*1024*1024) throw new PlatformException("Package archive is missing or exceeds 512 MiB.");
        var hash=SafeFiles.Hash(archivePath);
        using var zip=Open(archivePath);
        if(zip.Entries.Count>10000) throw new PlatformException("Package has too many archive entries.");
        var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); var spellings=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); long total=0;
        foreach(var e in zip.Entries)
        {
            var path=SafeFiles.Relative(e.FullName); var unixType=(e.ExternalAttributes>>16)&0xF000;
            var prefix=""; foreach(var part in path.Split('/')) { prefix=prefix==""?part:prefix+"/"+part; if(spellings.TryGetValue(prefix,out var spelling)&&spelling!=prefix)throw new PlatformException("Archive contains directory/file case aliases: "+prefix); spellings[prefix]=prefix; }
            if(unixType is not 0 and not 0x8000 and not 0x4000 || (e.ExternalAttributes&0x400)!=0) throw new PlatformException("Archive links and special entries are forbidden.");
            if(!entries.TryAdd(path,e)) throw new PlatformException("Archive contains duplicate/case-aliased paths: "+path);
            total=checked(total+e.Length);
            if(total>512L*1024*1024 || (e.Length>1024*1024 && e.Length>Math.Max(1,e.CompressedLength)*200L)) throw new PlatformException("Archive expansion limit exceeded.");
        }
        // A file may not also be an ancestor of another entry.
        foreach(var key in entries.Keys) { var parent=key; while(parent.Contains('/')) { parent=parent[..parent.LastIndexOf('/')]; if(entries.TryGetValue(parent,out var e)&&!e.FullName.EndsWith('/')) throw new PlatformException("Archive file/directory alias: "+parent); } }
        Manifest m;
        if(entries.TryGetValue("sevo.mod.json",out var manifestEntry))
        {
            if(manifestEntry.Length>1024*1024) throw new PlatformException("Package manifest exceeds 1 MiB.");
            using var stream=manifestEntry.Open(); m=SafeFiles.Parse<Manifest>(stream);
            if(m.SchemaVersion!=1) throw new PlatformException("Unsupported package manifest schema.");
            if(string.IsNullOrWhiteSpace(m.PayloadRoot))throw new PlatformException("Manifest payloadRoot is required.");
            m.PayloadRoot=SafeFiles.Relative(m.PayloadRoot.TrimEnd('/'));
            if(m.PayloadRoot.Contains('/')) throw new PlatformException("Manifest payloadRoot must be one archive-root directory.");
            if(options!=null && ((options.Id!=null&&options.Id!=m.Id)||(options.Version!=null&&options.Version!=m.Version))) throw new PlatformException("Explicit identity conflicts with the package manifest.");
            if(options?.Repository!=null)
            {
                if(m.Repository!=null&&!string.Equals(m.Repository,options.Repository,StringComparison.OrdinalIgnoreCase))throw new PlatformException("Explicit repository conflicts with the package manifest.");
                m.Repository=options.Repository;
            }
            if(options?.AssetName!=null)
            {
                if(m.AssetName!=null&&m.AssetName!=options.AssetName)throw new PlatformException("Explicit asset name conflicts with the package manifest.");
                m.AssetName=options.AssetName;
            }
        }
        else
        {
            var recognized=new[] { ("GraphicalTweaks","local.sevo.graphicaltweaks"),("HullFiller","local.sevo.hullfiller"),("InfiniteRotator","local.sevo.infiniterotator") }
                .Where(x=>entries.Keys.Any(k=>k.StartsWith("BepInEx/plugins/"+x.Item1+"/",StringComparison.OrdinalIgnoreCase))).ToArray();
            if(recognized.Length!=1) throw new PlatformException("Archive must contain a native manifest or exactly one recognized legacy plugin directory.");
            var versions=Regex.Matches(Path.GetFileNameWithoutExtension(archivePath),@"(?<![\d.])\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?(?![\d.])").Select(x=>x.Value).Distinct().ToArray();
            if(options?.Version==null&&versions.Length!=1) throw new PlatformException("Legacy archive version is ambiguous; supply explicit import metadata.");
            m=new Manifest { SchemaVersion=1, Id=options?.Id??recognized[0].Item2,Name=options?.Name??recognized[0].Item1,Version=options?.Version??versions[0],PayloadRoot="BepInEx/plugins/"+recognized[0].Item1,Repository=options?.Repository,AssetName=options?.AssetName??Path.GetFileName(archivePath) };
        }
        SafeFiles.Id(m.Id); PackageVersion.Parse(m.Version);
        if ((options?.ExpectedId is { } expectedId && m.Id != expectedId) ||
            (options?.ExpectedVersion is { } expectedVersion && m.Version != expectedVersion))
            throw new PlatformException($"The selected ZIP contains {m.Id} {m.Version}, which does not match the requested mod.");
        if(string.IsNullOrWhiteSpace(m.Name)||m.Name.Length>200)throw new PlatformException("Package name is required and must be at most 200 characters.");
        if(m.Repository!=null) GitHubReleaseClient.ValidateRepository(m.Repository);
        if(m.AssetName!=null) { SafeFiles.Relative(m.AssetName); if(m.AssetName.Contains('/'))throw new PlatformException("Invalid asset filename."); }
        if(m.Dependencies==null||m.Conflicts==null||m.FixedBrickIds==null||m.FixedShapeIds==null)throw new PlatformException("Manifest collections must not be null.");
        foreach(var dep in m.Dependencies) { SafeFiles.Id(dep.Key); PackageVersion.Satisfies("0.0.0",dep.Value); }
        foreach(var id in m.Conflicts)SafeFiles.Id(id);
        if(m.FixedBrickIds.Concat(m.FixedShapeIds).Any(x=>x<0)||m.FixedBrickIds.Distinct().Count()!=m.FixedBrickIds.Count||m.FixedShapeIds.Distinct().Count()!=m.FixedShapeIds.Count)throw new PlatformException("Fixed IDs must be nonnegative and unique within their namespace.");
        var selected=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long documentationBytes=0; int documentationCount=0;
        foreach(var (path,e) in entries)
        {
            if(e.FullName.EndsWith('/'))continue;
            if(path.StartsWith(m.PayloadRoot+"/",StringComparison.OrdinalIgnoreCase))
            {
                var relative=path[(m.PayloadRoot.Length+1)..];
                if(!PayloadExtensions.Contains(Path.GetExtension(relative))) throw new PlatformException("Unsupported or executable payload file: "+path);
                selected.Add(relative,e);
            }
            else if(!path.Equals("sevo.mod.json",StringComparison.OrdinalIgnoreCase))
            {
                if(!IsDocumentation(path))throw new PlatformException("Archive contains files outside its validated payload: "+path);
                documentationBytes+=e.Length; documentationCount++;
                if(e.Length>2*1024*1024||documentationBytes>32L*1024*1024||documentationCount>1024)throw new PlatformException("Archive documentation exceeds allowed size/count limits.");
            }
        }
        if(!selected.Keys.Any(k=>k.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)))throw new PlatformException("Plugin package contains no DLL.");
        var existing=catalog.Packages.SingleOrDefault(p=>p.Id==m.Id&&p.Version==m.Version);
        if(existing!=null)
        {
            if(existing.Sha256!=hash)throw new PlatformException("This package ID/version is already cached with different bytes.");
            if(options?.Repository!=null&&existing.Repository!=null&&!string.Equals(existing.Repository,options.Repository,StringComparison.OrdinalIgnoreCase))throw new PlatformException("This cached package already has a different repository; its source cannot be silently replaced.");
            if(options?.AssetName!=null&&existing.AssetName!=null&&existing.AssetName!=options.AssetName)throw new PlatformException("This cached package already has a different release asset name.");
            if(existing.Repository==null&&options?.Repository!=null)existing.Repository=options.Repository;
            if(existing.AssetName==null&&options?.AssetName!=null)existing.AssetName=options.AssetName;
            return existing;
        }
        var directory=SafeFiles.Under(store,hash); var stage=SafeFiles.Under(store,"import-"+Guid.NewGuid().ToString("N")); SafeFiles.Create(stage);
        try
        {
            var payload=Path.Combine(stage,"payload"); SafeFiles.Create(payload);
            foreach(var (relative,e) in selected)
            {
                var dest=SafeFiles.Under(payload,relative); SafeFiles.Create(Path.GetDirectoryName(dest)!);
                using var input=e.Open(); using var output=new FileStream(dest,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                input.CopyTo(output); if(output.Length!=e.Length)throw new PlatformException("Archive payload size mismatch."); output.Flush(true);
            }
            File.Copy(archivePath,Path.Combine(stage,"archive.zip"));
            if(SafeFiles.Hash(Path.Combine(stage,"archive.zip"))!=hash)throw new PlatformException("Archive changed during import.");
            var record=new PackageRecord { Id=m.Id,Name=m.Name,Version=m.Version,Sha256=hash,Repository=m.Repository,AssetName=m.AssetName,PayloadDirectory=Path.Combine(directory,"payload"),Dependencies=m.Dependencies,Conflicts=m.Conflicts,FixedBrickIds=m.FixedBrickIds,FixedShapeIds=m.FixedShapeIds,Files=SafeFiles.Inventory(payload) };
            SafeFiles.Write(Path.Combine(stage,"metadata.json"),record);
            if(Directory.Exists(directory))
            {
                // A previously committed content object can be shared only if every byte matches.
                if(!SafeFiles.Equal(SafeFiles.Inventory(directory),SafeFiles.Inventory(stage)))throw new PlatformException("Existing content-addressed object differs from imported content.");
            }
            else Directory.Move(stage,directory);
            catalog.Packages.Add(record); return record;
        }
        finally { SafeFiles.DeleteTree(stage); }
    }
    static ZipArchive Open(string path) { try { return ZipFile.OpenRead(path); } catch(InvalidDataException ex) { throw new PlatformException("Not a valid ZIP package.",ex); } }
    static bool IsDocumentation(string path)
    {
        var parts=path.Split('/'); if(parts.Length>8)return false;
        var extension=Path.GetExtension(path);
        bool text=extension.Equals(".md",StringComparison.OrdinalIgnoreCase)||extension.Equals(".txt",StringComparison.OrdinalIgnoreCase)||extension.Equals(".rst",StringComparison.OrdinalIgnoreCase)||extension.Equals(".markdown",StringComparison.OrdinalIgnoreCase);
        if(parts.Length==1)return text||Regex.IsMatch(path,@"^(README|LICENSE|LICENCE|COPYING|NOTICE|AUTHORS|CHANGELOG)(?:-[A-Za-z0-9-]+)?$",RegexOptions.IgnoreCase);
        return text&&(parts[0].Equals("docs",StringComparison.OrdinalIgnoreCase)||parts[0].Equals("doc",StringComparison.OrdinalIgnoreCase)||parts[0].Equals("documentation",StringComparison.OrdinalIgnoreCase)||parts[0].Equals("licenses",StringComparison.OrdinalIgnoreCase));
    }
}
