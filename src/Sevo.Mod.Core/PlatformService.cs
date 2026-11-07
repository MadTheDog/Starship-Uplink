using System.Text.RegularExpressions;
namespace Sevo.Mod.Core;

/// <summary>Immutable package storage and exclusively owned deployments. Never loads imported assemblies.</summary>
public sealed partial class PlatformService
{
    readonly string root,store,catalogPath,profilesPath,journalPath,creationPath;
    readonly IGameProcessInspector processes;
    readonly Action<string>? checkpoint;
    public PlatformService(string workspaceRoot) : this(workspaceRoot,new GameProcessInspector()) { }
    public PlatformService(string workspaceRoot,IGameProcessInspector processInspector,Action<string>? mutationCheckpoint=null)
    {
        root=Path.GetFullPath(workspaceRoot); SafeFiles.Create(root); store=Path.Combine(root,"packages"); SafeFiles.Create(store);
        catalogPath=Path.Combine(root,"catalog.json"); profilesPath=Path.Combine(root,"profiles.json"); journalPath=Path.Combine(root,"operation.json");
        creationPath=Path.Combine(root,"profile-create.json");
        processes=processInspector??throw new ArgumentNullException(nameof(processInspector)); checkpoint=mutationCheckpoint;
    }
    FileStream Lock()
    {
        var path=Path.Combine(root,"platform.lock"); SafeFiles.Ancestors(path);
        try { return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch(IOException ex) { throw new PlatformException("Another platform operation is in progress. Try again after it finishes.",ex); }
    }
    Catalog Catalog()
    {
        var data=File.Exists(catalogPath)?SafeFiles.Read<Catalog>(catalogPath):new Catalog();
        if(data.SchemaVersion!=1||data.Packages==null)throw new PlatformException("Unsupported or malformed package catalog.");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var p in data.Packages)
        {
            if(p==null)throw new PlatformException("Package catalog contains a null package record.");
            SafeFiles.Id(p.Id); PackageVersion.Parse(p.Version);
            if(!Regex.IsMatch(p.Sha256??"","^[a-f0-9]{64}$")||!seen.Add(p.Id+"/"+p.Version)||p.Files==null||p.Dependencies==null||p.Conflicts==null||p.FixedBrickIds==null||p.FixedShapeIds==null)throw new PlatformException("Invalid or duplicate package catalog entry.");
            if(string.IsNullOrWhiteSpace(p.Name)||p.Name.Length>200)throw new PlatformException("Package catalog contains a missing or invalid package name.");
            ValidateAbsolutePath(p.PayloadDirectory,"catalog payload directory");
            if(!string.Equals(Path.GetFullPath(p.PayloadDirectory),Path.Combine(store,p.Sha256!,"payload"),StringComparison.OrdinalIgnoreCase))throw new PlatformException("Catalog payload directory escapes its content object.");
            ValidateFileMap(p.Files,"package catalog");
            foreach(var dependency in p.Dependencies) { SafeFiles.Id(dependency.Key); PackageVersion.Satisfies("0.0.0",dependency.Value); }
            foreach(var conflict in p.Conflicts)SafeFiles.Id(conflict);
            if(p.Repository!=null)GitHubReleaseClient.ValidateRepository(p.Repository);
            if(p.AssetName!=null) { SafeFiles.Relative(p.AssetName); if(p.AssetName.Contains('/'))throw new PlatformException("Invalid asset name in package catalog."); }
            if(p.FixedBrickIds.Concat(p.FixedShapeIds).Any(x=>x<0)||p.FixedBrickIds.Distinct().Count()!=p.FixedBrickIds.Count||p.FixedShapeIds.Distinct().Count()!=p.FixedShapeIds.Count)throw new PlatformException("Invalid fixed IDs in package catalog.");
        }
        return data;
    }
    Profiles Profiles() => ReadProfiles(profilesPath);
    static Profiles ReadProfiles(string path)
    {
        SafeFiles.Ancestors(path);
        var attributes=SafeFiles.Attributes(path);
        if(attributes is FileAttributes present && (present&FileAttributes.Directory)!=0)throw new PlatformException("Profile catalog path is a directory.");
        var data=attributes.HasValue?SafeFiles.Read<Profiles>(path):new Profiles();
        if(data.SchemaVersion!=1||data.Items==null)throw new PlatformException("Unsupported or malformed profile catalog.");
        var ids=new HashSet<string>(); var games=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var p in data.Items)
        {
            ValidateProfileRecord(p,"profile catalog");
            if(!ids.Add(p.Id)||!games.Add(Path.GetFullPath(p.GameDirectory)))throw new PlatformException("Duplicate profile identity or game directory.");
        }
        return data;
    }
    static void ValidateAbsolutePath(string? path,string context)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path))throw new PlatformException("Missing or invalid absolute path in "+context+".");
        try { Path.GetFullPath(path); } catch(Exception ex) when(ex is ArgumentException or NotSupportedException) { throw new PlatformException("Invalid path in "+context+".",ex); }
    }
    static void ValidateFileMap(Dictionary<string,string>? files,string context)
    {
        if(files==null)throw new PlatformException("Missing file inventory in "+context+".");
        foreach(var f in files) { SafeFiles.Relative(f.Key); if(!Regex.IsMatch(f.Value??"","^[a-f0-9]{64}$"))throw new PlatformException("Invalid file digest in "+context+"."); }
    }
    static void ValidateProfileRecord(ProfileRecord? p,string context)
    {
        if(p==null)throw new PlatformException("Null profile record in "+context+".");
        if(!Guid.TryParseExact(p.Id,"N",out _)||!Guid.TryParseExact(p.Generation,"N",out _)||string.IsNullOrWhiteSpace(p.Name)||p.Name.Length>200||p.Mods==null)throw new PlatformException("Missing or invalid profile fields in "+context+".");
        ValidateAbsolutePath(p.GameDirectory,context);
        var mods=new HashSet<string>();
        foreach(var m in p.Mods)
        {
            if(m==null)throw new PlatformException("Null mod record in "+context+".");
            SafeFiles.Id(m.PackageId); PackageVersion.Parse(m.Version); if(m.PreviousVersion!=null)PackageVersion.Parse(m.PreviousVersion);
            if(!mods.Add(m.PackageId))throw new PlatformException("Duplicate profile mod in "+context+".");
        }
    }
    public PackageRecord ImportPackage(string archivePath,PackageImportOptions? options=null)
    { using var gate=Lock(); Recover(); var data=Catalog(); var result=PackageImporter.Import(Path.GetFullPath(archivePath),options,store,data); SafeFiles.Write(catalogPath,data); return result; }
    public IReadOnlyList<PackageRecord> ListPackages() { using var gate=Lock(); Recover(); return Catalog().Packages; }
    public IReadOnlyList<ProfileRecord> ListProfiles() { using var gate=Lock(); Recover(); return Profiles().Items; }
    public ProfileRecord GetProfile(string profileId) { using var gate=Lock(); Recover(); return Find(Profiles(),profileId); }
    static ProfileRecord Find(Profiles profiles,string id) => profiles.Items.SingleOrDefault(x=>x.Id==id)??throw new PlatformException("Profile not found: "+id);
    string Managed(ProfileRecord p) => Path.Combine(p.GameDirectory,"BepInEx","plugins","SevoManaged");
    string Marker(ProfileRecord p) => Path.Combine(p.GameDirectory,"BepInEx","sevo-profile-owner.json");
    string Transaction(ProfileRecord p,string id) => Path.Combine(p.GameDirectory,"BepInEx","SevoTransactions",p.Id,id);
    void Stopped(ProfileRecord p) { SafeFiles.Ancestors(p.GameDirectory); if(processes.IsRunning(p.GameDirectory))throw new PlatformException("Close the selected Starship EVO game before changing its mods."); }
    void Owner(ProfileRecord p)
    {
        AssertNoGameCleanup(p.GameDirectory);
        SafeFiles.Ancestors(Marker(p)); if(!File.Exists(Marker(p)))throw new PlatformException("The game directory has no profile ownership marker.");
        var owner=SafeFiles.Read<Ownership>(Marker(p));
        if(owner.SchemaVersion!=1||owner.ProfileId!=p.Id||!string.Equals(owner.WorkspaceRoot,root,StringComparison.OrdinalIgnoreCase))throw new PlatformException("The game directory belongs to another profile/workspace.");
    }
    public ProfileRecord CreateProfile(string name,string gameDirectory)
    {
        using var gate=Lock(); Recover();
        if(string.IsNullOrWhiteSpace(name)||name.Length>200)throw new PlatformException("Profile name is required (maximum 200 characters).");
        var game=Path.GetFullPath(gameDirectory); SafeFiles.Ancestors(game);
        var executable=Path.Combine(game,"Starship EVO.exe"); var assembly=Path.Combine(game,"Starship EVO_Data","Managed","Assembly-CSharp.dll"); var plugins=Path.Combine(game,"BepInEx","plugins");
        SafeFiles.Ancestors(executable); SafeFiles.Ancestors(assembly); SafeFiles.Ancestors(plugins);
        if(!File.Exists(executable)||!File.Exists(assembly))throw new PlatformException("Select a valid Starship EVO game directory containing Starship EVO.exe and Starship EVO_Data/Managed/Assembly-CSharp.dll. The launcher prepares BepInEx automatically.");
        var data=Profiles(); var p=new ProfileRecord { Id=Guid.NewGuid().ToString("N"),Name=name,GameDirectory=game,Generation=Guid.NewGuid().ToString("N") }; Stopped(p);
        AssertNoGameCleanup(game);
        SafeFiles.Ancestors(Marker(p)); SafeFiles.Ancestors(Managed(p));
        if(data.Items.Any(x=>SamePath(x.GameDirectory,game))||File.Exists(Marker(p)))throw new PlatformException("This game directory already has a profile claim. Open its owning Uplink workspace, or choose Clean game to inspect a claim left by a lost profile.");
        if(Directory.Exists(Managed(p))||File.Exists(Managed(p))||Directory.Exists(Marker(p)))throw new PlatformException("This game directory has SevoManaged content or an invalid profile marker. Preserve and review those files before claiming the game.");
        var createdDirectories=new[]{"BepInEx","BepInEx/plugins"}.Where(relative=>!Directory.Exists(SafeFiles.Under(game,relative))).ToList();
        SafeFiles.Write(creationPath,new ProfileCreationIntent { Profile=p,CreatedDirectories=createdDirectories }); checkpoint?.Invoke("create-intent-written");
        foreach(var relative in createdDirectories)SafeFiles.Create(SafeFiles.Under(game,relative));
        SafeFiles.Ancestors(Marker(p));
        try { using var claim=new FileStream(Marker(p),FileMode.CreateNew,FileAccess.Write,FileShare.None); System.Text.Json.JsonSerializer.Serialize(claim,new Ownership { ProfileId=p.Id,WorkspaceRoot=root },SafeFiles.Json); claim.Flush(true); }
        catch(IOException ex) { throw new PlatformException("Could not exclusively claim this game directory.",ex); }
        checkpoint?.Invoke("create-owner-written"); data.Items.Add(p); SafeFiles.Write(profilesPath,data); checkpoint?.Invoke("create-profile-committed"); File.Delete(creationPath); return p;
    }
    public ProfileRecord Install(string profileId,string packageId,string? version=null) => Mutate(profileId,(p,c)=>
    {
        SafeFiles.Id(packageId); var package=c.Packages.Where(x=>x.Id==packageId&&(version==null||x.Version==version)).OrderByDescending(x=>PackageVersion.Parse(x.Version)).FirstOrDefault()??throw new PlatformException("Package/version not found. Import it before installing.");
        var mod=p.Mods.SingleOrDefault(x=>x.PackageId==packageId);
        if(mod==null)p.Mods.Add(new ProfileModRecord { PackageId=packageId,Version=package.Version,Enabled=true });
        else { if(mod.Pinned&&mod.Version!=package.Version)throw new PlatformException("This mod is pinned. Unpin it explicitly before changing its version."); if(mod.Version!=package.Version) { mod.PreviousVersion=mod.Version; mod.Version=package.Version; } mod.Enabled=true; }
    });
    static ProfileModRecord Mod(ProfileRecord p,string id) => p.Mods.SingleOrDefault(x=>x.PackageId==id)??throw new PlatformException("This package is not installed in the profile.");
    public ProfileRecord SetEnabled(string profileId,string packageId,bool enabled) => Mutate(profileId,(p,c)=>Mod(p,packageId).Enabled=enabled);
    public ProfileRecord Remove(string profileId,string packageId) => Mutate(profileId,(p,c)=>p.Mods.Remove(Mod(p,packageId)));
    public ProfileRecord SetPinned(string profileId,string packageId,bool pinned) => Mutate(profileId,(p,c)=>Mod(p,packageId).Pinned=pinned);
    public ProfileRecord Rollback(string profileId,string packageId) => Mutate(profileId,(p,c)=>
    { var m=Mod(p,packageId); if(m.Pinned)throw new PlatformException("Unpin the mod before rolling back."); if(m.PreviousVersion==null)throw new PlatformException("No previous version is recorded."); (m.Version,m.PreviousVersion)=(m.PreviousVersion,m.Version); });
    PackageRecord Package(Catalog c,ProfileModRecord m) => c.Packages.SingleOrDefault(x=>x.Id==m.PackageId&&x.Version==m.Version)??throw new PlatformException("Installed package version is missing from the catalog: "+m.PackageId);
    List<string> Validate(ProfileRecord p,Catalog c)
    {
        var errors=new List<string>(); var active=p.Mods.Where(m=>m.Enabled).ToDictionary(m=>m.PackageId,m=>Package(c,m)); var bricks=new Dictionary<int,string>(); var shapes=new Dictionary<int,string>();
        foreach(var (id,package) in active)
        {
            foreach(var dep in package.Dependencies)if(!active.TryGetValue(dep.Key,out var required)||!PackageVersion.Satisfies(required.Version,dep.Value))errors.Add($"{id} requires enabled {dep.Key} {dep.Value}; install the dependency explicitly.");
            foreach(var conflict in package.Conflicts)if(active.ContainsKey(conflict))errors.Add($"{id} conflicts with {conflict}.");
            foreach(var (values,map,label) in new[]{(package.FixedBrickIds,bricks,"brick"),(package.FixedShapeIds,shapes,"shape")})foreach(var value in values)if(!map.TryAdd(value,id))errors.Add($"Fixed {label} ID {value} overlaps between {map[value]} and {id}.");
        }
        var visiting=new HashSet<string>(); var done=new HashSet<string>();
        void Visit(string id) { if(done.Contains(id))return; if(!visiting.Add(id)) { errors.Add("Dependency cycle involving "+id+"."); return; } foreach(var dep in active[id].Dependencies.Keys)if(active.ContainsKey(dep))Visit(dep); visiting.Remove(id); done.Add(id); }
        foreach(var id in active.Keys)Visit(id); return errors;
    }
    Dictionary<string,string> Expected(ProfileRecord p,Catalog c)
    { var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); foreach(var m in p.Mods.Where(x=>x.Enabled))foreach(var f in Package(c,m).Files)result.Add(m.PackageId+"/"+f.Key,f.Value); return result; }
    void CheckTree(string path,Dictionary<string,string> expected,string context)
    { if(!SafeFiles.Equal(SafeFiles.Inventory(path),expected))throw new PlatformException(context+" differs from its recorded files. Preserve and resolve external edits before retrying."); }
    public IReadOnlyList<string> ValidateProfile(string profileId)
    {
        using var gate=Lock(); Recover(); var p=Find(Profiles(),profileId); var c=Catalog(); var result=Validate(p,c);
        try { Owner(p); CheckTree(Managed(p),Expected(p,c),"Installed content"); foreach(var m in p.Mods)CheckTree(Package(c,m).PayloadDirectory,Package(c,m).Files,"Cached package"); } catch(PlatformException ex) { result.Add(ex.Message); } return result;
    }
    ProfileRecord Mutate(string profileId,Action<ProfileRecord,Catalog> change)
    {
        using var gate=Lock(); Recover(); var profiles=Profiles(); var before=Find(profiles,profileId); var c=Catalog(); Owner(before); Stopped(before);
        var oldFiles=Expected(before,c); CheckTree(Managed(before),oldFiles,"Installed content");
        var after=System.Text.Json.JsonSerializer.Deserialize<ProfileRecord>(System.Text.Json.JsonSerializer.Serialize(before,SafeFiles.Json),SafeFiles.Json)!;
        change(after,c); var errors=Validate(after,c); if(errors.Count>0)throw new PlatformException(string.Join(Environment.NewLine,errors));
        foreach(var m in after.Mods) { var pack=Package(c,m); CheckTree(pack.PayloadDirectory,pack.Files,"Cached package"); }
        after.Generation=Guid.NewGuid().ToString("N"); var tx=Guid.NewGuid().ToString("N"); var dir=Transaction(before,tx); SafeFiles.Create(dir); var stage=Path.Combine(dir,"new"); SafeFiles.Create(stage);
        foreach(var m in after.Mods.Where(x=>x.Enabled)) { var pack=Package(c,m); foreach(var f in pack.Files) { var dest=SafeFiles.Under(stage,m.PackageId+"/"+f.Key); SafeFiles.Create(Path.GetDirectoryName(dest)!); File.Copy(SafeFiles.Under(pack.PayloadDirectory,f.Key),dest); } }
        var newFiles=Expected(after,c); CheckTree(stage,newFiles,"Staged content"); var intent=new OperationIntent { TransactionId=tx,Before=before,After=after,OldFiles=oldFiles,NewFiles=newFiles };
        SafeFiles.Write(journalPath,intent); checkpoint?.Invoke("intent-written"); Stopped(before); CheckTree(Managed(before),oldFiles,"Installed content");
        if(Directory.Exists(Managed(before)))Directory.Move(Managed(before),Path.Combine(dir,"old")); checkpoint?.Invoke("old-moved");
        Directory.Move(stage,Managed(before)); checkpoint?.Invoke("new-moved"); profiles.Items[profiles.Items.FindIndex(x=>x.Id==profileId)]=after; SafeFiles.Write(profilesPath,profiles); checkpoint?.Invoke("profile-committed");
        // The same verified recovery path also performs normal cleanup, preserving edits
        // made to a backup or deployment between the swap and profile commit.
        Recover(); return after;
    }
    void Recover()
    {
        RecoverCreation();
        if(!File.Exists(journalPath))return; var i=SafeFiles.Read<OperationIntent>(journalPath);
        ValidateProfileRecord(i.Before,"pending operation before state"); ValidateProfileRecord(i.After,"pending operation after state");
        ValidateFileMap(i.OldFiles,"pending operation before inventory"); ValidateFileMap(i.NewFiles,"pending operation after inventory");
        if(i.SchemaVersion!=1||!Guid.TryParseExact(i.TransactionId,"N",out _)||i.Before.Id!=i.After.Id||i.Before.GameDirectory!=i.After.GameDirectory)throw new PlatformException("Unsafe or unsupported pending operation; manual review required.");
        var profiles=Profiles(); var current=Find(profiles,i.Before.Id); if(current.GameDirectory!=i.Before.GameDirectory)throw new PlatformException("Pending operation ownership changed; manual review required."); var catalog=Catalog();
        if(!SafeFiles.Equal(Expected(i.Before,catalog),i.OldFiles)||!SafeFiles.Equal(Expected(i.After,catalog),i.NewFiles))throw new PlatformException("Pending operation metadata changed; manual review required.");
        Owner(current); Stopped(current); var dir=Transaction(current,i.TransactionId); SafeFiles.Ancestors(dir); var old=Path.Combine(dir,"old"); var stage=Path.Combine(dir,"new");
        // Validate every transaction file before restoring or cleaning anything.
        var transactionFiles=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(Directory.Exists(old)) { CheckTree(old,i.OldFiles,"Recovery backup"); foreach(var f in i.OldFiles)transactionFiles.Add("old/"+f.Key,f.Value); }
        if(Directory.Exists(stage)) { CheckTree(stage,i.NewFiles,"Recovery staging"); foreach(var f in i.NewFiles)transactionFiles.Add("new/"+f.Key,f.Value); }
        CheckTree(dir,transactionFiles,"Recovery transaction");
        if(current.Generation==i.After.Generation) { CheckTree(Managed(current),i.NewFiles,"Committed deployment"); if(Directory.Exists(old))CheckTree(old,i.OldFiles,"Recovery backup"); }
        else if(current.Generation==i.Before.Generation)
        {
            if(Directory.Exists(old)) { CheckTree(old,i.OldFiles,"Recovery backup"); if(Directory.Exists(Managed(current))) { CheckTree(Managed(current),i.NewFiles,"Interrupted deployment"); SafeFiles.DeleteTree(Managed(current)); } Directory.Move(old,Managed(current)); }
            else if(i.OldFiles.Count==0&&!Directory.Exists(stage)&&Directory.Exists(Managed(current)))
            {
                // An absent original managed tree produces no backup. Once staging
                // has moved into plugins, roll back only an exact verified new tree.
                // An originally empty tree has an empty backup and uses the branch above.
                CheckTree(Managed(current),i.NewFiles,"Interrupted initial deployment");
                SafeFiles.DeleteTree(Managed(current));
            }
            else CheckTree(Managed(current),i.OldFiles,"Original deployment");
        }
        else throw new PlatformException("Profile changed during pending operation; manual review required.");
        if(Directory.Exists(stage))CheckTree(stage,i.NewFiles,"Recovery staging"); SafeFiles.DeleteTree(dir); File.Delete(journalPath);
    }
    void RecoverCreation()
    {
        if(!File.Exists(creationPath))return;
        var intent=SafeFiles.Read<ProfileCreationIntent>(creationPath); var p=intent.Profile;
        ValidateProfileRecord(p,"profile creation intent");
        if(intent.SchemaVersion!=1||p.Mods.Count!=0||intent.CreatedDirectories==null||intent.CreatedDirectories.Count>2||intent.CreatedDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=intent.CreatedDirectories.Count||intent.CreatedDirectories.Any(x=>x is not "BepInEx" and not "BepInEx/plugins"))throw new PlatformException("Unsafe profile creation intent; manual review required.");
        Stopped(p); var profiles=Profiles(); var committed=profiles.Items.SingleOrDefault(x=>x.Id==p.Id);
        if(committed!=null)
        {
            if(committed.GameDirectory!=p.GameDirectory||committed.Generation!=p.Generation)throw new PlatformException("Profile changed during creation recovery.");
            Owner(p);
        }
        else
        {
            var markerPresent=File.Exists(Marker(p)); if(markerPresent)Owner(p);
            // Before removing even the ownership marker, reject any new content in
            // directories this interrupted creation would otherwise remove.
            foreach(var relative in intent.CreatedDirectories)
            {
                var directory=SafeFiles.Under(p.GameDirectory,relative); if(!Directory.Exists(directory))continue;
                var allowed=intent.CreatedDirectories.Where(x=>x.StartsWith(relative+"/",StringComparison.Ordinal)&&!x[(relative.Length+1)..].Contains('/')).Select(x=>SafeFiles.Under(p.GameDirectory,x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if(relative=="BepInEx"&&markerPresent)allowed.Add(Marker(p));
                if(Directory.EnumerateFileSystemEntries(directory).Any(x=>!allowed.Contains(x)))throw new PlatformException("Profile creation recovery found added content; it has been preserved for manual review.");
            }
            if(markerPresent)File.Delete(Marker(p));
            foreach(var relative in intent.CreatedDirectories.OrderByDescending(x=>x.Length)) { var directory=SafeFiles.Under(p.GameDirectory,relative); if(Directory.Exists(directory))Directory.Delete(directory,false); }
        }
        File.Delete(creationPath);
    }
    internal sealed class Ownership { [System.Text.Json.Serialization.JsonRequired] public int SchemaVersion { get; set; }=1; [System.Text.Json.Serialization.JsonRequired] public string ProfileId { get; set; }=""; [System.Text.Json.Serialization.JsonRequired] public string WorkspaceRoot { get; set; }=""; }
}
