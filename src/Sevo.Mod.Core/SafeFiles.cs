using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace Sevo.Mod.Core;

internal static class SafeFiles
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive=true, WriteIndented=true, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    internal static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.StartsWith('/') || value.Length>220) throw new PlatformException("Unsafe relative path: " + value);
        foreach(var part in value.TrimEnd('/').Split('/'))
        {
            if(part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c=>c<32 || "<>:\"|?*".Contains(c)) || Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$",RegexOptions.IgnoreCase))
                throw new PlatformException("Unsafe path component: " + part);
        }
        return value.TrimEnd('/');
    }
    internal static string Id(string id)
    {
        if(id==null || !Regex.IsMatch(id,"^[a-z0-9][a-z0-9._-]{0,99}$") || id.Contains("..")) throw new PlatformException("Package ID must contain lowercase letters, numbers, dots, hyphens or underscores.");
        Relative(id); return id;
    }
    internal static void Ancestors(string path)
    {
        var current=Path.GetFullPath(path);
        while(!string.IsNullOrEmpty(current))
        {
            if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new PlatformException("Symbolic links and reparse points are not allowed: " + current);
            current=Path.GetDirectoryName(current)!;
        }
    }
    internal static string Under(string root,string relative)
    {
        Relative(relative); var full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new PlatformException("Path escapes its owned directory.");
        Ancestors(full); return full;
    }
    internal static void Create(string path) { Ancestors(path); Directory.CreateDirectory(path); }
    internal static T Read<T>(string file)
    {
        Ancestors(file);
        try
        {
            if(new FileInfo(file).Length>16*1024*1024) throw new PlatformException("Metadata is too large: " + file);
            var bytes=File.ReadAllBytes(file); using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=32 }); CheckJson(doc.RootElement);
            return JsonSerializer.Deserialize<T>(bytes,Json) ?? throw new PlatformException("Empty JSON metadata: " + file);
        }
        catch(JsonException ex) { throw new PlatformException("Malformed JSON metadata: " + file,ex); }
    }
    internal static T Parse<T>(Stream stream)
    {
        using var reader=new StreamReader(stream); var value=reader.ReadToEnd();
        try { using var doc=JsonDocument.Parse(value,new JsonDocumentOptions { MaxDepth=32 }); CheckJson(doc.RootElement); return JsonSerializer.Deserialize<T>(value,Json) ?? throw new PlatformException("Empty manifest."); }
        catch(JsonException ex) { throw new PlatformException("Malformed package manifest.",ex); }
    }
    static void CheckJson(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object) { var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach(var p in e.EnumerateObject()) { if(!keys.Add(p.Name)) throw new PlatformException("Duplicate JSON property: " + p.Name); CheckJson(p.Value); } }
        else if(e.ValueKind==JsonValueKind.Array) foreach(var v in e.EnumerateArray()) CheckJson(v);
    }
    internal static void Write<T>(string file,T value)
    {
        Ancestors(file); Create(Path.GetDirectoryName(file)!); var temp=file+".tmp-"+Guid.NewGuid().ToString("N");
        try { using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { JsonSerializer.Serialize(stream,value,Json); stream.Flush(true); } File.Move(temp,file,true); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    internal static string Hash(string file) { Ancestors(file); using var s=File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
    internal static Dictionary<string,string> Inventory(string root)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); Ancestors(root); if(!Directory.Exists(root)) return result;
        void Walk(string dir)
        {
            foreach(var p in Directory.EnumerateFileSystemEntries(dir))
            {
                Ancestors(p); var rel=Path.GetRelativePath(root,p).Replace('\\','/'); Relative(rel);
                if(Directory.Exists(p)) Walk(p); else if(!result.TryAdd(rel,Hash(p))) throw new PlatformException("Duplicate file alias: " + rel);
            }
        }
        Walk(root); return result;
    }
    internal static bool Equal(Dictionary<string,string> a,Dictionary<string,string> b) => a.Count==b.Count && a.All(kv=>b.TryGetValue(kv.Key,out var v)&&v==kv.Value);
    internal static void DeleteTree(string path) { Ancestors(path); if(Directory.Exists(path)) { Inventory(path); Directory.Delete(path,true); } }
}

internal readonly record struct PackageVersion(int Major,int Minor,int Patch,string Suffix) : IComparable<PackageVersion>
{
    internal static PackageVersion Parse(string value)
    {
        var m=Regex.Match(value??"",@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$");
        if(!m.Success || !int.TryParse(m.Groups[1].Value,out var major)||!int.TryParse(m.Groups[2].Value,out var minor)||!int.TryParse(m.Groups[3].Value,out var patch)) throw new PlatformException("Expected semantic version (for example 1.2.3): " + value);
        return new(major,minor,patch,m.Groups[4].Value);
    }
    public int CompareTo(PackageVersion other)
    {
        var c=Major.CompareTo(other.Major); if(c!=0)return c; c=Minor.CompareTo(other.Minor); if(c!=0)return c; c=Patch.CompareTo(other.Patch); if(c!=0)return c;
        if(Suffix==other.Suffix)return 0; if(Suffix=="")return 1; if(other.Suffix=="")return -1;
        var a=Suffix.Split('.'); var b=other.Suffix.Split('.');
        for(int i=0;i<Math.Min(a.Length,b.Length);i++) { bool ai=int.TryParse(a[i],out var av),bi=int.TryParse(b[i],out var bv); c=ai&&bi?av.CompareTo(bv):ai?-1:bi?1:string.CompareOrdinal(a[i],b[i]); if(c!=0)return c; }
        return a.Length.CompareTo(b.Length);
    }
    internal static bool Satisfies(string version,string requirement)
    {
        if(requirement=="*")return true;
        var m=Regex.Match(requirement??"",@"^(>=|<=|>|<|=|\^|~)?\s*(\S+)$"); if(!m.Success)throw new PlatformException("Unsupported dependency requirement: "+requirement);
        var actual=Parse(version); var target=Parse(m.Groups[2].Value); var c=actual.CompareTo(target);
        return m.Groups[1].Value switch { ">="=>c>=0,"<="=>c<=0,">"=>c>0,"<"=>c<0,"^"=>c>=0&&(target.Major>0?actual.Major==target.Major:target.Minor>0?actual.Major==0&&actual.Minor==target.Minor:actual.Major==0&&actual.Minor==0&&actual.Patch==target.Patch),"~"=>c>=0&&actual.Major==target.Major&&actual.Minor==target.Minor,_=>c==0 };
    }
}
