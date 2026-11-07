using System.Text.Json;
using Sevo.Mod.Core;

var arguments=args.ToList();
string? Option(string name)
{
    var i=arguments.IndexOf(name); if(i<0)return null;
    if(i+1>=arguments.Count||arguments[i+1].StartsWith("--"))throw new PlatformException("Missing value for "+name);
    var value=arguments[i+1]; arguments.RemoveRange(i,2); return value;
}
void Help() => Console.WriteLine("""
Starship Uplink command line
Usage: sevo-mod [--root <workspace>] <command> <arguments>
  packages                          List cached packages
  import <zip> [--id ID --name NAME --version X.Y.Z --repository OWNER/REPO --asset-name FILE]
  profiles                          List profiles
  create-profile <name> <game-dir>   Claim a clean or prepared Starship EVO game
  install <profile-id> <package-id> [version]
  enable|disable|remove|rollback|pin|unpin <profile-id> <package-id>
  validate <profile-id>              Report dependencies and file integrity
  --help                            Show this help
Default workspace: ./local-data. Close the selected game before mutations.
Profile creation prepares owned plugin directories; the launcher installs BepInEx.
""");
try
{
    if(arguments.Count==0||arguments.Contains("--help")||arguments.Contains("-h")) { Help(); return 0; }
    var root=Option("--root")??Path.Combine(Environment.CurrentDirectory,"local-data");
    if(arguments.Count==0)throw new PlatformException("A command is required."); var command=arguments[0]; arguments.RemoveAt(0);
    PackageImportOptions? importOptions=null;
    if(command=="import")importOptions=new PackageImportOptions { Id=Option("--id"),Name=Option("--name"),Version=Option("--version"),Repository=Option("--repository"),AssetName=Option("--asset-name") };
    int expected=command switch { "packages" or "profiles"=>0,"import" or "validate"=>1,"create-profile" or "enable" or "disable" or "remove" or "rollback" or "pin" or "unpin"=>2,"install"=>arguments.Count==3?3:2,_=>throw new PlatformException("Unknown command. Use --help.") };
    if(arguments.Count!=expected||arguments.Any(x=>x.StartsWith("--")))throw new PlatformException("Invalid command arguments. Use --help.");
    var service=new PlatformService(root);
    object result=command switch
    {
        "packages"=>service.ListPackages(), "profiles"=>service.ListProfiles(),
        "import"=>service.ImportPackage(arguments[0],importOptions),
        "create-profile"=>service.CreateProfile(arguments[0],arguments[1]),
        "install"=>service.Install(arguments[0],arguments[1],arguments.Count==3?arguments[2]:null),
        "enable"=>service.SetEnabled(arguments[0],arguments[1],true), "disable"=>service.SetEnabled(arguments[0],arguments[1],false),
        "remove"=>service.Remove(arguments[0],arguments[1]), "rollback"=>service.Rollback(arguments[0],arguments[1]),
        "pin"=>service.SetPinned(arguments[0],arguments[1],true), "unpin"=>service.SetPinned(arguments[0],arguments[1],false),
        "validate"=>service.ValidateProfile(arguments[0]), _=>throw new PlatformException("Unknown command.")
    };
    Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase }));
    return command=="validate"&&((IReadOnlyList<string>)result).Count>0?1:0;
}
catch(Exception ex) when(ex is PlatformException or IOException or UnauthorizedAccessException or ArgumentException)
{ Console.Error.WriteLine("Error: "+ex.Message); return 2; }
