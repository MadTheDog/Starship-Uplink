using System.IO.Compression;
using Sevo.Mod.Hosting;
using Sevo.Mod.Core;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../local-evidence/hosting-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var tests = new List<(string Name, Action Run)>();
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected safe refusal"); }
(string Game,string State,string Zip,string Framework) Fixture(string name)
{
    var target = Path.Combine(root, name); Directory.CreateDirectory(target);
    var game = Path.Combine(target, "game"); Directory.CreateDirectory(Path.Combine(game,"Starship EVO_Data","Managed"));
    File.WriteAllText(Path.Combine(game,"Starship EVO.exe"),"fixture executable, never executed");
    File.WriteAllText(Path.Combine(game,"Starship EVO_Data","Managed","Assembly-CSharp.dll"),"fixture");
    var framework = Path.Combine(target,"framework"); Directory.CreateDirectory(framework);
    File.WriteAllText(Path.Combine(framework,"Sevo.Framework.Runtime.dll"),"runtime fixture");
    File.WriteAllText(Path.Combine(framework,"Sevo.Framework.Sdk.dll"),"sdk fixture");
    var zip = Path.Combine(target,"loader.zip");
    using(var z=ZipFile.Open(zip,ZipArchiveMode.Create))
        foreach(var (path,value) in new[]{("winhttp.dll","proxy"),("doorstop_config.ini","[General]\nenabled=true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll"),("BepInEx/core/BepInEx.Preloader.dll","preloader"),("BepInEx/core/BepInEx.dll","core")})
            using(var writer=new StreamWriter(z.CreateEntry(path).Open())) writer.Write(value);
    return(game,Path.Combine(target,"runtime-state"),zip,framework);
}
tests.Add(("prepare provisions complete runtime and preserves unrelated files",()=>{
    var f=Fixture("normal"); File.WriteAllText(Path.Combine(f.Game,"keep.txt"),"keep");
    var provisioner=new RuntimeProvisioner(root); var result=provisioner.Prepare(f.Game,f.State,f.Zip,f.Framework);
    Check(result.FilesInstalled>=6,"runtime payload missing");
    Check(File.Exists(Path.Combine(f.Game,"BepInEx","plugins","SevoPlatform","Sevo.Framework.Runtime.dll")),"framework missing");
    Check(File.ReadAllText(Path.Combine(f.Game,"BepInEx","config","BepInEx.cfg")).Contains("HideManagerGameObject = true"),"required loader configuration missing");
    Check(File.Exists(Path.Combine(f.State,"sevo-runtime-profile.json")),"profile marker missing");
    provisioner.Prepare(f.Game,f.State,f.Zip,f.Framework);
    provisioner.Remove(f.Game,f.State);
    Check(!File.Exists(Path.Combine(f.Game,"winhttp.dll")),"owned proxy remains");
    Check(File.ReadAllText(Path.Combine(f.Game,"keep.txt"))=="keep","unrelated file changed");
}));
tests.Add(("unmanaged existing bootstrap is never overwritten",()=>{
    var f=Fixture("unowned"); File.WriteAllText(Path.Combine(f.Game,"winhttp.dll"),"someone else's runtime");
    Throws(()=>new RuntimeProvisioner(root).Prepare(f.Game,f.State,f.Zip,f.Framework));
    Check(File.ReadAllText(Path.Combine(f.Game,"winhttp.dll"))=="someone else's runtime","unmanaged file overwritten");
}));
foreach (bool manualKeel in new[] { false, true })
    tests.Add(($"verified existing BepInEx stays external with manual Keel={manualKeel}", () =>
    {
        var f = Fixture("reuse-" + manualKeel);
        ZipFile.ExtractToDirectory(f.Zip, f.Game);
        var plugins = Path.Combine(f.Game, "BepInEx/plugins/SevoPlatform");
        var runtime = Path.Combine(plugins, "Sevo.Framework.Runtime.dll");
        var sdk = Path.Combine(plugins, "Sevo.Framework.Sdk.dll");
        if (manualKeel)
        {
            Directory.CreateDirectory(plugins);
            File.Copy(Path.Combine(f.Framework, "Sevo.Framework.Runtime.dll"), runtime);
            File.Copy(Path.Combine(f.Framework, "Sevo.Framework.Sdk.dll"), sdk);
        }
        var bootstrap = Path.Combine(f.Game, "winhttp.dll");
        var bootstrapTime = File.GetLastWriteTimeUtc(bootstrap);
        var p = new RuntimeProvisioner(root);
        Check(p.Prepare(f.Game, f.State, f.Zip, f.Framework).ReusedLoader, "existing loader was not reused");
        p.ValidatePrepared(f.Game, f.State);
        Check(p.Prepare(f.Game, f.State, f.Zip, f.Framework).ReusedLoader, "repeat prepare did not preserve reuse");
        Check(File.GetLastWriteTimeUtc(bootstrap) == bootstrapTime, "bootstrap was rewritten");
        var owner = Path.Combine(f.Game, ".sevo-runtime-owned.json");
        var ownership = System.Text.Json.JsonSerializer.Deserialize<RuntimeProvisioner.RuntimeManifest>(File.ReadAllText(owner))!;
        Check(ownership.SchemaVersion == 2 && ownership.ExternalFiles!.Contains("winhttp.dll"), "old Uplink must refuse external ownership records");
        Check(File.Exists(runtime) && File.Exists(sdk), "Keel payload was not prepared");
        File.WriteAllText(bootstrap, "user changed their own loader");
        Throws(() => p.ValidatePrepared(f.Game, f.State));
        p.Remove(f.Game, f.State);
        Check(File.ReadAllText(bootstrap) == "user changed their own loader", "removal touched external bootstrap");
        Check(File.Exists(runtime) == manualKeel && File.Exists(sdk) == manualKeel, "removal did not honor Keel ownership");
    }));
tests.Add(("partial matching loader still refuses preparation", () =>
{
    var f = Fixture("partial-external");
    var bootstrap = Path.Combine(f.Game, "winhttp.dll");
    File.WriteAllText(bootstrap, "proxy");
    Throws(() => new RuntimeProvisioner(root).Prepare(f.Game, f.State, f.Zip, f.Framework));
    Check(File.ReadAllText(bootstrap) == "proxy", "partial loader was modified");
    Check(!File.Exists(Path.Combine(f.Game, "doorstop_config.ini")), "partial loader was filled in");
    Check(!File.Exists(Path.Combine(f.State, "sevo-runtime-profile.json")), "partial loader was admitted");
}));
tests.Add(("incomplete external preparation rolls back owned files only", () =>
{
    var f = Fixture("external-recovery");
    ZipFile.ExtractToDirectory(f.Zip, f.Game);
    var p = new RuntimeProvisioner(root);
    p.Prepare(f.Game, f.State, f.Zip, f.Framework);
    var owner = Path.Combine(f.Game, ".sevo-runtime-owned.json");
    var pending = Path.Combine(f.Game, ".sevo-runtime-pending.json");
    File.Move(owner, pending);
    var bootstrap = Path.Combine(f.Game, "winhttp.dll");
    File.WriteAllText(bootstrap, "external edit");
    Throws(() => p.Prepare(f.Game, f.State, f.Zip, f.Framework));
    Check(!File.Exists(pending) && !File.Exists(owner), "incomplete ownership was not recovered");
    Check(!File.Exists(Path.Combine(f.Game, "BepInEx/plugins/SevoPlatform/Sevo.Framework.Runtime.dll")), "owned partial payload remains");
    Check(File.ReadAllText(bootstrap) == "external edit", "recovery touched external bootstrap");
}));
tests.Add(("external schema with missing inventory refuses removal", () =>
{
    var f = Fixture("external-missing-inventory");
    ZipFile.ExtractToDirectory(f.Zip, f.Game);
    var p = new RuntimeProvisioner(root);
    p.Prepare(f.Game, f.State, f.Zip, f.Framework);
    var owner = Path.Combine(f.Game, ".sevo-runtime-owned.json");
    var manifest = System.Text.Json.JsonSerializer.Deserialize<RuntimeProvisioner.RuntimeManifest>(File.ReadAllText(owner))!;
    manifest.ExternalFiles = null;
    File.WriteAllText(owner, System.Text.Json.JsonSerializer.Serialize(manifest));
    Throws(() => p.Remove(f.Game, f.State));
    Check(File.Exists(Path.Combine(f.Game, "winhttp.dll")), "malformed record removed external bootstrap");
}));
tests.Add(("externally modified runtime blocks removal",()=>{
    var f=Fixture("edited"); var p=new RuntimeProvisioner(root);p.Prepare(f.Game,f.State,f.Zip,f.Framework);
    File.WriteAllText(Path.Combine(f.Game,"winhttp.dll"),"user changed"); Throws(()=>p.Remove(f.Game,f.State));
    Check(File.ReadAllText(Path.Combine(f.Game,"winhttp.dll"))=="user changed","user change removed");
}));
tests.Add(("runtime archive traversal rejected before any deployment",()=>{
    var f=Fixture("traversal"); using(var z=ZipFile.Open(f.Zip,ZipArchiveMode.Update)){using var w=new StreamWriter(z.CreateEntry("../escape.dll").Open());w.Write("bad");}
    Throws(()=>new RuntimeProvisioner(root).Prepare(f.Game,f.State,f.Zip,f.Framework));
    Check(!File.Exists(Path.Combine(f.Game,"winhttp.dll")),"partial runtime deployed");
}));
tests.Add(("state directory cannot escape portable workspace",()=>{
    var f=Fixture("outside");Throws(()=>new RuntimeProvisioner(root).Prepare(f.Game,Path.Combine(root,"..","outside"),f.Zip,f.Framework));
}));
tests.Add(("malformed runtime ownership fails with an actionable refusal",()=>{
    var f=Fixture("malformed-owner");var p=new RuntimeProvisioner(root);p.Prepare(f.Game,f.State,f.Zip,f.Framework);
    File.WriteAllText(Path.Combine(f.Game,".sevo-runtime-owned.json"),"{\"SchemaVersion\":1,\"Files\":[null]}");
    Throws(()=>p.ValidatePrepared(f.Game,f.State));Check(File.Exists(Path.Combine(f.Game,"winhttp.dll")),"malformed ownership changed runtime");
}));
tests.Add(("runtime ownership requires an explicit schema version",()=>{
    var f=Fixture("owner-schema");var p=new RuntimeProvisioner(root);p.Prepare(f.Game,f.State,f.Zip,f.Framework);
    var path=Path.Combine(f.Game,".sevo-runtime-owned.json");var json=File.ReadAllText(path).Replace("\"SchemaVersion\": 1,","");File.WriteAllText(path,json);
    Throws(()=>p.ValidatePrepared(f.Game,f.State));
}));
tests.Add(("missing managed runtime file cannot be reported prepared",()=>{
    var f=Fixture("missing"); var p=new RuntimeProvisioner(root);p.Prepare(f.Game,f.State,f.Zip,f.Framework);
    File.Delete(Path.Combine(f.Game,"winhttp.dll")); Throws(()=>p.ValidatePrepared(f.Game,f.State));
}));
tests.Add(("interrupted initial provision rolls back unchanged partial files",()=>{
    var f=Fixture("recovery");
    File.WriteAllText(Path.Combine(f.Game,"winhttp.dll"),"proxy");
    var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("proxy"))).ToLowerInvariant();
    File.WriteAllText(Path.Combine(f.Game,".sevo-runtime-pending.json"),System.Text.Json.JsonSerializer.Serialize(new RuntimeProvisioner.RuntimeManifest {Files=[new(){Path="winhttp.dll",Sha256=hash}]}));
    var p=new RuntimeProvisioner(root);p.Prepare(f.Game,f.State,f.Zip,f.Framework);
    Check(!File.Exists(Path.Combine(f.Game,".sevo-runtime-pending.json")),"recovery record remains");
    p.ValidatePrepared(f.Game,f.State);
}));
tests.Add(("running game refuses validation before pending runtime recovery",()=>{
    var f=Fixture("running-recovery");
    var bootstrap=Path.Combine(f.Game,"winhttp.dll");File.WriteAllText(bootstrap,"proxy");
    var originalBootstrap=File.ReadAllBytes(bootstrap);
    var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalBootstrap)).ToLowerInvariant();
    var journal=Path.Combine(f.Game,".sevo-runtime-pending.json");
    File.WriteAllText(journal,System.Text.Json.JsonSerializer.Serialize(new RuntimeProvisioner.RuntimeManifest {Files=[new(){Path="winhttp.dll",Sha256=hash}]}));
    var originalJournal=File.ReadAllBytes(journal);var inspector=new RunningGameInspector(f.Game);
    Throws(()=>new RuntimeProvisioner(root,inspector).ValidatePrepared(f.Game,f.State));
    Check(inspector.Calls==1,"game state was not checked before recovery");
    Check(File.Exists(bootstrap)&&File.ReadAllBytes(bootstrap).SequenceEqual(originalBootstrap),"running game's partial bootstrap was changed by recovery");
    Check(File.Exists(journal)&&File.ReadAllBytes(journal).SequenceEqual(originalJournal),"running game's pending journal was changed by recovery");
    Check(!File.Exists(Path.Combine(f.State,"sevo-runtime-profile.json")),"running game validation wrote a profile marker");
}));
tests.Add(("recovery preserves externally changed partial runtime",()=>{
    var f=Fixture("recovery-edited");File.WriteAllText(Path.Combine(f.Game,"winhttp.dll"),"external edit");
    var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("proxy"))).ToLowerInvariant();
    File.WriteAllText(Path.Combine(f.Game,".sevo-runtime-pending.json"),System.Text.Json.JsonSerializer.Serialize(new RuntimeProvisioner.RuntimeManifest {Files=[new(){Path="winhttp.dll",Sha256=hash}]}));
    Throws(()=>new RuntimeProvisioner(root).Prepare(f.Game,f.State,f.Zip,f.Framework));
    Check(File.ReadAllText(Path.Combine(f.Game,"winhttp.dll"))=="external edit","recovery destroyed external edit");
}));
tests.Add(("runtime configuration requires the correct INI section",()=>{
    var f=Fixture("wrong-section");Directory.CreateDirectory(Path.Combine(f.Game,"BepInEx","config"));
    File.WriteAllText(Path.Combine(f.Game,"BepInEx","config","BepInEx.cfg"),"[WrongSection]\nHideManagerGameObject = true");
    Throws(()=>new RuntimeProvisioner(root).Prepare(f.Game,f.State,f.Zip,f.Framework));
    Check(!File.Exists(Path.Combine(f.Game,"winhttp.dll")),"wrong config allowed deployment");
}));
tests.Add(("launch ticket scopes state and distinguishes sessions",()=>{
    var f=Fixture("ticket"); var a=GameLaunch.Create(f.Game,f.State);var b=GameLaunch.Create(f.Game,f.State);
    Check(a.Session!=b.Session,"launch session reused");
    Check(a.StartInfo.Environment["SEVO_PLATFORM_SESSION"]==a.Session,"session not passed to runtime");
    Check(a.StartInfo.Environment["SEVO_PLATFORM_STATE_DIR"]==f.State,"state path not passed");
    Check(a.StartInfo.FileName==Path.Combine(f.Game,"Starship EVO.exe"),"wrong executable");
    Check(!a.StartInfo.UseShellExecute,"environment cannot be scoped through shell launch");
}));
tests.Add(("runtime status rejects stale nonce or process identity",()=>{
    var f=Fixture("status");Directory.CreateDirectory(f.State);
    File.WriteAllText(Path.Combine(f.State,"runtime-status.json"),"{\"schemaVersion\":1,\"session\":\"fresh\",\"processId\":123,\"resourcesReady\":true}");
    Check(GameLaunch.ReadSessionStatus(f.State,"stale",123)==null,"stale session accepted");
    Check(GameLaunch.ReadSessionStatus(f.State,"fresh",456)==null,"wrong process accepted");
    Check(GameLaunch.ReadSessionStatus(f.State,"fresh",123)!=null,"matching report rejected");
}));
tests.Add(("bundled loader integrity is checked before cache use",()=>{
    var f=Fixture("digest"); using var client=new HttpClient();
    Throws(()=>RuntimeDownloads.EnsureLoaderAsync(client,Path.Combine(root,"loader-cache"),f.Zip).GetAwaiter().GetResult());
}));
tests.Add(("same named GitHub assets update native packages without replacing prior downloads",()=>{
    byte[] Package(string version)
    {
        using var bytes=new MemoryStream();
        using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))
        {
            using(var writer=new StreamWriter(zip.CreateEntry("sevo.mod.json").Open()))
                writer.Write(System.Text.Json.JsonSerializer.Serialize(new {schemaVersion=1,id="stable-name",name="Stable name",version,payloadRoot="payload"}));
            using(var writer=new StreamWriter(zip.CreateEntry("payload/Mod.dll").Open())) writer.Write("fixture version "+version);
        }
        return bytes.ToArray();
    }
    var first=Package("1.0.0");var second=Package("2.0.0");
    const string url="https://github.com/example/mod/releases/download/";
    using var http=new HttpClient(new FixtureAssetsHandler(new Dictionary<string,byte[]> {[url+"v1/mod.zip"]=first,[url+"v2/mod.zip"]=second}));
    var client=new GitHubReleaseClient(http);var cache=Path.Combine(root,"stable-filename-downloads");
    var one=ReleaseDownloads.DownloadAsync(client,new(){Name="mod.zip",DownloadUrl=url+"v1/mod.zip"},cache).GetAwaiter().GetResult();
    var two=ReleaseDownloads.DownloadAsync(client,new(){Name="mod.zip",DownloadUrl=url+"v2/mod.zip"},cache).GetAwaiter().GetResult();
    Check(one!=two&&Path.GetFileName(one)=="mod.zip"&&Path.GetFileName(two)=="mod.zip","asset filename was changed or destinations collided");
    Check(File.ReadAllBytes(one).SequenceEqual(first)&&File.ReadAllBytes(two).SequenceEqual(second),"download bytes were replaced");
    var service=new PlatformService(Path.Combine(root,"stable-name-store"));
    var options=new PackageImportOptions {Repository="example/mod",AssetName="mod.zip"};
    var old=service.ImportPackage(one,options);var latest=service.ImportPackage(two,options);
    var f=Fixture("stable-name-install");var profile=service.CreateProfile("Stable filename updates",f.Game);
    service.Install(profile.Id,old.Id,old.Version);service.Install(profile.Id,latest.Id,latest.Version);
    Check(service.GetProfile(profile.Id).Mods.Single().Version=="2.0.0","same filename update failed");
    service.Rollback(profile.Id,old.Id);Check(service.GetProfile(profile.Id).Mods.Single().Version=="1.0.0","previous version was not retained");
    Check(File.ReadAllBytes(one).SequenceEqual(first),"rollback changed original download");

    var staging = Path.Combine(root, "github-import-staging");
    var update = ReleaseDownloads.ImportPackageAsync(client,
        new ReleaseAsset { Name = "mod.zip", DownloadUrl = url + "v2/mod.zip" },
        staging, service, "example/mod").GetAwaiter().GetResult();
    Check(!Directory.EnumerateFileSystemEntries(staging).Any(), "GitHub import kept a second copy of the downloaded ZIP");
    var cleanup = service.UpdateFromGitHub(profile.Id, old.Id, old.Version, update.Version);
    Check(cleanup.RemovedVersions.SequenceEqual(new[] { "1.0.0" }), "GitHub update did not prune the replaced package");
    Check(!Directory.Exists(old.PayloadDirectory) && File.ReadAllBytes(one).SequenceEqual(first),
        "GitHub update did not distinguish the package object from the source ZIP");
    Check(service.GetProfile(profile.Id).Mods.Single().Version == "2.0.0" && service.ValidateProfile(profile.Id).Count == 0,
        "GitHub update left the profile invalid");
}));
var articleRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../local-evidence/test-articles"));
var graphicsZip=Path.Combine(articleRoot,"Sals-Graphical-Tweaks-Unity6-2.0.1.zip");
var hullZip=Path.Combine(articleRoot,"HullFiller-v0.1.2.zip");
if(File.Exists(graphicsZip)&&File.Exists(hullZip)) tests.Add(("verified published packages deploy exact DLL and asset bytes and toggle cleanly",()=>{
    var f=Fixture("published");var service=new PlatformService(Path.Combine(root,"published-store"));
    var graphics=service.ImportPackage(graphicsZip,new(){Repository="MadTheDog/StarshipEVO-Graphical-Tweaks-Unity6"});
    var hull=service.ImportPackage(hullZip,new(){Repository="MadTheDog/StarshipEVO-HullFiller"});
    Check(graphics.Sha256=="916bcfeb415b9e55dfdbdcb175a96aa4b8491d986832f4d30a3e0588b16546b9","graphics fixture differs from published digest");
    Check(hull.Sha256=="aa2cfb3ee735369f6b1cf0d75707795d226b2046da2bd6a691299fab56bf5aea","hull fixture differs from published digest");
    var profile=service.CreateProfile("Published ZIP integration",f.Game);service.Install(profile.Id,graphics.Id,graphics.Version);service.Install(profile.Id,hull.Id,hull.Version);
    Check(service.ValidateProfile(profile.Id).Count==0,"published profile failed validation");
    foreach(var (zipPath,package) in new[]{(graphicsZip,graphics),(hullZip,hull)})
    {
        using var zip=ZipFile.OpenRead(zipPath);
        foreach(var entry in zip.Entries.Where(x=>x.FullName.StartsWith("BepInEx/plugins/",StringComparison.OrdinalIgnoreCase)&&x.Length>0))
        {
            var relative=string.Join('/',entry.FullName.Split('/').Skip(3));
            var deployed=Path.Combine(f.Game,"BepInEx","plugins","SevoManaged",package.Id,relative);
            using var source=entry.Open();var expected=System.Security.Cryptography.SHA256.HashData(source);
            Check(File.Exists(deployed)&&expected.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(deployed))),"deployed payload mismatch: "+relative);
        }
    }
    service.SetEnabled(profile.Id,graphics.Id,false);
    Check(!Directory.GetFiles(Path.Combine(f.Game,"BepInEx","plugins"),"GraphicalTweaks.dll",SearchOption.AllDirectories).Any(),"disabled plugin still discoverable");
    service.SetEnabled(profile.Id,graphics.Id,true);service.Remove(profile.Id,hull.Id);
    Check(service.ValidateProfile(profile.Id).Count==0,"profile invalid after toggle/remove");
}));
else Console.WriteLine("SKIP published-package integration: private verified fixtures are not present.");
var oldGraphicsZip=Path.Combine(articleRoot,"Sals-Graphical-Tweaks-Unity6-2.0.0.zip");
if(File.Exists(oldGraphicsZip)&&File.Exists(graphicsZip)) tests.Add(("published version update pin and rollback restore exact previous bytes",()=>{
    var f=Fixture("published-update");var service=new PlatformService(Path.Combine(root,"published-updates"));
    var old=service.ImportPackage(oldGraphicsZip);var latest=service.ImportPackage(graphicsZip);
    Check(old.Sha256=="11f46bb9c6637c1f94e86a597f001e224b7ad96cd76705e980157d3e577d9136","old fixture differs from published digest");
    var profile=service.CreateProfile("Published update",f.Game);service.Install(profile.Id,old.Id,old.Version);service.SetPinned(profile.Id,old.Id,true);
    bool blocked=false;try{service.Install(profile.Id,latest.Id,latest.Version);}catch(PlatformException){blocked=true;}Check(blocked,"pinned published package was upgraded");
    service.SetPinned(profile.Id,old.Id,false);service.Install(profile.Id,latest.Id,latest.Version);service.Rollback(profile.Id,old.Id);
    var restored=service.GetProfile(profile.Id).Mods.Single();Check(restored.Version=="2.0.0","previous version not restored");
    var file=Path.Combine(f.Game,"BepInEx","plugins","SevoManaged",old.Id,"GraphicalTweaks.dll");
    using var zip=ZipFile.OpenRead(oldGraphicsZip);using var entry=zip.GetEntry("BepInEx/plugins/GraphicalTweaks/GraphicalTweaks.dll")!.Open();
    Check(System.Security.Cryptography.SHA256.HashData(entry).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))),"rollback DLL differs from exact release bytes");
}));
var loaderArticle=Path.Combine(articleRoot,RuntimeDownloads.LoaderFileName);
if(File.Exists(loaderArticle)) tests.Add(("verified upstream loader is cached without network access",()=>{
    using var client=new HttpClient();var cache=Path.Combine(root,"verified-loader-cache");
    var path=RuntimeDownloads.EnsureLoaderAsync(client,cache,loaderArticle).GetAwaiter().GetResult();
    Check(File.Exists(path),"verified loader was not cached");Check(RuntimeDownloads.EnsureLoaderAsync(client,cache).GetAwaiter().GetResult()==path,"offline cache was not reused");
}));
tests.Add(("modpack round trips exact releases across repositories and applies the profile together", () =>
{
    var fixture = Fixture("modpack");
    ModpackTests.Verify(fixture.Game, Path.GetDirectoryName(fixture.Game)!);
}));
LauncherUpdateTests.Register(tests, root);
KeelUpdateTests.Register(tests);
if (args is ["--keel-release-fixtures", var oldKeel, var newKeel, var keelChecksum])
    KeelUpdateTests.RegisterArtifacts(tests, oldKeel, newKeel, keelChecksum, Fixture);
GameCleanupTests.Add(tests, root);
foreach(var test in tests) { try {test.Run();Console.WriteLine("PASS "+test.Name);passed++;}catch(Exception ex){Console.Error.WriteLine("FAIL "+test.Name+": "+ex.Message);} }
Console.WriteLine($"{passed}/{tests.Count} hosting tests passed"); return passed==tests.Count?0:1;

sealed class RunningGameInspector(string expectedGame) : IGameProcessInspector
{
    public int Calls { get; private set; }
    public bool IsRunning(string gameDirectory)
    {
        if(gameDirectory!=expectedGame) throw new Exception("Wrong installation inspected");
        Calls++;return true;
    }
}

sealed class FixtureAssetsHandler(Dictionary<string,byte[]> assets) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        var content=assets[request.RequestUri!.AbsoluteUri];
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {RequestMessage=request,Content=new ByteArrayContent(content)});
    }
}
