using Sevo.Mod.Core;
using System.IO.Compression;
using System.Text.Json;

if(args.Length==1&&args[0]=="--process-fixture") { File.WriteAllText("fixture-ready.txt","ready"); Thread.Sleep(15000); return; }

var runRoot = Path.GetFullPath("local-evidence/core-tests/run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runRoot);
int passed=0, failed=0;
void Test(string name, Action<string> action)
{
    if(args.Length>0&&!name.Contains(args[0],StringComparison.OrdinalIgnoreCase))return;
    var root=Path.Combine(runRoot,(passed+failed).ToString()); Directory.CreateDirectory(root);
    try { action(root); Console.WriteLine("PASS " + name); passed++; }
    catch(Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); failed++; }
}
void Check(bool value,string message="Assertion failed") { if(!value) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch(PlatformException) { return; } throw new Exception("Expected safe rejection"); }
string Zip(string root,string id="a",string version="1.0.0",Dictionary<string,string>? deps=null,string[]? conflicts=null,int[]? bricks=null,Dictionary<string,string>? extra=null)
{
    var path=Path.Combine(root,Guid.NewGuid()+".zip");
    using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
    var files=new Dictionary<string,string> { ["sevo.mod.json"]=JsonSerializer.Serialize(new { schemaVersion=1,id,name=id,version,payloadRoot="payload",dependencies=deps??[],conflicts=conflicts??[],fixedBrickIds=bricks??[] }), ["payload/Mod.dll"]="assembly-"+version };
    if(extra!=null) foreach(var kv in extra) files[kv.Key]=kv.Value;
    foreach(var kv in files) { using var w=new StreamWriter(zip.CreateEntry(kv.Key).Open()); w.Write(kv.Value); }
    return path;
}
string Game(string root) { var p=Path.Combine(root,"game"); Directory.CreateDirectory(Path.Combine(p,"BepInEx","plugins")); Directory.CreateDirectory(Path.Combine(p,"Starship EVO_Data","Managed")); File.WriteAllText(Path.Combine(p,"Starship EVO_Data","Managed","Assembly-CSharp.dll"),"fixture assembly"); File.WriteAllText(Path.Combine(p,"Starship EVO.exe"),"fixture"); return p; }
string ModPath(ProfileRecord p,string id="a") => Path.Combine(p.GameDirectory,"BepInEx","plugins","SevoManaged",id,"Mod.dll");

Test("fresh catalog",r=>Check(new PlatformService(r).ListPackages().Count==0));

Test("batch install and disable validate the resulting dependency set together", r =>
{
    var service = new PlatformService(r, new FakeProcesses());
    service.ImportPackage(Zip(r, "consumer", deps: new() { ["provider"] = ">=1.0.0" }));
    service.ImportPackage(Zip(r, "provider"));
    var profile = service.CreateProfile("Batch", Game(r));
    var installed = service.InstallBatch(profile.Id, [new("consumer", "1.0.0"), new("provider", "1.0.0")]);
    Check(installed.Mods.All(mod => mod.Enabled) && File.Exists(ModPath(profile, "consumer")));
    Reject(() => service.SetEnabledBatch(profile.Id, ["provider"], false));
    Check(service.GetProfile(profile.Id).Generation == installed.Generation && File.Exists(ModPath(profile, "provider")));
    service.SetEnabledBatch(profile.Id, ["provider", "consumer"], false);
    Check(service.GetProfile(profile.Id).Mods.All(mod => !mod.Enabled) && !File.Exists(ModPath(profile, "consumer")));
});

Test("batch updates switch related versions in one transaction and keep disabled mods disabled", r =>
{
    var service = new PlatformService(r, new FakeProcesses());
    foreach (var version in new[] { "1.0.0", "2.0.0" })
    {
        service.ImportPackage(Zip(r, "consumer", version, deps: new() { ["provider"] = version }),
            new() { Repository = "Owner/Consumer" });
        service.ImportPackage(Zip(r, "provider", version), new() { Repository = "Owner/Provider" });
        service.ImportPackage(Zip(r, "optional", version), new() { Repository = "Owner/Optional" });
    }
    var profile = service.CreateProfile("Updates", Game(r));
    service.InstallBatch(profile.Id, [new("consumer", "1.0.0"), new("provider", "1.0.0"), new("optional", "1.0.0")]);
    var before = service.SetEnabled(profile.Id, "optional", false);
    Reject(() => service.UpdateFromGitHubBatch(profile.Id, [new("provider", "1.0.0", "2.0.0")]));
    Check(service.GetProfile(profile.Id).Generation == before.Generation);
    service.UpdateFromGitHubBatch(profile.Id, [new("provider", "1.0.0", "2.0.0"),
        new("consumer", "1.0.0", "2.0.0"), new("optional", "1.0.0", "2.0.0")]);
    var after = service.GetProfile(profile.Id);
    Check(after.Mods.All(mod => mod.Version == "2.0.0") && !after.Mods.Single(mod => mod.PackageId == "optional").Enabled);
    Check(File.ReadAllText(ModPath(profile, "provider")) == "assembly-2.0.0" && !File.Exists(ModPath(profile, "optional")));
});

Test("library removal deletes only the selected cached version", r =>
{
    var service = new PlatformService(r);
    var source = Zip(r);
    var old = service.ImportPackage(source);
    service.ImportPackage(source, new PackageImportOptions { Repository = "Owner/Repo", AssetName = "mod.zip" });
    var current = service.ImportPackage(Zip(r, version: "2.0.0"));
    service.RemovePackage("a", "1.0.0");
    Check(service.ListPackages().Single().Version == "2.0.0");
    Check(!Directory.Exists(Path.GetDirectoryName(old.PayloadDirectory)));
    Check(Directory.Exists(current.PayloadDirectory) && File.Exists(source));
    Check(service.ImportPackage(source).Sha256 == old.Sha256);
});

Test("library removal protects disabled installs and rollback versions", r =>
{
    var service = new PlatformService(r, new FakeProcesses());
    service.ImportPackage(Zip(r));
    service.ImportPackage(Zip(r, version: "2.0.0"));
    var profile = service.CreateProfile("Other profile", Game(r));
    service.Install(profile.Id, "a", "1.0.0");
    service.Install(profile.Id, "a", "2.0.0");
    service.SetEnabled(profile.Id, "a", false);
    Reject(() => service.RemovePackage("a", "2.0.0"));
    Reject(() => service.RemovePackage("a", "1.0.0"));
    Check(service.ListPackages().Count == 2);
    service.Rollback(profile.Id, "a");
    Check(service.GetProfile(profile.Id).Mods.Single().Version == "1.0.0");
});

Test("library removal preserves unexpected files in a cache object", r =>
{
    var service = new PlatformService(r);
    var package = service.ImportPackage(Zip(r));
    var external = Path.Combine(Path.GetDirectoryName(package.PayloadDirectory)!, "owner.txt");
    File.WriteAllText(external, "keep");
    Reject(() => service.RemovePackage("a", "1.0.0"));
    Check(service.ListPackages().Single().Version == "1.0.0");
    Check(File.ReadAllText(external) == "keep");
});

foreach (var point in new[] { "package-removal-intent", "package-removal-committed" })
    Test("library removal recovers interruption at " + point, r =>
    {
        var source = Zip(r);
        var package = new PlatformService(r).ImportPackage(source);
        var interrupted = new PlatformService(r, new FakeProcesses(), step =>
        {
            if (step == point) throw new SimulatedCrash();
        });
        try { interrupted.RemovePackage("a", "1.0.0"); throw new Exception("Removal did not reach the checkpoint."); }
        catch (SimulatedCrash) { }
        bool committed = point == "package-removal-committed";
        if (committed) File.Delete(Path.Combine(package.PayloadDirectory, "Mod.dll"));
        var recovered = new PlatformService(r);
        Check(recovered.ListPackages().Count == (committed ? 0 : 1));
        Check(Directory.Exists(package.PayloadDirectory) != committed);
        Check(recovered.ImportPackage(source).Sha256 == package.Sha256);
    });

Test("GitHub update prunes older source versions and preserves references and other packages", r =>
{
    var service = new PlatformService(r, new FakeProcesses());
    var github = new PackageImportOptions { Repository = "Owner/Repo" };
    var unused = service.ImportPackage(Zip(r, version: "0.9.0"), github);
    var protectedRollback = service.ImportPackage(Zip(r, version: "1.0.0"), github);
    var protectedInstall = service.ImportPackage(Zip(r, version: "1.1.0"), github);
    var replaced = service.ImportPackage(Zip(r, version: "1.2.0"), github);
    var next = service.ImportPackage(Zip(r, version: "2.0.0"), github);
    service.ImportPackage(Zip(r, version: "3.0.0"), github);
    service.ImportPackage(Zip(r, version: "0.8.0"));
    service.ImportPackage(Zip(r, "b"), github);
    var current = service.CreateProfile("Updating", Game(r));
    var other = service.CreateProfile("Other", Game(Path.Combine(r, "other")));
    service.Install(current.Id, "a", replaced.Version);
    service.Install(other.Id, "a", protectedRollback.Version);
    service.Install(other.Id, "a", protectedInstall.Version);
    service.SetEnabled(other.Id, "a", false);

    var result = service.UpdateFromGitHub(current.Id, "a", replaced.Version, next.Version);
    Check(result.RemovedVersions.Order().SequenceEqual(new[] { "0.9.0", "1.2.0" }));
    Check(result.CleanupWarnings.Count == 0);
    Check(service.ListPackages().Where(p => p.Id == "a").Select(p => p.Version).Order()
        .SequenceEqual(new[] { "0.8.0", "1.0.0", "1.1.0", "2.0.0", "3.0.0" }));
    Check(service.ListPackages().Any(p => p.Id == "b"));
    Check(!Directory.Exists(Path.GetDirectoryName(unused.PayloadDirectory)) &&
          !Directory.Exists(Path.GetDirectoryName(replaced.PayloadDirectory)));
    Check(service.GetProfile(current.Id).Mods.Single().PreviousVersion == null);
    Check(File.ReadAllText(ModPath(current)) == "assembly-2.0.0");
    service.Rollback(other.Id, "a");
    Check(service.GetProfile(other.Id).Mods.Single().Version == "1.0.0");
});

Test("failed GitHub update retains previous package bytes and profile", r =>
{
    var service = new PlatformService(r, new FakeProcesses());
    var github = new PackageImportOptions { Repository = "Owner/Repo" };
    var old = service.ImportPackage(Zip(r), github);
    service.ImportPackage(Zip(r, version: "2.0.0", deps: new() { ["missing"] = "*" }), github);
    var profile = service.Install(service.CreateProfile("Current", Game(r)).Id, "a", old.Version);
    Reject(() => service.UpdateFromGitHub(profile.Id, "a", old.Version, "2.0.0"));
    Reject(() => service.UpdateFromGitHub(profile.Id, "a", "0.9.0", "2.0.0"));
    Check(service.GetProfile(profile.Id).Mods.Single().Version == old.Version);
    Check(File.ReadAllText(ModPath(profile)) == "assembly-1.0.0");
    Check(service.ListPackages().Count == 2 && Directory.Exists(old.PayloadDirectory));
});

Test("GitHub update reports an edited old cache without undoing a successful install", r =>
{
    var service = new PlatformService(r, new FakeProcesses());
    var github = new PackageImportOptions { Repository = "Owner/Repo" };
    var old = service.ImportPackage(Zip(r), github);
    service.ImportPackage(Zip(r, version: "2.0.0"), github);
    var profile = service.Install(service.CreateProfile("Current", Game(r)).Id, "a", old.Version);
    var edited = Path.Combine(old.PayloadDirectory, "Mod.dll");
    File.WriteAllText(edited, "owner edit");
    var result = service.UpdateFromGitHub(profile.Id, "a", old.Version, "2.0.0");
    Check(result.CleanupWarnings.Count == 1 && result.RemovedVersions.Count == 0);
    Check(File.ReadAllText(edited) == "owner edit");
    Check(service.ListPackages().Count == 2 && File.ReadAllText(ModPath(profile)) == "assembly-2.0.0");
});

Test("interrupted GitHub update restores deployment before any cache pruning", r =>
{
    var service = new PlatformService(r, new FakeProcesses());
    var github = new PackageImportOptions { Repository = "Owner/Repo" };
    var old = service.ImportPackage(Zip(r), github);
    service.ImportPackage(Zip(r, version: "2.0.0"), github);
    var profile = service.Install(service.CreateProfile("Current", Game(r)).Id, "a", old.Version);
    var interrupted = new PlatformService(r, new FakeProcesses(), step =>
    {
        if (step == "new-moved") throw new SimulatedCrash();
    });
    try
    {
        interrupted.UpdateFromGitHub(profile.Id, "a", old.Version, "2.0.0");
        throw new Exception("Update did not reach the checkpoint.");
    }
    catch (SimulatedCrash) { }
    Check(service.GetProfile(profile.Id).Mods.Single().Version == "1.0.0");
    Check(File.ReadAllText(ModPath(profile)) == "assembly-1.0.0" && Directory.Exists(old.PayloadDirectory));
    Check(service.UpdateFromGitHub(profile.Id, "a", old.Version, "2.0.0").RemovedVersions.Single() == "1.0.0");
});
Test("import persists immutable payload and archive",r=> { var s=new PlatformService(r); var p=s.ImportPackage(Zip(r)); Check(File.ReadAllText(Path.Combine(p.PayloadDirectory,"Mod.dll"))=="assembly-1.0.0"); Check(new PlatformService(r).ListPackages().Single().Sha256==p.Sha256); });
Test("install disable enable remove preserves config",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); var g=Game(r); Directory.CreateDirectory(Path.Combine(g,"BepInEx","config")); var c=Path.Combine(g,"BepInEx","config","a.cfg"); File.WriteAllText(c,"owner"); var p=s.CreateProfile("Test",g); p=s.Install(p.Id,"a"); Check(File.Exists(ModPath(p))); p=s.SetEnabled(p.Id,"a",false); Check(!File.Exists(ModPath(p))); p=s.SetEnabled(p.Id,"a",true); Check(File.Exists(ModPath(p))); s.Remove(p.Id,"a"); Check(!File.Exists(ModPath(p))); Check(File.ReadAllText(c)=="owner"); });
Test("same version cannot be replaced",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); Reject(()=>s.ImportPackage(Zip(r,extra:new(){["payload/change.txt"]="changed"}))); });
Test("owner prevents second profile",r=> { var g=Game(r); var s=new PlatformService(r); s.CreateProfile("one",g); Reject(()=>s.CreateProfile("two",g)); Reject(()=>new PlatformService(Path.Combine(r,"other")).CreateProfile("foreign",g)); });
Test("edited deployment survives refusal",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); var p=s.Install(s.CreateProfile("one",Game(r)).Id,"a"); File.WriteAllText(ModPath(p),"external"); Reject(()=>s.Remove(p.Id,"a")); Check(File.ReadAllText(ModPath(p))=="external"); });
foreach(var path in new[]{"../escape.dll","payload/../escape.dll","payload/CON.dll","payload/a:ads","/absolute.dll","payload/MOD.dll","payload/run.ps1","source/project.cs"})
    Test("reject archive "+path,r=>Reject(()=>new PlatformService(r).ImportPackage(Zip(r,extra:new(){[path]="bad"}))));
Test("reject duplicate manifest keys",r=> { var z=Zip(r); using(var zip=ZipFile.Open(z,ZipArchiveMode.Update)) { zip.GetEntry("sevo.mod.json")!.Delete(); using var w=new StreamWriter(zip.CreateEntry("sevo.mod.json").Open()); w.Write("{\"schemaVersion\":1,\"id\":\"a\",\"ID\":\"b\"}"); } Reject(()=>new PlatformService(r).ImportPackage(z)); });
Test("missing dependency and required disable removal",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r,"a",deps:new(){["b"]=">=1.0.0"})); s.ImportPackage(Zip(r,"b")); var p=s.CreateProfile("x",Game(r)); Reject(()=>s.Install(p.Id,"a")); s.Install(p.Id,"b"); s.Install(p.Id,"a"); Reject(()=>s.SetEnabled(p.Id,"b",false)); Reject(()=>s.Remove(p.Id,"b")); });
Test("dependency version mismatch",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r,"a",deps:new(){["b"]=">=2.0.0"})); s.ImportPackage(Zip(r,"b")); var p=s.CreateProfile("x",Game(r)); s.Install(p.Id,"b"); Reject(()=>s.Install(p.Id,"a")); });
Test("conflict rejects before activation",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r,"a",conflicts:["b"])); s.ImportPackage(Zip(r,"b")); var p=s.CreateProfile("x",Game(r)); s.Install(p.Id,"a"); Reject(()=>s.Install(p.Id,"b")); Check(!File.Exists(ModPath(p,"b"))); });
Test("fixed ID overlap rejects",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r,"a",bricks:[1000])); s.ImportPackage(Zip(r,"b",bricks:[1000])); var p=s.CreateProfile("x",Game(r)); s.Install(p.Id,"a"); Reject(()=>s.Install(p.Id,"b")); });
Test("pin blocks upgrades and rollback restores prior bytes",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); s.ImportPackage(Zip(r,version:"2.0.0")); var p=s.CreateProfile("x",Game(r)); s.Install(p.Id,"a","1.0.0"); s.SetPinned(p.Id,"a",true); Reject(()=>s.Install(p.Id,"a","2.0.0")); s.SetPinned(p.Id,"a",false); s.Install(p.Id,"a","2.0.0"); p=s.Rollback(p.Id,"a"); Check(p.Mods.Single().Version=="1.0.0"); Check(File.ReadAllText(ModPath(p))=="assembly-1.0.0"); });
Test("legacy recognition preserves full subtree",r=> { var z=Path.Combine(r,"Sals-Graphical-Tweaks-Unity6-2.0.0.zip"); using(var zip=ZipFile.Open(z,ZipArchiveMode.Create)) foreach(var entry in new[]{"BepInEx/plugins/GraphicalTweaks/GraphicalTweaks.dll","BepInEx/plugins/GraphicalTweaks/sub/data.bundle","README.md"}) { using var w=new StreamWriter(zip.CreateEntry(entry).Open()); w.Write("bytes"); } var p=new PlatformService(r).ImportPackage(z); Check(p.Id=="local.sevo.graphicaltweaks" && p.Version=="2.0.0"); Check(File.Exists(Path.Combine(p.PayloadDirectory,"sub","data.bundle"))); });
foreach(var point in new[]{"intent-written","old-moved","new-moved","profile-committed"})
    Test("recover interruption "+point,r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); s.ImportPackage(Zip(r,version:"2.0.0")); var p=s.Install(s.CreateProfile("x",Game(r)).Id,"a","1.0.0"); var crash=new PlatformService(r,new FakeProcesses(),s=> { if(s==point)throw new SimulatedCrash(); }); try { crash.Install(p.Id,"a","2.0.0"); } catch(SimulatedCrash) { } var restored=new PlatformService(r).GetProfile(p.Id); var expected=point=="profile-committed"?"2.0.0":"1.0.0"; Check(restored.Mods.Single().Version==expected); Check(File.ReadAllText(ModPath(restored))=="assembly-"+expected); Check(!File.Exists(Path.Combine(r,"operation.json"))); });
Test("recovery refuses external edit",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); s.ImportPackage(Zip(r,version:"2.0.0")); var p=s.Install(s.CreateProfile("x",Game(r)).Id,"a","1.0.0"); var crash=new PlatformService(r,new FakeProcesses(),s=> { if(s=="new-moved")throw new SimulatedCrash(); }); try { crash.Install(p.Id,"a","2.0.0"); } catch(SimulatedCrash) { } File.WriteAllText(ModPath(p),"external"); Reject(()=>new PlatformService(r).ListProfiles()); Check(File.ReadAllText(ModPath(p))=="external"); });
Test("exclusive lock refuses contention",r=> { var s=new PlatformService(r); using var held=new FileStream(Path.Combine(r,"platform.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); Reject(()=>s.ListPackages()); });
Test("running selected game refuses mutation",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); var p=s.CreateProfile("x",Game(r)); Reject(()=>new PlatformService(r,new FakeProcesses { Running=true }).Install(p.Id,"a")); Check(!File.Exists(ModPath(p))); });
Test("cached payload edit refuses activation",r=> { var s=new PlatformService(r); var a=s.ImportPackage(Zip(r)); File.WriteAllText(Path.Combine(a.PayloadDirectory,"Mod.dll"),"changed"); var p=s.CreateProfile("x",Game(r)); Reject(()=>s.Install(p.Id,"a")); Check(!File.Exists(ModPath(p))); });
Test("dependency cycle refused",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r,"a",deps:new(){["a"]="*"})); var p=s.CreateProfile("x",Game(r)); Reject(()=>s.Install(p.Id,"a")); });
Test("malformed catalog schema refused",r=> { var s=new PlatformService(r); File.WriteAllText(Path.Combine(r,"catalog.json"),"{\"schemaVersion\":2,\"packages\":[]}"); Reject(()=>s.ListPackages()); });
Test("GitHub stable endpoint parsed",r=> { string? requested=null; using var http=new HttpClient(new FakeHttp(req=> { requested=req.RequestUri!.ToString(); return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content=new StringContent("{\"tag_name\":\"v1.0.0\",\"html_url\":\"https://github.com/owner/repo/releases/tag/v1.0.0\",\"prerelease\":false,\"assets\":[{\"name\":\"mod.zip\",\"browser_download_url\":\"https://github.com/owner/repo/releases/download/v1/mod.zip\",\"digest\":\"sha256:"+new string('a',64)+"\"}]} ") }; })); var release=new GitHubReleaseClient(http).GetLatestAsync("owner/repo").GetAwaiter().GetResult(); Check(requested=="https://api.github.com/repos/owner/repo/releases/latest"); Check(release.TagName=="v1.0.0"&&release.Assets.Single().Sha256==new string('a',64)); });
Test("GitHub digest failure leaves no download",r=> { using var http=new HttpClient(new FakeHttp(req=>new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content=new StringContent("wrong") })); var client=new GitHubReleaseClient(http); Reject(()=>client.DownloadAsync(new ReleaseAsset { Name="mod.zip",DownloadUrl="https://github.com/owner/repo/releases/download/v1/mod.zip",Sha256=new string('a',64) },r).GetAwaiter().GetResult()); Check(!File.Exists(Path.Combine(r,"mod.zip"))); Check(Directory.GetFiles(r).Length==0); });
Test("GitHub unsafe destination refused",r=> { using var http=new HttpClient(new FakeHttp(req=>throw new Exception("Must not request"))); Reject(()=>new GitHubReleaseClient(http).DownloadAsync(new ReleaseAsset { Name="../evil.zip",DownloadUrl="https://github.com/owner/repo/releases/download/v1/mod.zip" },r).GetAwaiter().GetResult()); });
Test("GitHub nonofficial URL refused",r=> { using var http=new HttpClient(new FakeHttp(req=>throw new Exception("Must not request"))); Reject(()=>new GitHubReleaseClient(http).DownloadAsync(new ReleaseAsset { Name="mod.zip",DownloadUrl="https://example.com/mod.zip" },r).GetAwaiter().GetResult()); });
Test("archive rejects directory case aliases",r=>Reject(()=>new PlatformService(r).ImportPackage(Zip(r,extra:new(){["payload/sub/A.dll"]="a",["payload/SUB/B.dll"]="b"}))));
Test("archive rejects symbolic links",r=> { var z=Zip(r); using(var zip=ZipFile.Open(z,ZipArchiveMode.Update)) { var e=zip.CreateEntry("payload/link.dll"); e.ExternalAttributes=unchecked((int)0xA1FF0000); using var w=new StreamWriter(e.Open()); w.Write("target"); } Reject(()=>new PlatformService(r).ImportPackage(z)); });
Test("archive rejects expansion bomb",r=> { var z=Zip(r,extra:new(){["payload/huge.bin"]=new string('a',2*1024*1024)}); Reject(()=>new PlatformService(r).ImportPackage(z)); });
Test("legacy ambiguous version requires options",r=> { var z=Path.Combine(r,"legacy.zip"); using(var zip=ZipFile.Open(z,ZipArchiveMode.Create)) { using var w=new StreamWriter(zip.CreateEntry("BepInEx/plugins/HullFiller/HullFiller.dll").Open()); w.Write("payload"); } var s=new PlatformService(r); Reject(()=>s.ImportPackage(z)); var p=s.ImportPackage(z,new PackageImportOptions { Version="1.2.3" }); Check(p.Id=="local.sevo.hullfiller"&&p.Version=="1.2.3"); });
Test("manifest wrong schema refused",r=> { var z=Zip(r); using(var zip=ZipFile.Open(z,ZipArchiveMode.Update)) { zip.GetEntry("sevo.mod.json")!.Delete(); using var w=new StreamWriter(zip.CreateEntry("sevo.mod.json").Open()); w.Write("{\"schemaVersion\":7}"); } Reject(()=>new PlatformService(r).ImportPackage(z)); });
Test("validation reports externally edited files",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); var p=s.Install(s.CreateProfile("x",Game(r)).Id,"a"); Check(s.ValidateProfile(p.Id).Count==0); File.WriteAllText(ModPath(p),"external"); Check(s.ValidateProfile(p.Id).Any(x=>x.Contains("differs"))); });
Test("recovery preserves added transaction file",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); var p=s.Install(s.CreateProfile("x",Game(r)).Id,"a"); var crash=new PlatformService(r,new FakeProcesses(),s=> { if(s=="new-moved")throw new SimulatedCrash(); }); try { crash.SetEnabled(p.Id,"a",false); } catch(SimulatedCrash) { } var tx=Directory.GetDirectories(Path.Combine(p.GameDirectory,"BepInEx","SevoTransactions",p.Id)).Single(); var external=Path.Combine(tx,"owner-data.txt"); File.WriteAllText(external,"keep"); Reject(()=>new PlatformService(r).ListProfiles()); Check(File.ReadAllText(external)=="keep"); });
Test("GitHub digest success writes exact bytes",r=> { using var http=new HttpClient(new FakeHttp(req=>new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content=new StringContent("payload") })); var sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("payload"))).ToLowerInvariant(); var path=new GitHubReleaseClient(http).DownloadAsync(new ReleaseAsset { Name="mod.zip",DownloadUrl="https://github.com/owner/repo/releases/download/v1/mod.zip",Sha256=sha },r).GetAwaiter().GetResult(); Check(File.ReadAllText(path)=="payload"); });
Test("GitHub HTTP failure surfaced",r=> { using var http=new HttpClient(new FakeHttp(req=>new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))); Reject(()=>new GitHubReleaseClient(http).GetLatestAsync("owner/repo").GetAwaiter().GetResult()); });
Test("GitHub cancellation propagated",r=> { using var http=new HttpClient(new FakeHttp(req=>throw new Exception("must not request"))); using var cancel=new CancellationTokenSource(); cancel.Cancel(); try { new GitHubReleaseClient(http).GetLatestAsync("owner/repo",cancel.Token).GetAwaiter().GetResult(); throw new Exception("Expected cancellation"); } catch(OperationCanceledException) { } });
Test("GitHub invalid repository refused",r=> { using var http=new HttpClient(new FakeHttp(req=>throw new Exception("must not request"))); Reject(()=>new GitHubReleaseClient(http).GetLatestAsync("https://github.com/owner/repo").GetAwaiter().GetResult()); });
Test("null manifest metadata refused",r=> { var z=Zip(r); using(var zip=ZipFile.Open(z,ZipArchiveMode.Update)) { zip.GetEntry("sevo.mod.json")!.Delete(); using var w=new StreamWriter(zip.CreateEntry("sevo.mod.json").Open()); w.Write("{\"schemaVersion\":1,\"id\":\"a\",\"name\":\"A\",\"version\":\"1.0.0\",\"payloadRoot\":null}"); } Reject(()=>new PlatformService(r).ImportPackage(z)); });
Test("production process inspector matches executable path",r=> {
    var game=Game(r); var other=Path.Combine(r,"other-game"); Directory.CreateDirectory(other);
    foreach(var file in Directory.GetFiles(AppContext.BaseDirectory)) { var dest=Path.Combine(game,Path.GetFileName(file)); if(!File.Exists(dest))File.Copy(file,dest); }
    var apphost=Path.Combine(AppContext.BaseDirectory,typeof(FakeProcesses).Assembly.GetName().Name+".exe");
    Check(File.Exists(apphost),"Expected Windows test apphost next to the test assembly");
    File.Copy(apphost,Path.Combine(game,"Starship EVO.exe"),true);
    using var proc=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(game,"Starship EVO.exe"),"--process-fixture") { UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=game })!;
    try { Check(SpinWait.SpinUntil(()=>File.Exists(Path.Combine(game,"fixture-ready.txt"))||proc.HasExited,TimeSpan.FromSeconds(10)),"Fixture startup timed out"); Check(!proc.HasExited,"Fixture exited before inspection"); var inspector=new GameProcessInspector(); Check(inspector.IsRunning(game),"Expected exact executable detected"); Check(!inspector.IsRunning(other),"Unrelated same-name process must not block other target"); Reject(()=>new PlatformService(r).CreateProfile("busy",game)); }
    finally { if(!proc.HasExited)proc.Kill(); proc.WaitForExit(); }
});
foreach(var point in new[]{"create-intent-written","create-owner-written","create-profile-committed"})
    Test("profile creation recovers "+point,r=> { var game=Game(r); bool interrupted=false; var crash=new PlatformService(r,new FakeProcesses(),s=> { if(s==point)throw new SimulatedCrash(); }); try { crash.CreateProfile("x",game); } catch(SimulatedCrash) { interrupted=true; } Check(interrupted,"Expected injected creation interruption"); var profiles=new PlatformService(r).ListProfiles(); Check(profiles.Count==(point=="create-profile-committed"?1:0)); Check(File.Exists(Path.Combine(game,"BepInEx","sevo-profile-owner.json"))==(point=="create-profile-committed")); if(profiles.Count==0)new PlatformService(r).CreateProfile("replacement",game); });
Test("committed cleanup preserves edited backup",r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); var p=s.Install(s.CreateProfile("x",Game(r)).Id,"a"); string? changed=null; var editing=new PlatformService(r,new FakeProcesses(),point=> { if(point=="profile-committed") { var tx=Directory.GetDirectories(Path.Combine(p.GameDirectory,"BepInEx","SevoTransactions",p.Id)).Single(); changed=Path.Combine(tx,"old","a","Mod.dll"); File.WriteAllText(changed,"owner edit"); } }); Reject(()=>editing.SetEnabled(p.Id,"a",false)); Check(changed!=null&&File.ReadAllText(changed)=="owner edit"); });
foreach(var (label,json) in new[]{("null record","{\"schemaVersion\":1,\"packages\":[null]}"),("missing schema","{\"packages\":[]}"),("missing collection","{\"schemaVersion\":1}"),("null collection","{\"schemaVersion\":1,\"packages\":null}")})
    Test("review corrupt catalog "+label,r=> { var s=new PlatformService(r); var file=Path.Combine(r,"catalog.json"); File.WriteAllText(file,json); Reject(()=>s.ListPackages()); Check(File.ReadAllText(file)==json,"Rejected catalog was modified"); });
foreach(var (label,edit) in new (string,Action<System.Text.Json.Nodes.JsonObject>)[]{
    ("null payload directory",x=>x["payloadDirectory"]=null),("null files",x=>x["files"]=null),("null digest",x=>x["files"]!["Mod.dll"]=null),("null dependency requirement",x=>x["dependencies"]!["b"]=null),("null conflict",x=>x["conflicts"]=new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null)),("missing required name",x=>x.Remove("name")) })
    Test("review corrupt package "+label,r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); var file=Path.Combine(r,"catalog.json"); var data=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!; edit(data["packages"]![0]!.AsObject()); var json=data.ToJsonString(); File.WriteAllText(file,json); Reject(()=>s.ListPackages()); Check(File.ReadAllText(file)==json); });
foreach(var (label,json) in new[]{("null record","{\"schemaVersion\":1,\"items\":[null]}"),("missing schema","{\"items\":[]}"),("missing collection","{\"schemaVersion\":1}")})
    Test("review corrupt profiles "+label,r=> { var s=new PlatformService(r); var file=Path.Combine(r,"profiles.json"); File.WriteAllText(file,json); Reject(()=>s.ListProfiles()); Check(File.ReadAllText(file)==json); });
foreach(var (label,edit) in new (string,Action<System.Text.Json.Nodes.JsonObject>)[]{ ("null mod",x=>x["mods"]=new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null)),("null game path",x=>x["gameDirectory"]=null),("missing mods",x=>x.Remove("mods")),("null mod version",x=>x["mods"]![0]!["version"]=null) })
    Test("review corrupt profile "+label,r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); var p=s.Install(s.CreateProfile("x",Game(r)).Id,"a"); var file=Path.Combine(r,"profiles.json"); var data=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!; edit(data["items"]![0]!.AsObject()); var json=data.ToJsonString(); File.WriteAllText(file,json); Reject(()=>s.ListProfiles()); Check(File.ReadAllText(ModPath(p))=="assembly-1.0.0"); Check(File.ReadAllText(file)==json); });
foreach(var (label,edit) in new (string,Action<System.Text.Json.Nodes.JsonObject>)[]{ ("null before",x=>x["before"]=null),("null after mod",x=>x["after"]!["mods"]=new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null)),("null old files",x=>x["oldFiles"]=null),("missing schema",x=>x.Remove("schemaVersion")),("missing after",x=>x.Remove("after")) })
    Test("review corrupt operation intent "+label,r=> { var s=new PlatformService(r); s.ImportPackage(Zip(r)); var p=s.Install(s.CreateProfile("x",Game(r)).Id,"a"); var crash=new PlatformService(r,new FakeProcesses(),point=> { if(point=="intent-written")throw new SimulatedCrash(); }); try { crash.SetEnabled(p.Id,"a",false); } catch(SimulatedCrash) { } var file=Path.Combine(r,"operation.json"); var data=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject(); edit(data); var json=data.ToJsonString(); File.WriteAllText(file,json); var profiles=File.ReadAllText(Path.Combine(r,"profiles.json")); Reject(()=>s.ListPackages()); Check(File.ReadAllText(ModPath(p))=="assembly-1.0.0"); Check(File.ReadAllText(file)==json); Check(File.ReadAllText(Path.Combine(r,"profiles.json"))==profiles); });
foreach(var (label,json) in new[]{("missing schema","{\"profile\":null}"),("null profile","{\"schemaVersion\":1,\"profile\":null}"),("missing profile","{\"schemaVersion\":1}")})
    Test("review corrupt creation intent "+label,r=> { var s=new PlatformService(r); var file=Path.Combine(r,"profile-create.json"); File.WriteAllText(file,json); Reject(()=>s.ListProfiles()); Check(File.ReadAllText(file)==json); });
Test("review nested documentation retained but never deployed",r=> { var s=new PlatformService(r); var zip=Zip(r,extra:new(){["docs/DEVELOPMENT.md"]="guide",["docs/nested/setup.txt"]="setup",["LICENSE"]="license",["README"]="readme",["COPYING"]="copying",["NOTICE.txt"]="notice"}); var a=s.ImportPackage(zip); Check(a.Files.Count==1); Check(File.ReadAllBytes(zip).SequenceEqual(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(a.PayloadDirectory)!,"archive.zip")))); var p=s.Install(s.CreateProfile("x",Game(r)).Id,"a"); Check(Directory.GetFiles(Path.Combine(p.GameDirectory,"BepInEx","plugins","SevoManaged"),"*",SearchOption.AllDirectories).Length==1); });
Test("review same bytes permit explicit missing source enrichment",r=> { var s=new PlatformService(r); var zip=Zip(r); var original=s.ImportPackage(zip); var hash=original.Sha256; Check(original.Repository==null); var enriched=s.ImportPackage(zip,new PackageImportOptions { Repository="Owner/Repo" }); Check(enriched.Repository=="Owner/Repo"&&enriched.Sha256==hash); Check(new PlatformService(r).ListPackages().Single().Repository=="Owner/Repo"); Reject(()=>s.ImportPackage(zip,new PackageImportOptions { Repository="Other/Repo" })); Check(s.ListPackages().Single().Repository=="Owner/Repo"); });
foreach(var path in new[]{"docs/run.ps1","docs/project.cs","source/readme.md","docs/../escape.md","docs/tool.exe"})Test("review documentation rejects "+path,r=>Reject(()=>new PlatformService(r).ImportPackage(Zip(r,extra:new(){[path]="unsafe"}))));
string CleanGame(string r) { var g=Path.Combine(r,"clean-game"); Directory.CreateDirectory(Path.Combine(g,"Starship EVO_Data","Managed")); File.WriteAllText(Path.Combine(g,"Starship EVO.exe"),"inert fixture"); File.WriteAllText(Path.Combine(g,"Starship EVO_Data","Managed","Assembly-CSharp.dll"),"inert managed fixture"); return g; }
Test("review clean valid game creates profile without manual loader",r=> { var g=CleanGame(r); var s=new PlatformService(r); var p=s.CreateProfile("clean",g); Check(p.GameDirectory==g); Check(Directory.Exists(Path.Combine(g,"BepInEx","plugins"))); Check(File.Exists(Path.Combine(g,"BepInEx","sevo-profile-owner.json"))); Reject(()=>s.CreateProfile("second",g)); });
Test("review clean target requires game managed assembly",r=> { var g=Path.Combine(r,"not-game"); Directory.CreateDirectory(Path.Combine(g,"BepInEx","plugins")); File.WriteAllText(Path.Combine(g,"Starship EVO.exe"),"fake"); Reject(()=>new PlatformService(r).CreateProfile("invalid",g)); Check(!File.Exists(Path.Combine(g,"BepInEx","sevo-profile-owner.json"))); });
Test("review clean creation recovery removes only owned empty directories",r=> { var g=CleanGame(r); var s=new PlatformService(r,new FakeProcesses(),point=> { if(point=="create-owner-written")throw new SimulatedCrash(); }); bool crashed=false; try { s.CreateProfile("clean",g); } catch(SimulatedCrash) { crashed=true; } Check(crashed); Check(new PlatformService(r).ListProfiles().Count==0); Check(!Directory.Exists(Path.Combine(g,"BepInEx"))); });
Test("review clean creation recovery preserves added content",r=> { var g=CleanGame(r); var s=new PlatformService(r,new FakeProcesses(),point=> { if(point=="create-owner-written")throw new SimulatedCrash(); }); try { s.CreateProfile("clean",g); } catch(SimulatedCrash) { } var added=Path.Combine(g,"BepInEx","plugins","owner.dll"); File.WriteAllText(added,"owner bytes"); Reject(()=>new PlatformService(r).ListProfiles()); Check(File.ReadAllText(added)=="owner bytes"); });
foreach(var initiallyEmpty in new[]{false,true})foreach(var point in new[]{"intent-written","old-moved","new-moved","profile-committed"})
    Test($"final first install recovery empty={initiallyEmpty} {point}",r=>
    {
        var s=new PlatformService(r,new FakeProcesses()); s.ImportPackage(Zip(r)); var p=s.CreateProfile("first",Game(r));
        var managed=Path.Combine(p.GameDirectory,"BepInEx","plugins","SevoManaged"); if(initiallyEmpty)Directory.CreateDirectory(managed);
        var interrupted=false; var crashing=new PlatformService(r,new FakeProcesses(),step=> { if(step==point)throw new SimulatedCrash(); });
        try { crashing.Install(p.Id,"a"); } catch(SimulatedCrash) { interrupted=true; } Check(interrupted,"Expected durable-boundary interruption");
        var recovered=new PlatformService(r,new FakeProcesses()); var restored=recovered.GetProfile(p.Id); bool committed=point=="profile-committed";
        Check(restored.Mods.Count==(committed?1:0),"Recovered profile does not match committed state");
        Check(File.Exists(ModPath(restored))==committed,"Recovered deployed DLL does not match profile");
        Check(Directory.Exists(managed)==(committed||initiallyEmpty),"Original managed directory existence was not restored");
        Check(!File.Exists(Path.Combine(r,"operation.json")),"Recovered intent still blocks operations"); Check(recovered.ValidateProfile(p.Id).Count==0);
        recovered.Install(p.Id,"a"); Check(File.ReadAllText(ModPath(p))=="assembly-1.0.0","Subsequent install remains blocked");
    });
foreach(var initiallyEmpty in new[]{false,true})foreach(var point in new[]{"new-moved","profile-committed"})foreach(var edit in new[]{"changed DLL","added file"})
    Test($"final first install preserves external {edit} empty={initiallyEmpty} {point}",r=>
    {
        var s=new PlatformService(r,new FakeProcesses()); s.ImportPackage(Zip(r)); var p=s.CreateProfile("first",Game(r));
        var managed=Path.Combine(p.GameDirectory,"BepInEx","plugins","SevoManaged"); if(initiallyEmpty)Directory.CreateDirectory(managed);
        var crashing=new PlatformService(r,new FakeProcesses(),step=> { if(step==point)throw new SimulatedCrash(); });
        try { crashing.Install(p.Id,"a"); } catch(SimulatedCrash) { }
        var changed=edit=="changed DLL"?ModPath(p):Path.Combine(managed,"owner-extra.dll"); File.WriteAllText(changed,"external bytes");
        var journal=File.ReadAllText(Path.Combine(r,"operation.json")); var profile=File.ReadAllText(Path.Combine(r,"profiles.json"));
        Reject(()=>new PlatformService(r,new FakeProcesses()).ListProfiles());
        Check(File.ReadAllText(changed)=="external bytes","External content erased during recovery");
        Check(File.ReadAllText(Path.Combine(r,"operation.json"))==journal,"Unsafe intent was removed or changed");
        Check(File.ReadAllText(Path.Combine(r,"profiles.json"))==profile,"Unsafe recovery changed profile metadata");
    });
Console.WriteLine($"RESULT {passed} passed, {failed} failed; evidence {runRoot}"); Environment.ExitCode=failed==0&&passed>0?0:1;
sealed class FakeProcesses : IGameProcessInspector { public bool Running { get; set; } public bool IsRunning(string gameDirectory)=>Running; }
sealed class SimulatedCrash : Exception { }
sealed class FakeHttp(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) { token.ThrowIfCancellationRequested(); var r=response(request); r.RequestMessage=request; return Task.FromResult(r); } }
