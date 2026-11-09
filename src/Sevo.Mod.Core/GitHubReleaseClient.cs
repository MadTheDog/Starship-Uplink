using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
namespace Sevo.Mod.Core;
public sealed class ReleaseInfo
{
    public string TagName { get; set; }="";
    public string PageUrl { get; set; }="";
    public bool Prerelease { get; set; }
    public List<ReleaseAsset> Assets { get; set; }=[];
}
public sealed class ReleaseAsset
{
    public string Name { get; set; }="";
    public string DownloadUrl { get; set; }="";
    public string? Sha256 { get; set; }
}
public sealed class GitHubReleaseClient
{
    readonly HttpClient client;
    public GitHubReleaseClient(HttpClient client) { this.client=client??throw new ArgumentNullException(nameof(client)); }
    internal static void ValidateRepository(string repository) { if(string.IsNullOrEmpty(repository)||!Regex.IsMatch(repository,@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$")||repository.EndsWith(".git",StringComparison.OrdinalIgnoreCase)||repository.Split('/')[1] is "." or "..")throw new PlatformException("Use a public GitHub repository ID in owner/repo form."); }
    static Uri Official(string value,bool redirected=false)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo)||!uri.IsDefaultPort||uri.Fragment!="")throw new PlatformException("Only official HTTPS GitHub release asset URLs are accepted.");
        bool valid=uri.Host=="github.com"&&Regex.IsMatch(uri.AbsolutePath,@"^/[^/]+/[^/]+/releases/download/[^/]+/[^/]+$") || uri.Host=="api.github.com"&&Regex.IsMatch(uri.AbsolutePath,@"^/repos/[^/]+/[^/]+/releases/assets/\d+$");
        if(redirected&&(uri.Host=="release-assets.githubusercontent.com"||uri.Host=="objects.githubusercontent.com"))valid=true;
        if(!valid)throw new PlatformException("Only official GitHub release assets are accepted."); return uri;
    }
    static HttpRequestMessage Request(Uri uri,string accept)
    {
        var request=new HttpRequestMessage(HttpMethod.Get,uri); request.Headers.UserAgent.ParseAdd("Sevo-Mod-Platform/0.1"); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept)); request.Headers.Add("X-GitHub-Api-Version","2022-11-28"); return request;
    }
    public Task<ReleaseInfo> GetLatestAsync(string repository,CancellationToken cancellationToken=default)
        => ReadReleasesAsync(repository,"latest",r =>
        {
            var release=ParseRelease(repository,r);
            if(release.Prerelease)throw new PlatformException("GitHub returned a prerelease from the stable release endpoint.");
            return release;
        },cancellationToken);

    internal static void ValidateTag(string tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > 200 ||
            tag.Any(char.IsControl) || tag.Split('/').Any(part => part is "" or "." or ".."))
            throw new PlatformException("Invalid GitHub release tag.");
    }

    internal Task<ReleaseInfo> GetByTagAsync(string repository, string tag, CancellationToken token = default)
    {
        ValidateTag(tag);
        return ReadReleasesAsync(repository, "tags/" + Uri.EscapeDataString(tag), json =>
        {
            var release = ParseRelease(repository, json);
            if (json.GetProperty("draft").GetBoolean() || release.TagName != tag)
                throw new PlatformException("GitHub did not return the exact published release.");
            return release;
        }, token);
    }

    // Keel publishes preview releases with ordinary vX.Y.Z tags. Other consumers keep the stable-only endpoint.
    internal async Task<IReadOnlyList<ReleaseInfo>> GetPublishedAsync(string repository,CancellationToken cancellationToken=default)
    {
        const int pageSize=30;
        var releases=new List<ReleaseInfo>();
        for(int page=1; ; page++)
        {
            var result=await ReadReleasesAsync(repository,$"?per_page={pageSize}&page={page}",json =>
                (Count:json.GetArrayLength(), Releases:json.EnumerateArray()
                    .Where(r=>!r.GetProperty("draft").GetBoolean()).Select(r=>ParseRelease(repository,r)).ToArray()),
                cancellationToken).ConfigureAwait(false);
            releases.AddRange(result.Releases);
            if(result.Count<pageSize)return releases;
        }
    }

    async Task<T> ReadReleasesAsync<T>(string repository,string endpoint,Func<JsonElement,T> parse,CancellationToken cancellationToken)
    {
        ValidateRepository(repository); cancellationToken.ThrowIfCancellationRequested();
        string suffix=endpoint.StartsWith('?') ? endpoint : "/"+endpoint;
        using var request=Request(new Uri("https://api.github.com/repos/"+repository+"/releases"+suffix),"application/vnd.github+json");
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            if(response.RequestMessage?.RequestUri?.Host!="api.github.com")throw new PlatformException("GitHub API redirected outside its official API host.");
            using var input=await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); using var bytes=new MemoryStream(); await CopyBounded(input,bytes,4*1024*1024,cancellationToken).ConfigureAwait(false); bytes.Position=0;
            using var json=await JsonDocument.ParseAsync(bytes,new JsonDocumentOptions { MaxDepth=32 },cancellationToken).ConfigureAwait(false);
            return parse(json.RootElement);
        }
        catch(HttpRequestException ex) { throw new PlatformException("GitHub release request failed: "+ex.Message,ex); }
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException) { throw new PlatformException("GitHub returned malformed release metadata.",ex); }
    }

    static ReleaseInfo ParseRelease(string repository,JsonElement r)
    {
        var release=new ReleaseInfo { TagName=r.GetProperty("tag_name").GetString()??throw new PlatformException("Release tag is missing."),PageUrl=r.GetProperty("html_url").GetString()??"",Prerelease=r.GetProperty("prerelease").GetBoolean() };
        if(!Uri.TryCreate(release.PageUrl,UriKind.Absolute,out var page)||page.Scheme!="https"||page.Host!="github.com"||!page.AbsolutePath.StartsWith("/"+repository+"/releases/",StringComparison.OrdinalIgnoreCase))throw new PlatformException("Release page URL does not match the requested repository.");
        foreach(var a in r.GetProperty("assets").EnumerateArray())
        {
            var name=a.GetProperty("name").GetString()??""; SafeFiles.Relative(name); if(name.Contains('/'))throw new PlatformException("Release asset has an unsafe filename.");
            var url=a.GetProperty("browser_download_url").GetString()??""; var uri=Official(url); if(!uri.AbsolutePath.StartsWith("/"+repository+"/releases/download/",StringComparison.OrdinalIgnoreCase))throw new PlatformException("Release asset belongs to a different repository.");
            string? sha=null; if(a.TryGetProperty("digest",out var d)&&d.ValueKind==JsonValueKind.String) { var digest=d.GetString()!; if(!Regex.IsMatch(digest,"^sha256:[0-9a-fA-F]{64}$"))throw new PlatformException("Unsupported release asset digest."); sha=digest[7..].ToLowerInvariant(); }
            release.Assets.Add(new ReleaseAsset { Name=name,DownloadUrl=url,Sha256=sha });
        }
        return release;
    }

    public async Task<string> DownloadAsync(ReleaseAsset asset,string destinationDirectory,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var uri=Official(asset.DownloadUrl); SafeFiles.Relative(asset.Name); if(asset.Name.Contains('/'))throw new PlatformException("Unsafe release asset filename.");
        if(asset.Sha256!=null&&!Regex.IsMatch(asset.Sha256,"^[a-fA-F0-9]{64}$"))throw new PlatformException("Invalid SHA-256 digest.");
        var directory=Path.GetFullPath(destinationDirectory); SafeFiles.Create(directory); var destination=SafeFiles.Under(directory,asset.Name); var temp=SafeFiles.Under(directory,".download-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var request=Request(uri,"application/octet-stream"); using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            Official(response.RequestMessage?.RequestUri?.ToString()??asset.DownloadUrl,true);
            if(response.Content.Headers.ContentLength>512L*1024*1024)throw new PlatformException("Release asset exceeds 512 MiB.");
            using(var input=await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { await CopyBounded(input,output,512L*1024*1024,cancellationToken).ConfigureAwait(false); output.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested(); var digest=SafeFiles.Hash(temp);
            if(asset.Sha256!=null&&!digest.Equals(asset.Sha256,StringComparison.OrdinalIgnoreCase))throw new PlatformException("Release asset SHA-256 verification failed.");
            if(File.Exists(destination)) { if(SafeFiles.Hash(destination)!=digest)throw new PlatformException("An existing download has different bytes; choose another destination."); return destination; }
            SafeFiles.Ancestors(destination); File.Move(temp,destination); return destination;
        }
        catch(HttpRequestException ex) { throw new PlatformException("GitHub asset download failed: "+ex.Message,ex); }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    static async Task CopyBounded(Stream input,Stream output,long limit,CancellationToken token)
    { var buffer=new byte[81920]; long total=0; int read; while((read=await input.ReadAsync(buffer,token).ConfigureAwait(false))>0) { total+=read; if(total>limit)throw new PlatformException("Download exceeds its allowed size."); await output.WriteAsync(buffer.AsMemory(0,read),token).ConfigureAwait(false); } }
}
